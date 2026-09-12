using System.Collections.Concurrent;
using System.Buffers;
using System.Diagnostics;
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

    public async Task StartAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var job = item.ToJob();
        try
        {
            await StartAsync(job, cancellationToken);
        }
        finally
        {
            var updated = job.ToRecord();
            item.FileName = updated.FileName;
            item.TotalBytes = updated.TotalBytes;
            item.DownloadedBytes = updated.DownloadedBytes;
            item.State = updated.State;
            item.SupportsResume = updated.SupportsResume;
            item.ETag = updated.ETag;
            item.LastModified = updated.LastModified;
            item.CompletedAt = updated.CompletedAt;
            item.ErrorMessage = updated.ErrorMessage;
        }
    }

    private sealed class RuntimeContext
    {
        public required DownloadJob Item { get; init; }
        public required List<DownloadSegment> Segments { get; init; }
        public BandwidthLimiter? BandwidthLimiter { get; init; }
        public CancellationTokenSource Cts { get; } = new();
        public long TotalDownloadedSnapshot;
        public long LastProgressTimestamp = Stopwatch.GetTimestamp();
        public int ReportingProgress;
        public long BytesSinceLastReport;
    }

    public async Task StartAsync(DownloadJob item, CancellationToken cancellationToken = default)
    {
        var ctx = new RuntimeContext
        {
            Item = item,
            Segments = new List<DownloadSegment>(),
            BandwidthLimiter = item.Options.SpeedLimitBytesPerSecond > 0
                ? new BandwidthLimiter(item.Options.SpeedLimitBytesPerSecond)
                : null
        };
        _active[item.Id] = ctx;

        try
        {
            SetState(item, DownloadState.Connecting);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cts.Token, cancellationToken);
            var ct = linkedCts.Token;
            var previousETag = item.Runtime.ETag;
            var previousLastModified = item.Runtime.LastModified;
            var hadPartialDownload = item.Runtime.DownloadedBytes > 0 || Directory.Exists(GetTempDir(item));
            var probe = await ProbeAsync(item, ct);
            item.Runtime.TotalBytes = probe.ContentLength ?? item.Runtime.TotalBytes;
            item.Runtime.SupportsResume = probe.AcceptsRanges && item.Runtime.TotalBytes > 0;
            item.Runtime.ETag = probe.ETag;
            item.Runtime.LastModified = probe.LastModified;

            if (!item.FileNameIsExplicit && !string.IsNullOrWhiteSpace(probe.SuggestedFileName))
            {
                item.FileName = probe.SuggestedFileName;
                item.Options.Category = DownloadCategoryResolver.Resolve(item.FileName);
            }

            Directory.CreateDirectory(item.DestinationDirectory);
            var tempDir = GetTempDir(item);
            if (hadPartialDownload && ValidatorsChanged(previousETag, previousLastModified, probe.ETag, probe.LastModified))
            {
                CleanupTempDir(tempDir);
                item.Runtime.DownloadedBytes = 0;
            }
            Directory.CreateDirectory(tempDir);

            ctx.Segments.AddRange(BuildSegments(item, tempDir));
            item.Runtime.DownloadedBytes = ctx.Segments.Sum(s => s.DownloadedBytes);
            ctx.TotalDownloadedSnapshot = item.Runtime.DownloadedBytes;

            SetState(item, DownloadState.Downloading);

            await Task.WhenAll(ctx.Segments.Select(segment => DownloadSegmentAsync(item, ctx, segment, ct)));

            if (ctx.Cts.IsCancellationRequested)
                return; // paused or cancelled — state already set by PauseAsync/CancelAsync

            SetState(item, DownloadState.Merging);
            await MergeSegmentsAsync(item, ctx.Segments, ct);

            // Some servers omit Content-Length. Once the merged file exists, its
            // length is the authoritative completed size for both persistence and
            // the UI (and prevents a completed row from displaying 0 B / 0 B).
            if (File.Exists(item.FullPath))
                item.Runtime.TotalBytes = new FileInfo(item.FullPath).Length;
            item.Runtime.CompletedAt = DateTimeOffset.UtcNow;
            item.Runtime.DownloadedBytes = item.Runtime.TotalBytes;
            SetState(item, DownloadState.Completed);

            CleanupTempDir(tempDir);
        }
        catch (OperationCanceledException)
        {
            // Expected on pause/cancel — state already handled by the caller.
        }
        catch (Exception ex)
        {
            item.Runtime.ErrorMessage = ex.Message;
            SetState(item, DownloadState.Failed, ex.Message);
        }
        finally
        {
            _active.TryRemove(item.Id, out _);
            ctx.Cts.Dispose();
        }
    }

    public Task PauseAsync(Guid downloadId)
    {
        if (_active.TryGetValue(downloadId, out var ctx))
        {
            ctx.Item.Runtime.State = DownloadState.Paused;
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

    private async Task<ProbeResult> ProbeAsync(DownloadJob item, CancellationToken ct)
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

        // A few media CDNs return a misleading Content-Length on HEAD (often
        // the probe/manifest size). Prefer the authoritative total from a
        // ranged GET before trusting HEAD's Content-Length.
        long? length = primary.Content.Headers.ContentRange?.Length
                        ?? secondary?.Content.Headers.ContentRange?.Length
                        ?? primary.Content.Headers.ContentLength
                        ?? secondary?.Content.Headers.ContentLength;

        var mediaType = primary.Content.Headers.ContentType?.MediaType
                        ?? secondary?.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrEmpty(fileName))
            fileName = SynthesizeFileName(item.Url, mediaType);
        else
            fileName = EnsureMediaExtension(fileName, mediaType);

        return new ProbeResult(
            length,
            acceptsRanges,
            primary.Headers.ETag?.Tag ?? secondary?.Headers.ETag?.Tag,
            primary.Content.Headers.LastModified ?? secondary?.Content.Headers.LastModified,
            fileName);
    }

    private async Task<HttpResponseMessage> ProbeViaRangedGetAsync(DownloadJob item, CancellationToken ct)
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
        var ext = ExtensionForMediaType(mediaType) ?? "";
        return $"{host}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}";
    }

    private static string EnsureMediaExtension(string fileName, string? mediaType)
    {
        if (Path.HasExtension(fileName))
            return fileName;

        var extension = ExtensionForMediaType(mediaType);
        return extension is null ? fileName : fileName + extension;
    }

    private static string? ExtensionForMediaType(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/x-matroska" => ".mkv",
        "video/quicktime" => ".mov",
        "video/x-msvideo" => ".avi",
        "audio/mpeg" => ".mp3",
        "audio/mp4" => ".m4a",
        "application/pdf" => ".pdf",
        "application/zip" => ".zip",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        _ => null
    };

    private static string? DetectExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[4] == (byte)'f' && bytes[5] == (byte)'t' &&
            bytes[6] == (byte)'y' && bytes[7] == (byte)'p')
            return ".mp4";
        if (bytes.Length >= 4 && bytes[0] == 0x1A && bytes[1] == 0x45 &&
            bytes[2] == 0xDF && bytes[3] == 0xA3)
            return ".webm";
        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' &&
            bytes[2] == (byte)'F' && bytes[3] == (byte)'F' && bytes[8] == (byte)'A' &&
            bytes[9] == (byte)'V' && bytes[10] == (byte)'I' && bytes[11] == (byte)' ')
            return ".avi";
        return null;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');
        return name;
    }

    // --- Segment planning ---

    private List<DownloadSegment> BuildSegments(DownloadJob item, string tempDir)
    {
        var segments = new List<DownloadSegment>();

        var canSplit = item.Runtime.SupportsResume && item.Runtime.TotalBytes >= MinBytesPerSegment;
        var count = canSplit ? Math.Clamp(item.Options.SegmentCount, 1, 16) : 1;

        if (count == 1 || item.Runtime.TotalBytes <= 0)
        {
            segments.Add(new DownloadSegment
            {
                Index = 0,
                StartByte = 0,
                EndByte = item.Runtime.TotalBytes > 0 ? item.Runtime.TotalBytes - 1 : -1,
                TempFilePath = Path.Combine(tempDir, "part0.tmp")
            });
            return RestoreExistingProgress(segments);
        }

        var chunkSize = item.Runtime.TotalBytes / count;
        for (var i = 0; i < count; i++)
        {
            var start = i * chunkSize;
            var end = (i == count - 1) ? item.Runtime.TotalBytes - 1 : start + chunkSize - 1;
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

    private async Task DownloadSegmentAsync(DownloadJob item, RuntimeContext ctx, DownloadSegment segment, CancellationToken ct)
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
                if (!item.Runtime.SupportsResume && segment.DownloadedBytes > 0)
                {
                    segment.DownloadedBytes = 0;
                    using (var reset = new FileStream(segment.TempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                    }
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
                ApplyRequestHeaders(request, item);

                var expectedBytes = segment.TotalBytes - segment.DownloadedBytes;
                if (item.Runtime.SupportsResume && !segment.IsOpenEnded)
                    request.Headers.Range = new RangeHeaderValue(segment.ResumeOffset, segment.EndByte);

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var responseType = response.Content.Headers.ContentType?.MediaType;
                var expectedCategory = DownloadCategoryResolver.Resolve(item.FileName);
                if (expectedCategory is DownloadCategory.Video or DownloadCategory.Music
                    && (string.Equals(responseType, "text/html", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(responseType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("The server returned a web page instead of the requested media file. The link may have expired or require browser authorization.");
                if (!item.Runtime.SupportsResume && response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var fullRange = response.Content.Headers.ContentRange;
                    if (fullRange?.From != 0 || fullRange.Length is null || fullRange.To != fullRange.Length - 1)
                        throw new InvalidDataException("The server returned only part of the file without a requested range; the download was not completed.");
                }

                if (item.Runtime.SupportsResume && !segment.IsOpenEnded && response.StatusCode != HttpStatusCode.PartialContent)
                    throw new IOException("The server ignored the requested byte range; refusing to append potentially invalid data.");

                if (item.Runtime.SupportsResume && !segment.IsOpenEnded)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range?.From != segment.ResumeOffset || range.To != segment.EndByte || range.Length != item.Runtime.TotalBytes)
                        throw new IOException("The server returned an invalid byte range; refusing to append potentially invalid data.");
                }
                await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(
                    segment.TempFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                var bufferSize = item.Options.SpeedLimitBytesPerSecond > 0
                    ? (int)Math.Min(65536, Math.Max(1, item.Options.SpeedLimitBytesPerSecond))
                    : 65536;
                var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                try
                {
                int read;
                while ((read = await responseStream.ReadAsync(buffer.AsMemory(0, bufferSize), ct)) > 0)
                {
                    if (segment.StartByte == 0 && segment.DownloadedBytes == 0 && !Path.HasExtension(item.FileName))
                    {
                        var detectedExtension = DetectExtension(buffer.AsSpan(0, read));
                        if (detectedExtension is not null)
                        {
                            item.FileName = SanitizeFileName(item.FileName + detectedExtension);
                            item.Options.Category = DownloadCategoryResolver.Resolve(item.FileName);
                        }
                    }

                    if (ctx.BandwidthLimiter is not null)
                        await ctx.BandwidthLimiter.WaitAsync(read, ct);
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    segment.DownloadedBytes += read;
                    ReportProgress(item, ctx, read);
                }

                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }

                if (!segment.IsOpenEnded && segment.DownloadedBytes - (segment.TotalBytes - expectedBytes) != expectedBytes)
                    throw new IOException($"Segment {segment.Index} ended early; expected {expectedBytes} bytes.");

                segment.State = SegmentState.Completed;
                return;
            }
            catch (InvalidDataException)
            {
                segment.State = SegmentState.Failed;
                throw;
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

    private void ApplyRequestHeaders(HttpRequestMessage request, DownloadJob item)
    {
        // Media CDNs must not transparently gzip/br-compress a byte-range
        // response: the downloader validates offsets against the raw MP4
        // bytes, just as the browser does for a video file.
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (!string.IsNullOrEmpty(item.Options.UserAgent))
            request.Headers.UserAgent.ParseAdd(item.Options.UserAgent);
        if (!string.IsNullOrEmpty(item.Options.ReferrerUrl))
            request.Headers.Referrer = new Uri(item.Options.ReferrerUrl);
        if (!string.IsNullOrEmpty(item.Options.CookieHeader))
            request.Headers.TryAddWithoutValidation("Cookie", item.Options.CookieHeader);
        if (!string.IsNullOrEmpty(item.Runtime.ETag))
            request.Headers.TryAddWithoutValidation("If-Range", item.Runtime.ETag);
        else if (item.Runtime.LastModified is { } lastModified)
            request.Headers.TryAddWithoutValidation("If-Range", lastModified.ToUniversalTime().ToString("R"));
    }

    // --- Progress + merge ---

    private void ReportProgress(DownloadJob item, RuntimeContext ctx, int bytesJustRead)
    {
        Interlocked.Add(ref ctx.TotalDownloadedSnapshot, bytesJustRead);
        Interlocked.Add(ref ctx.BytesSinceLastReport, bytesJustRead);
        item.Runtime.DownloadedBytes = ctx.TotalDownloadedSnapshot;

        var now = Stopwatch.GetTimestamp();
        var previous = Volatile.Read(ref ctx.LastProgressTimestamp);
        if (Stopwatch.GetElapsedTime(previous, now) < ProgressReportInterval
            || Interlocked.CompareExchange(ref ctx.ReportingProgress, 1, 0) != 0) return;
        try
        {
            previous = Volatile.Read(ref ctx.LastProgressTimestamp);
            var elapsed = Stopwatch.GetElapsedTime(previous, now).TotalSeconds;
            if (elapsed < ProgressReportInterval.TotalSeconds) return;
            Volatile.Write(ref ctx.LastProgressTimestamp, now);
            var bytes = Interlocked.Exchange(ref ctx.BytesSinceLastReport, 0);
            var total = Interlocked.Read(ref ctx.TotalDownloadedSnapshot);
            var bps = bytes / elapsed;
            item.Runtime.DownloadedBytes = total;
            TimeSpan? eta = bps > 0 && item.Runtime.TotalBytes > 0
                ? TimeSpan.FromSeconds(Math.Max(0, item.Runtime.TotalBytes - total) / bps) : null;
            ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
            {
                DownloadId = item.Id, DownloadedBytes = total, TotalBytes = item.Runtime.TotalBytes,
                BytesPerSecond = bps, Eta = eta
            });
        }
        finally { Volatile.Write(ref ctx.ReportingProgress, 0); }
    }

    private async Task MergeSegmentsAsync(DownloadJob item, List<DownloadSegment> segments, CancellationToken cancellationToken)
    {
        item.FileName = GetAvailableFileName(item);
        var finalizingPath = item.FullPath + ".mdm-finalizing";
        try { if (File.Exists(finalizingPath)) File.Delete(finalizingPath); } catch { }

        {
            await using var output = new FileStream(
                finalizingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long mergedBytes = 0;
            foreach (var segment in segments.OrderBy(s => s.Index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expectedBytes = segment.IsOpenEnded ? 0 : segment.EndByte - segment.StartByte + 1;
                await using var input = new FileStream(
                    segment.TempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (!segment.IsOpenEnded && input.Length != expectedBytes)
                    throw new IOException($"Segment {segment.Index + 1} is incomplete ({input.Length:N0} of {expectedBytes:N0} bytes).");

                await input.CopyToAsync(output, 131072, cancellationToken);
                mergedBytes += input.Length;
            }
            await output.FlushAsync(cancellationToken);
            output.Flush(true);

            if (item.Runtime.TotalBytes > 0 && mergedBytes != item.Runtime.TotalBytes)
                throw new IOException($"Merged file size is {mergedBytes:N0} bytes, expected {item.Runtime.TotalBytes:N0} bytes.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Move(finalizingPath, item.FullPath, overwrite: false);
    }

    private static string GetAvailableFileName(DownloadJob item)
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

    private void SetState(DownloadJob item, DownloadState state, string? error = null)
    {
        item.Runtime.State = state;
        StateChanged?.Invoke(this, new DownloadStateChangedEventArgs
        {
            DownloadId = item.Id,
            State = state,
            ErrorMessage = error
        });
    }

    private static string GetTempDir(DownloadJob item) =>
        Path.Combine(item.DestinationDirectory, $".mdm-{item.Id:N}");

    private static bool ValidatorsChanged(string? oldETag, DateTimeOffset? oldLastModified,
        string? newETag, DateTimeOffset? newLastModified) =>
        (oldETag is not null && !string.Equals(oldETag, newETag, StringComparison.Ordinal)) ||
        (oldETag is null && oldLastModified is not null && !Nullable.Equals(oldLastModified, newLastModified));

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
