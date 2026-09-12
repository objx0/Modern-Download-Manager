using System.Collections.Concurrent;
using ModernDownloadManager.Core.Engine;
using ModernDownloadManager.Core.Models;
using ModernDownloadManager.Core.Persistence;

namespace ModernDownloadManager.Core.Scheduling;

/// <summary>
/// The single object the UI (or native-messaging host) talks to. Owns the
/// queue, enforces "max simultaneous downloads", and keeps the repository
/// in sync so state survives app restarts.
/// </summary>
public sealed class DownloadQueueManager : IDownloadManager, IAsyncDisposable
{
    private readonly IDownloadEngine _engine;
    private readonly IDownloadRepository _repository;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly ConcurrentDictionary<Guid, DownloadItem> _items = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _itemCancellation = new();
    private readonly ConcurrentDictionary<Guid, Task> _runningTasks = new();
    private readonly ConcurrentDictionary<Guid, byte> _starting = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _lifecycleLocks = new();

    public int MaxConcurrentDownloads { get; }

    public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    public event EventHandler<DownloadStateChangedEventArgs>? StateChanged;

    /// <summary>Fired whenever a new item enters the queue — including from the
    /// browser extension via the local pipe, not just the UI's own Add button —
    /// so any listening UI can add a view model for it regardless of source.</summary>
    public event EventHandler<DownloadItem>? ItemAdded;

    public DownloadQueueManager(IDownloadEngine engine, IDownloadRepository repository, int maxConcurrentDownloads = 3)
    {
        _engine = engine;
        _repository = repository;
        MaxConcurrentDownloads = maxConcurrentDownloads;
        _concurrencyGate = new SemaphoreSlim(maxConcurrentDownloads);

        _engine.ProgressChanged += (s, e) => ProgressChanged?.Invoke(this, e);
        _engine.ProgressChanged += (s, e) =>
        {
            if (_items.TryGetValue(e.DownloadId, out var item))
            {
                item.DownloadedBytes = e.DownloadedBytes;
                item.TotalBytes = e.TotalBytes;
            }
        };
        _engine.StateChanged += (s, e) =>
        {
            if (_items.TryGetValue(e.DownloadId, out var item))
            {
                item.State = e.State;
                item.ErrorMessage = e.ErrorMessage;
            }
            StateChanged?.Invoke(this, e);
            if (_items.TryGetValue(e.DownloadId, out var persistedItem))
                _ = PersistStateSafelyAsync(persistedItem);
        };
    }

    public async Task LoadFromDiskAsync()
    {
        foreach (var item in await _repository.GetAllAsync())
        {
            // A process can be interrupted after the final file is written but
            // before the persisted state changes from Paused. The final file is
            // authoritative in that case.
            if (item.State is not DownloadState.Completed and not DownloadState.Cancelled &&
                item.TotalBytes > 0 && item.DownloadedBytes >= item.TotalBytes &&
                File.Exists(item.FullPath) && new FileInfo(item.FullPath).Length >= item.TotalBytes)
            {
                TransitionTo(item, DownloadState.Completed, persist: false);
                item.DownloadedBytes = item.TotalBytes;
                item.CompletedAt ??= File.GetLastWriteTimeUtc(item.FullPath);
            }

            // Anything that was mid-flight when the app last closed comes back as Paused,
            // not Downloading — we never resume network activity without the user asking.
            if (item.State is DownloadState.Downloading or DownloadState.Connecting or DownloadState.Merging)
                TransitionTo(item, DownloadState.Paused, persist: false);

            _items[item.Id] = item;
            _itemCancellation[item.Id] = new CancellationTokenSource();
            _lifecycleLocks[item.Id] = new SemaphoreSlim(1, 1);
            if (item.State is DownloadState.Completed or DownloadState.Cancelled)
                CleanupTempFiles(item);
            await _repository.UpsertAsync(item);
        }
    }

    public IReadOnlyCollection<DownloadItem> Items => _items.Values.ToList();

    public async Task<DownloadItem> EnqueueAsync(
        string url,
        string destinationDirectory,
        string? suggestedFileName = null,
        long speedLimitBytesPerSecond = 0,
        int segmentCount = 8,
        string? referrer = null,
        string? cookie = null,
        string? userAgent = null,
        DownloadCategory? category = null,
        bool startImmediately = true)
    {
        return await EnqueueAsync(url, new DownloadOptions
        {
            DestinationDirectory = destinationDirectory,
            SuggestedFileName = suggestedFileName,
            SpeedLimitBytesPerSecond = speedLimitBytesPerSecond,
            SegmentCount = segmentCount,
            ReferrerUrl = referrer,
            CookieHeader = cookie,
            UserAgent = userAgent,
            Category = category,
            StartImmediately = startImmediately
        });
    }

    public async Task<DownloadItem> EnqueueAsync(string url, DownloadOptions options)
    {
        if (options is null)
            throw new ArgumentNullException(nameof(options));

        // Only trust a URL-path-derived guess if it actually looks like a filename
        // (has an extension) — otherwise leave it for the engine to fill in from
        // Content-Disposition/redirect once headers arrive, instead of showing
        // something like "get" or "download" from a redirect endpoint.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Only HTTP and HTTPS download URLs are supported.", nameof(url));

        if (string.IsNullOrWhiteSpace(options.DestinationDirectory))
            throw new ArgumentException("A destination directory is required.", nameof(options));

        var destinationDirectory = Path.GetFullPath(options.DestinationDirectory);
        var urlGuess = Path.GetFileName(parsedUrl.LocalPath);
        var looksLikeRealFileName = !string.IsNullOrWhiteSpace(urlGuess) && Path.HasExtension(urlGuess);

        var suggestedFileName = SanitizeFileName(options.SuggestedFileName);

        var fileName = suggestedFileName
            ?? (looksLikeRealFileName ? urlGuess : "Fetching name…");

        var item = new DownloadItem
        {
            Url = url,
            FileName = fileName,
            FileNameIsExplicit = suggestedFileName is not null,
            DestinationDirectory = destinationDirectory,
            Category = options.Category ?? DownloadCategoryResolver.Resolve(fileName),
            State = options.StartImmediately ? DownloadState.Queued : DownloadState.Paused,
            SpeedLimitBytesPerSecond = options.SpeedLimitBytesPerSecond,
            SegmentCount = options.SegmentCount,
            ReferrerUrl = options.ReferrerUrl,
            CookieHeader = options.CookieHeader,
            UserAgent = options.UserAgent
        };

        _items[item.Id] = item;
        _itemCancellation[item.Id] = new CancellationTokenSource();
        _lifecycleLocks[item.Id] = new SemaphoreSlim(1, 1);
        await _repository.UpsertAsync(item);
        ItemAdded?.Invoke(this, item);

        if (options.StartImmediately)
            StartQueuedRun(item);
        return item;
    }

    private void StartQueuedRun(DownloadItem item)
    {
        var run = RunWhenSlotAvailableAsync(item);
        _runningTasks[item.Id] = run;
        _ = run.ContinueWith(completedTask => _runningTasks.TryRemove(item.Id, out var removed),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static string? SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        fileName = Path.GetFileName(fileName.Trim());
        foreach (var invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ? null : fileName;
    }

    private async Task RunWhenSlotAvailableAsync(DownloadItem item)
    {
        if (!_starting.TryAdd(item.Id, 0))
            return;

        if (!_itemCancellation.TryGetValue(item.Id, out var itemCts))
        {
            _starting.TryRemove(item.Id, out _);
            return;
        }

        try
        {
            await _concurrencyGate.WaitAsync(itemCts.Token);
        }
        catch (OperationCanceledException)
        {
            _starting.TryRemove(item.Id, out _);
            return;
        }

        try
        {
            if (itemCts.IsCancellationRequested || item.State != DownloadState.Queued)
                return;

            var job = item.ToJob();
            await _engine.StartAsync(job, itemCts.Token);
            var completedRecord = job.ToRecord();
            item.FileName = completedRecord.FileName;
            item.TotalBytes = completedRecord.TotalBytes;
            item.DownloadedBytes = completedRecord.DownloadedBytes;
            item.State = completedRecord.State;
            item.SupportsResume = completedRecord.SupportsResume;
            item.ETag = completedRecord.ETag;
            item.LastModified = completedRecord.LastModified;
            item.CompletedAt = completedRecord.CompletedAt;
            item.ErrorMessage = completedRecord.ErrorMessage;
            await _repository.UpsertAsync(item);
        }
        finally
        {
            _concurrencyGate.Release();
            _starting.TryRemove(item.Id, out _);
        }
    }

    public async Task PauseAsync(Guid id)
    {
        if (!_items.TryGetValue(id, out var item))
            return;

        using var guard = await AcquireLifecycleLockAsync(id);

        if (item.State == DownloadState.Queued)
        {
            if (_itemCancellation.TryGetValue(id, out var queuedCts))
                queuedCts.Cancel();
            await TransitionToAsync(item, DownloadState.Paused);
            return;
        }

        await _engine.PauseAsync(id);
        if (_runningTasks.TryGetValue(id, out var running))
            await IgnoreCompletionAsync(running);
    }

    public async Task CancelAsync(Guid id)
    {
        if (!_items.TryGetValue(id, out var item))
            return;

        using var guard = await AcquireLifecycleLockAsync(id);

        if (_itemCancellation.TryGetValue(id, out var cts))
            cts.Cancel();

        if (item.State is DownloadState.Queued or DownloadState.Paused or DownloadState.Failed)
        {
            await TransitionToAsync(item, DownloadState.Cancelled);
            CleanupTempFiles(item);
            return;
        }

        await _engine.CancelAsync(id);
        if (_runningTasks.TryGetValue(id, out var running))
            await IgnoreCompletionAsync(running);
        CleanupTempFiles(item);
    }

    public async Task ResumeAsync(Guid id)
    {
        if (!_items.TryGetValue(id, out var item) || item.State is not (DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled))
            return;

        using var guard = await AcquireLifecycleLockAsync(id);
        if (_runningTasks.TryGetValue(id, out var previousRun))
            await IgnoreCompletionAsync(previousRun);
        if (_itemCancellation.TryRemove(id, out var previous))
            previous.Dispose();
        _itemCancellation[id] = new CancellationTokenSource();
        await TransitionToAsync(item, DownloadState.Queued);
        StartQueuedRun(item);
    }

    public async Task RemoveAsync(Guid id, bool deleteFile = false)
    {
        if (_items.TryGetValue(id, out var item))
        {
            await CancelAsync(id);
            _items.TryRemove(id, out _);
            await _repository.DeleteAsync(item);
            if (_itemCancellation.TryRemove(id, out var cts))
                cts.Dispose();
            if (_lifecycleLocks.TryRemove(id, out var lifecycleLock))
                lifecycleLock.Dispose();
            if (deleteFile && File.Exists(item.FullPath))
                File.Delete(item.FullPath);
            CleanupTempFiles(item);
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var cts in _itemCancellation.Values)
            cts.Cancel();
        foreach (var cts in _itemCancellation.Values)
            cts.Dispose();
        foreach (var lifecycleLock in _lifecycleLocks.Values)
            lifecycleLock.Dispose();
        return _repository.DisposeAsync();
    }

    private async ValueTask<IDisposable> AcquireLifecycleLockAsync(Guid id)
    {
        var lifecycleLock = _lifecycleLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await lifecycleLock.WaitAsync();
        return new Releaser(lifecycleLock);
    }

    private void TransitionTo(DownloadItem item, DownloadState state, bool persist)
    {
        DownloadLifecycle.EnsureCanTransition(item.State, state);
        item.State = state;
        if (persist)
            StateChanged?.Invoke(this, new DownloadStateChangedEventArgs { DownloadId = item.Id, State = state });
    }

    private async Task TransitionToAsync(DownloadItem item, DownloadState state)
    {
        TransitionTo(item, state, persist: true);
        await _repository.UpsertAsync(item);
    }

    private async Task PersistStateSafelyAsync(DownloadItem item)
    {
        try
        {
            await _repository.UpsertAsync(item);
        }
        catch (Exception ex)
        {
            item.ErrorMessage = $"Could not persist download state: {ex.Message}";
        }
    }

    private static async Task IgnoreCompletionAsync(Task task)
    {
        try { await task; } catch (OperationCanceledException) { } catch (Exception) { }
    }

    private static void CleanupTempFiles(DownloadItem item)
    {
        var tempDir = Path.Combine(item.DestinationDirectory, $".mdm-{item.Id:N}");
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;
        public void Dispose() => _semaphore.Release();
    }
}
