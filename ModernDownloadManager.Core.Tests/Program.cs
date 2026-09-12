using System.Net;
using System.Net.Http.Headers;
using ModernDownloadManager.Core.Engine;
using ModernDownloadManager.Core.Models;

if (args.Contains("--benchmark"))
{
    var payload = CreateBytes(32 * 1024 * 1024);
    for (var run = 0; run < 6; run++)
    {
        var directory = NewDirectory();
        try
        {
            using var client = new HttpClient(new RangeHandler(payload));
            using var engine = new SegmentedDownloader(client);
            var item = NewItem(directory, "benchmark.bin", payload.Length, segments: 8);
            var updates = 0;
            engine.ProgressChanged += (_, _) => Interlocked.Increment(ref updates);
            var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            var allocated = GC.GetTotalAllocatedBytes(true);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await engine.StartAsync(item);
            timer.Stop();
            Assert(item.State == DownloadState.Completed, item.ErrorMessage ?? "benchmark failed");
            Console.WriteLine($"BENCH run={run} wall_ms={timer.Elapsed.TotalMilliseconds:0} cpu_ms={(System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime-cpu).TotalMilliseconds:0} allocated_bytes={GC.GetTotalAllocatedBytes(true)-allocated} updates={updates}");
            Assert(File.ReadAllBytes(item.FullPath).SequenceEqual(payload), "benchmark bytes differ");
        }
        finally { DeleteDirectory(directory); }
    }
    return 0;
}
var tests = new (string Name, Func<Task> Run)[]
{
    ("HTML response is not saved as a completed media file", TestMediaErrorPageAsync),
    ("capture handoff waits for acceptance and preserves metadata", TestCaptureHandoffAsync),
    ("segmented download merges exact ranges", TestSegmentedDownloadAsync),
    ("malformed range response is rejected", TestMalformedRangeAsync),
    ("non-resumable retry does not append duplicate data", TestNonResumableRetryAsync),
    ("download lifecycle rejects invalid transitions", TestDownloadLifecycleAsync),
    ("unknown content length downloads successfully", TestUnknownLengthAsync),
    ("download job round trips its record projection", TestDownloadJobRoundTripAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"FAIL {test.Name}: {ex.Message}");
        Console.WriteLine(failures[^1]);
    }
}

return failures.Count == 0 ? 0 : 1;

static async Task TestSegmentedDownloadAsync()
{
    const int length = 16 * 1024 * 1024;
    var bytes = CreateBytes(length);
    var directory = NewDirectory();
    try
    {
        using var client = new HttpClient(new RangeHandler(bytes));
        using var engine = new SegmentedDownloader(client);
        var item = NewItem(directory, "segmented.bin", length, segments: 4);
        await engine.StartAsync(item);
        Assert(item.State == DownloadState.Completed, $"state was {item.State}: {item.ErrorMessage}");
        Assert(File.ReadAllBytes(item.FullPath).SequenceEqual(bytes), "merged bytes differ");
    }
    finally { DeleteDirectory(directory); }
}

static async Task TestMalformedRangeAsync()
{
    const int length = 4 * 1024 * 1024;
    var directory = NewDirectory();
    try
    {
        using var client = new HttpClient(new RangeHandler(CreateBytes(length), malformedRange: true));
        using var engine = new SegmentedDownloader(client);
        var item = NewItem(directory, "invalid.bin", length, segments: 2);
        await engine.StartAsync(item);
        Assert(item.State == DownloadState.Failed, $"state was {item.State}");
        Assert(!File.Exists(item.FullPath), "invalid response created a final file");
    }
    finally { DeleteDirectory(directory); }
}

static async Task TestNonResumableRetryAsync()
{
    var expected = CreateBytes(8192);
    var directory = NewDirectory();
    try
    {
        using var client = new HttpClient(new RetryHandler(expected));
        using var engine = new SegmentedDownloader(client);
        var item = NewItem(directory, "retry.bin", expected.Length, segments: 1);
        await engine.StartAsync(item);
        Assert(item.State == DownloadState.Completed, $"state was {item.State}: {item.ErrorMessage}");
        Assert(File.ReadAllBytes(item.FullPath).SequenceEqual(expected), "retry appended duplicate data");
    }
    finally { DeleteDirectory(directory); }
}

static Task TestDownloadLifecycleAsync()
{
    Assert(DownloadLifecycle.CanTransition(DownloadState.Queued, DownloadState.Connecting),
        "queued should transition to connecting");
    Assert(DownloadLifecycle.CanTransition(DownloadState.Cancelled, DownloadState.Queued),
        "cancelled should be resumable");
    Assert(!DownloadLifecycle.CanTransition(DownloadState.Completed, DownloadState.Downloading),
        "completed should not transition back to downloading");

    try
    {
        DownloadLifecycle.EnsureCanTransition(DownloadState.Completed, DownloadState.Paused);
        throw new InvalidOperationException("invalid transition was accepted");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("Completed -> Paused"))
    {
        return Task.CompletedTask;
    }
}

static async Task TestUnknownLengthAsync()
{
    var expected = CreateBytes(8192);
    var directory = NewDirectory();
    try
    {
        using var client = new HttpClient(new UnknownLengthHandler(expected));
        using var engine = new SegmentedDownloader(client);
        var item = NewItem(directory, "unknown.bin", 0, segments: 4);
        await engine.StartAsync(item);
        Assert(item.State == DownloadState.Completed, $"state was {item.State}: {item.ErrorMessage}");
        Assert(File.ReadAllBytes(item.FullPath).SequenceEqual(expected), "unknown-length bytes differ");
        Assert(item.TotalBytes == expected.Length, "completed size was not discovered");
    }
    finally { DeleteDirectory(directory); }
}

static Task TestDownloadJobRoundTripAsync()
{
    var original = new DownloadItem
    {
        Url = "https://example.test/file.bin",
        FileName = "file.bin",
        FileNameIsExplicit = true,
        DestinationDirectory = Path.Combine(Path.GetTempPath(), "downloads"),
        TotalBytes = 100,
        DownloadedBytes = 42,
        State = DownloadState.Paused,
        Category = DownloadCategory.General,
        SupportsResume = true,
        ETag = "etag",
        SegmentCount = 4,
        SpeedLimitBytesPerSecond = 1234,
        ReferrerUrl = "https://example.test",
        UserAgent = "test-agent"
    };

    var roundTripped = original.ToJob().ToRecord();
    Assert(roundTripped.Url == original.Url && roundTripped.FileName == original.FileName,
        "request fields did not round-trip");
    Assert(roundTripped.DownloadedBytes == original.DownloadedBytes && roundTripped.State == original.State,
        "runtime fields did not round-trip");
    Assert(roundTripped.SegmentCount == original.SegmentCount &&
           roundTripped.SpeedLimitBytesPerSecond == original.SpeedLimitBytesPerSecond,
        "option fields did not round-trip");
    return Task.CompletedTask;
}

static DownloadItem NewItem(string directory, string fileName, long totalBytes, int segments) => new()
{
    Url = "https://example.test/download",
    FileName = fileName,
    FileNameIsExplicit = true,
    DestinationDirectory = directory,
    TotalBytes = totalBytes,
    SegmentCount = segments
};

static byte[] CreateBytes(int length)
{
    var bytes = new byte[length];
    for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i % 251);
    return bytes;
}

static string NewDirectory()
{
    var path = Path.Combine(Path.GetTempPath(), "mdm-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void DeleteDirectory(string path)
{
    try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task TestCaptureHandoffAsync()
{
    var pipe = "MDM.Test." + Guid.NewGuid().ToString("N");
    using var stop = new CancellationTokenSource();
    var entered = new TaskCompletionSource<ModernDownloadManager.Core.Models.DownloadRequestMessage>();
    var decision = new TaskCompletionSource<bool>();
    var server = ModernDownloadManager.Core.Ipc.DownloadPipe.RunConfirmedServerAsync(request =>
    {
        entered.TrySetResult(request);
        return request.Url.EndsWith("declined") ? Task.FromResult(false) : decision.Task;
    }, stop.Token, pipe);
    try
    {
        var request = new ModernDownloadManager.Core.Models.DownloadRequestMessage
        {
            Url = "https://example.test/file", SuggestedFileName = "sample.zip",
            TotalBytes = 12345, Cookie = "test=value", Referrer = "https://example.test/", UserAgent = "TestAgent"
        };
        var pending = ModernDownloadManager.Core.Ipc.DownloadPipe.TryConfirmedCaptureAsync(request, TimeSpan.FromSeconds(2), pipe);
        var received = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(!pending.IsCompleted, "acknowledged before user decision");
        Assert(received.TotalBytes == request.TotalBytes && received.SuggestedFileName == request.SuggestedFileName
            && received.Cookie == request.Cookie && received.Referrer == request.Referrer && received.UserAgent == request.UserAgent,
            "capture metadata was lost");
        // A second prompt must not be blocked behind the first user's decision.
        var declined = await ModernDownloadManager.Core.Ipc.DownloadPipe.TryConfirmedCaptureAsync(
            new() { Url = "https://example.test/declined" }, TimeSpan.FromSeconds(2), pipe).WaitAsync(TimeSpan.FromSeconds(5));
        Assert(declined == "declined", "declined capture was accepted");
        decision.SetResult(true);
        Assert(await pending.WaitAsync(TimeSpan.FromSeconds(5)) == "queued", "accepted capture was not acknowledged");
    }
    finally { decision.TrySetResult(false); stop.Cancel(); await server; }
}

static async Task TestMediaErrorPageAsync()
{
    var directory = NewDirectory();
    try
    {
        using var client = new HttpClient(new MediaErrorPageHandler());
        using var engine = new SegmentedDownloader(client);
        var item = NewItem(directory, "sample.mp4", 0, segments: 1);
        await engine.StartAsync(item);
        Assert(item.State == DownloadState.Failed, "HTML response was marked complete");
        Assert(!File.Exists(item.FullPath), "error page was saved as media");
        Assert(item.ErrorMessage?.Contains("web page") == true, "missing actionable error");
    }
    finally { DeleteDirectory(directory); }
}

sealed class MediaErrorPageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Session expired</body></html>", System.Text.Encoding.UTF8, "text/html")
        });
}

sealed class RangeHandler(byte[] payload, bool malformedRange = false) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Head)
            return Task.FromResult(Response(HttpStatusCode.OK, Array.Empty<byte>(), payload.Length, acceptsRanges: true));

        var range = request.Headers.Range?.Ranges.SingleOrDefault();
        if (range is null) return Task.FromResult(Response(HttpStatusCode.OK, payload, payload.Length, acceptsRanges: false));
        var from = range.From!.Value;
        var to = range.To!.Value;
        var actualFrom = malformedRange ? from + 1 : from;
        var body = payload[(int)from..((int)to + 1)];
        var response = Response(HttpStatusCode.PartialContent, body, payload.Length, acceptsRanges: true);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(actualFrom, to, payload.Length);
        return Task.FromResult(response);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body, long length, bool acceptsRanges)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentLength = body.Length == 0 ? length : body.Length;
        if (acceptsRanges) response.Headers.AcceptRanges.Add("bytes");
        response.Headers.ETag = new EntityTagHeaderValue("\"test\"");
        return response;
    }
}

sealed class RetryHandler(byte[] payload) : HttpMessageHandler
{
    private int _calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Head)
        {
            var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            head.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(head);
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        if (Interlocked.Increment(ref _calls) == 1)
            response.Content = new ThrowingContent(payload, 1000);
        return Task.FromResult(response);
    }
}

sealed class UnknownLengthHandler(byte[] payload) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Head)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
    }
}

sealed class ThrowingContent(byte[] payload, int bytesBeforeFailure) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
    protected override bool TryComputeLength(out long length) { length = payload.Length; return true; }
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new FailingStream(payload, bytesBeforeFailure));
}

sealed class FailingStream(byte[] payload, int bytesBeforeFailure) : MemoryStream(payload)
{
    private int _read;
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_read >= bytesBeforeFailure) throw new IOException("simulated interruption");
        var allowed = Math.Min(count, bytesBeforeFailure - _read);
        var read = base.Read(buffer, offset, allowed);
        _read += read;
        return read;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        new(Read(buffer.Span));
}
