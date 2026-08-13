using System.Net;
using System.Net.Http.Headers;
using ModernDownloadManager.Core.Engine;
using ModernDownloadManager.Core.Models;

var tests = new (string Name, Func<Task> Run)[]
{
    ("segmented download merges exact ranges", TestSegmentedDownloadAsync),
    ("malformed range response is rejected", TestMalformedRangeAsync),
    ("non-resumable retry does not append duplicate data", TestNonResumableRetryAsync)
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
    const int length = 4 * 1024 * 1024;
    var bytes = CreateBytes(length);
    var directory = NewDirectory();
    try
    {
        using var client = new HttpClient(new RangeHandler(bytes));
        using var engine = new SegmentedDownloader(client);
        var item = NewItem(directory, "segmented.bin", length, segments: 2);
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
