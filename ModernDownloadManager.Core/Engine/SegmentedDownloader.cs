using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using ModernDownloadManager.Core.Models;

namespace ModernDownloadManager.Core.Engine;

/// <summary>
/// Multi-connection download engine, the core speed advantage IDM/FDM-style
/// managers have over the browser's built-in downloader: it splits a file into
/// N byte-range chunks, fetches them in parallel over separate connections, and
/// reassembles them on completion. Falls back to a single stream automatically
/// when the server doesn't advertise Accept-Ranges or the size is unknown.
/// </summary>
public sealed class SegmentedDownloader : IDownloadEngine, IDisposable
{
    private const int MinBytesPerSegment = 4 * 1024 * 1024; // don't bother splitting under 4MB
    private const int MaxRetriesPerSegment = 3;
    private static readonly TimeSpan ProgressReportInterval = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<Guid, RuntimeContext> _active = new();

    public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    public event EventHandler<DownloadStateChangedEventArgs>? StateChanged;

    public SegmentedDownloader(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 16,
            AutomaticDecompression = DecompressionMethods.All
        });
    }

    private sealed class RuntimeContext
    {
        public required DownloadItem Item { get; init; }
        public required List<DownloadSegment> Segments { get; init; }
        public CancellationTokenSource Cts { get; } = new();
        public long TotalDownloadedSnapshot;
        public DateTime LastProgressReport = DateTime.MinValue;
        public long BytesSinceLastReport;
    }

    public async Task StartAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        var ctx = new RuntimeContext { Item = item, Segments = new List<DownloadSegment>() };
        _active[item.Id] = ctx;

        try
        {
            SetState(item, DownloadState.Connecting);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cts.Token, cancellationToken);
            var ct = linkedCts.Token;
            var probe = await ProbeAsync(item, ct);
            item.TotalBytes = probe.ContentLength ?? item.TotalBytes;
            item.SupportsResume = probe.AcceptsRanges;
            item.ETag = probe.ETag;
            item.LastModified = probe.LastModified;

            if (!item.FileNameIsExplicit && !string.IsNullOrWhiteSpace(probe.SuggestedFileName))
            {
                item.FileName = probe.SuggestedFileName;
                item.Category = DownloadCategoryResolver.Resolve(item.FileName);
            }

            Directory.CreateDirectory(item.DestinationDirectory);
            var tempDir = GetTempDir(item);
            Directory.CreateDirectory(tempDir);

            ctx.Segments.AddRange(BuildSegments(item, tempDir));
            item.DownloadedBytes = ctx.Segments.Sum(s => s.DownloadedBytes);
            ctx.TotalDownloadedSnapshot = item.DownloadedBytes;

            SetState(item, DownloadState.Downloading);

            var maxParallel = Math.Max(1, ctx.Segments.Count);
            using var gate = new SemaphoreSlim(maxParallel);
            var tasks = ctx.Segments.Select(async segment =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    await DownloadSegmentAsync(item, ctx, segment, ct);
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks);

            if (ctx.Cts.IsCancellationRequested)
                return; // paused or cancelled — state already set by PauseAsync/CancelAsync

            SetState(item, DownloadState.Merging);
            await MergeSegmentsAsync(item, ctx.Segments);

            item.CompletedAt = DateTimeOffset.UtcNow;
            item.DownloadedBytes = item.TotalBytes;
            SetState(item, DownloadState.Completed);

            CleanupTempDir(tempDir);
        }
        catch (OperationCanceledException)
        {
            // Expected on pause/cancel — state already handled by the caller.
        }
        catch (Exception ex)
        {
            item.ErrorMessage = ex.Message;
            SetState(item, DownloadState.Failed, ex.Message);
        }
        finally
        {
            _active.TryRemove(item.Id, out _);
        }
    }

    public Task PauseAsync(Guid downloadId)
    {
        if (_active.TryGetValue(downloadId, out var ctx))
        {
            ctx.Item.State = DownloadState.Paused;
            SetState(ctx.Item, DownloadState.Paused);
            ctx.Cts.Cancel();
        }
        return Task.CompletedTask;
    }

    public Task CancelAsync(Guid downloadId)
    {
        if (_active.TryGetValue(downloadId, out var ctx))
        {
            SetState(ctx.Item, DownloadState.Cancelled);
            ctx.Cts.Cancel();
            CleanupTempDir(GetTempDir(ctx.Item));
        }
        return Task.CompletedTask;
    }

    // --- Probing ---

    private readonly record struct ProbeResult(
        long? ContentLength, bool AcceptsRanges, string? ETag, DateTimeOffset? LastModified, string? SuggestedFileName);

    private async Task<ProbeResult> ProbeAsync(DownloadItem item, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, item.Url);
        ApplyRequestHeaders(request, item);

        HttpResponseMessage? headResponse = null;
        try
        {
            headResponse = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!headResponse.IsSuccessStatusCode)
            {
                headResponse.Dispose();
                headResponse = null;
            }
        }
        catch (HttpRequestException)
        {
            headResponse = null; // some servers reject HEAD outright
        }

        var fileName = headResponse is not null ? ExtractFileName(headResponse, item.Url) : null;

        // Some servers (video CDNs and download-proxy endpoints in particular) only
        // set Content-Disposition on a real GET, not on HEAD or a 0-byte range probe.
        // If HEAD didn't give us a usable name, spend one more ranged-GET request to check.
        var needsSecondProbe = headResponse is null || string.IsNullOrEmpty(fileName);
        HttpResponseMessage? getResponse = null;
        if (needsSecondProbe)
        {
            getResponse = await ProbeViaRangedGetAsync(item, ct);
            fileName ??= ExtractFileName(getResponse, item.Url);
        }

        using var primary = headResponse ?? getResponse!;
        using var secondary = headResponse is not null ? getResponse : null;

        var acceptsRanges = primary.Headers.AcceptRanges.Contains("bytes")
                             || primary.StatusCode == HttpStatusCode.PartialContent
                             || (secondary?.StatusCode == HttpStatusCode.PartialContent);

        long? length = primary.Content.Headers.ContentRange?.Length
                        ?? primary.Content.Headers.ContentLength
                        ?? secondary?.Content.Headers.ContentRange?.Length
                        ?? secondary?.Content.Headers.ContentLength;

        if (string.IsNullOrEmpty(fileName))
            fileName = SynthesizeFileName(item.Url, primary.Content.Headers.ContentType?.MediaType);

        return new ProbeResult(
            length,
            acceptsRanges,
            primary.Headers.ETag?.Tag ?? secondary?.Headers.ETag?.Tag,
            primary.Content.Headers.LastModified ?? secondary?.Content.Headers.LastModified,
            fileName);
    }

    private async Task<HttpResponseMessage> ProbeViaRangedGetAsync(DownloadItem item, CancellationToken ct)
    {
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, item.Url);
        ApplyRequestHeaders(getRequest, item);
        getRequest.Headers.Range = new RangeHeaderValue(0, 0);
        return await _http.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Filenames that are clearly a route/endpoint name, not a real
    /// file — some download-proxy backends echo these literally when the real
    /// name wasn't passed through, which is worse than falling back further.</summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "get", "download", "file", "index", "dl", "attachment", "output", "stream"
    };

    /// <summary>
    /// Prefers the server-declared filename (Content-Disposition), then a
    /// filename-shaped query parameter (?filename=, ?file=, ?name=, ?title=),
    /// then the last path segment of the *final* URL (post-redirect). Rejects
    /// generic route-like names ("get", "download", ...) at every step rather
    /// than trusting them, since those come from a proxy echoing its own
    /// endpoint name instead of the real file.
    /// </summary>
    private static string? ExtractFileName(HttpResponseMessage response, string originalUrl)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var fromHeader = disposition?.FileNameStar ?? disposition?.FileName;
        if (!string.IsNullOrWhiteSpace(fromHeader))
        {
            var candidate = SanitizeFileName(fromHeader.Trim('"'));
            if (!IsGeneric(candidate))
                return candidate;
        }

        var finalUri = response.RequestMessage?.RequestUri;
        foreach (var uri in new[] { finalUri, TryParse(originalUrl) })
        {
            if (uri is null) continue;

            var fromQuery = ExtractFromQueryString(uri);
            if (!string.IsNullOrEmpty(fromQuery))
                return fromQuery;

            var lastSegment = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(lastSegment) && Path.HasExtension(lastSegment) && !IsGeneric(Path.GetFileNameWithoutExtension(lastSegment)))
                return SanitizeFileName(lastSegment);
        }

        return null;
    }

    private static string? ExtractFromQueryString(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query)) return null;

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        foreach (var key in new[] { "filename", "file", "name", "title" })
        {
            var value = query[key];
            if (!string.IsNullOrWhiteSpace(value) && Path.HasExtension(value) && !IsGeneric(Path.GetFileNameWithoutExtension(value)))
                return SanitizeFileName(value);
        }
        return null;
    }

    private static bool IsGeneric(string nameWithoutExtension) => GenericNames.Contains(nameWithoutExtension);

    private static Uri? TryParse(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null;

    /// <summary>Last resort: host name + timestamp + an extension guessed from
    /// Content-Type, e.g. "cdn23-video-20260804-201530.mp4" — far more useful
    /// than a literal "get" with no extension at all.</summary>
    private static string SynthesizeFileName(string url, string? mediaType)
    {
        var host = TryParse(url)?.Host.Split('.').FirstOrDefault() ?? "download";
        var ext = mediaType switch
        {
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/x-matroska" => ".mkv",
            "audio/mpeg" => ".mp3",
            "audio/mp4" => ".m4a",
            "application/pdf" => ".pdf",
            "application/zip" => ".zip",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            _ => ""
        };
        return $"{host}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}";
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name;
    }

    // --- Segment planning ---

    private List<DownloadSegment> BuildSegments(DownloadItem item, string tempDir)
    {
        var segments = new List<DownloadSegment>();

        var canSplit = item.SupportsResume && item.TotalBytes >= MinBytesPerSegment;
        var count = canSplit ? Math.Clamp(item.SegmentCount, 1, 16) : 1;

        if (count == 1 || item.TotalBytes <= 0)
        {
            segments.Add(new DownloadSegment
            {
                Index = 0,
                StartByte = 0,
                EndByte = Math.Max(0, item.TotalBytes - 1),
                TempFilePath = Path.Combine(tempDir, "part0.tmp")
            });
            return RestoreExistingProgress(segments);
        }

        var chunkSize = item.TotalBytes / count;
        for (var i = 0; i < count; i++)
        {
            var start = i * chunkSize;
            var end = (i == count - 1) ? item.TotalBytes - 1 : start + chunkSize - 1;
            segments.Add(new DownloadSegment
            {
                Index = i,
                StartByte = start,
                EndByte = end,
                TempFilePath = Path.Combine(tempDir, $"part{i}.tmp")
            });
        }

        return RestoreExistingProgress(segments);
    }

    /// <summary>If temp files already exist from a prior run (resume), pick up where they left off.</summary>
    private List<DownloadSegment> RestoreExistingProgress(List<DownloadSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (File.Exists(segment.TempFilePath))
            {
                var existingBytes = new FileInfo(segment.TempFilePath).Length;
                segment.DownloadedBytes = Math.Min(existingBytes, segment.TotalBytes);
            }
        }
        return segments;
    }

    // --- Per-segment download ---

    private async Task DownloadSegmentAsync(DownloadItem item, RuntimeContext ctx, DownloadSegment segment, CancellationToken ct)
    {
        if (segment.IsComplete)
        {
            segment.State = SegmentState.Completed;
            return;
        }

        segment.State = SegmentState.Downloading;

        for (var attempt = 1; attempt <= MaxRetriesPerSegment; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
                ApplyRequestHeaders(request, item);

                if (item.SupportsResume)
                    request.Headers.Range = new RangeHeaderValue(segment.ResumeOffset, segment.EndByte);

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                if (item.SupportsResume && response.StatusCode != HttpStatusCode.PartialContent)
                    throw new IOException("The server ignored the requested byte range; refusing to append potentially invalid data.");

                await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(
                    segment.TempFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);

                var throttled = new ThrottledStream(fileStream, item.SpeedLimitBytesPerSecond);
                var buffer = new byte[81920];
                int read;
                while ((read = await responseStream.ReadAsync(buffer, ct)) > 0)
                {
                    await throttled.WriteAsync(buffer.AsMemory(0, read), ct);
                    segment.DownloadedBytes += read;
                    ReportProgress(item, ctx, read);
                }

                segment.State = SegmentState.Completed;
                return;
            }
            catch (OperationCanceledException)
            {
                segment.State = SegmentState.Paused;
                throw;
            }
            catch (Exception) when (attempt < MaxRetriesPerSegment)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct); // simple backoff
            }
        }

        segment.State = SegmentState.Failed;
        throw new IOException($"Segment {segment.Index} failed after {MaxRetriesPerSegment} attempts.");
    }

    private void ApplyRequestHeaders(HttpRequestMessage request, DownloadItem item)
    {
        if (!string.IsNullOrEmpty(item.UserAgent))
            request.Headers.UserAgent.ParseAdd(item.UserAgent);
        if (!string.IsNullOrEmpty(item.ReferrerUrl))
            request.Headers.Referrer = new Uri(item.ReferrerUrl);
        if (!string.IsNullOrEmpty(item.CookieHeader))
            request.Headers.TryAddWithoutValidation("Cookie", item.CookieHeader);
        if (!string.IsNullOrEmpty(item.ETag))
            request.Headers.TryAddWithoutValidation("If-Range", item.ETag);
        else if (item.LastModified is { } lastModified)
            request.Headers.TryAddWithoutValidation("If-Range", lastModified.ToUniversalTime().ToString("R"));
    }

    // --- Progress + merge ---

    private void ReportProgress(DownloadItem item, RuntimeContext ctx, int bytesJustRead)
    {
        Interlocked.Add(ref ctx.TotalDownloadedSnapshot, bytesJustRead);
        Interlocked.Add(ref ctx.BytesSinceLastReport, bytesJustRead);
        item.DownloadedBytes = ctx.TotalDownloadedSnapshot;

        var now = DateTime.UtcNow;
        if (now - ctx.LastProgressReport < ProgressReportInterval)
            return;

        var elapsed = (now - ctx.LastProgressReport).TotalSeconds;
        var bps = elapsed > 0 ? ctx.BytesSinceLastReport / elapsed : 0;
        ctx.BytesSinceLastReport = 0;
        ctx.LastProgressReport = now;

        var remaining = item.TotalBytes - item.DownloadedBytes;
        TimeSpan? eta = bps > 0 ? TimeSpan.FromSeconds(remaining / bps) : null;

        ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
        {
            DownloadId = item.Id,
            DownloadedBytes = item.DownloadedBytes,
            TotalBytes = item.TotalBytes,
            BytesPerSecond = bps,
            Eta = eta
        });
    }

    private async Task MergeSegmentsAsync(DownloadItem item, List<DownloadSegment> segments)
    {
        item.FileName = GetAvailableFileName(item);
        await using var output = new FileStream(item.FullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        foreach (var segment in segments.OrderBy(s => s.Index))
        {
            await using var input = new FileStream(segment.TempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await input.CopyToAsync(output);
        }
    }

    private static string GetAvailableFileName(DownloadItem item)
    {
        if (!File.Exists(item.FullPath))
            return item.FileName;

        var baseName = Path.GetFileNameWithoutExtension(item.FileName);
        var extension = Path.GetExtension(item.FileName);
        for (var i = 1; i < int.MaxValue; i++)
        {
            var candidate = $"{baseName} ({i}){extension}";
            if (!File.Exists(Path.Combine(item.DestinationDirectory, candidate)))
                return candidate;
        }

        throw new IOException("Could not choose a non-conflicting output filename.");
    }

    private void SetState(DownloadItem item, DownloadState state, string? error = null)
    {
        item.State = state;
        StateChanged?.Invoke(this, new DownloadStateChangedEventArgs
        {
            DownloadId = item.Id,
            State = state,
            ErrorMessage = error
        });
    }

    private static string GetTempDir(DownloadItem item) =>
        Path.Combine(item.DestinationDirectory, $".mdm-{item.Id:N}");

    private static void CleanupTempDir(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked file here shouldn't fail the whole download.
        }
    }

    public void Dispose() => _http.Dispose();
}
