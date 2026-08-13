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
public sealed class DownloadQueueManager : IAsyncDisposable
{
    private readonly IDownloadEngine _engine;
    private readonly DownloadRepository _repository;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly ConcurrentDictionary<Guid, DownloadItem> _items = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _itemCancellation = new();
    private readonly ConcurrentDictionary<Guid, Task> _runningTasks = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _lifecycleLocks = new();

    public int MaxConcurrentDownloads { get; }

    public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    public event EventHandler<DownloadStateChangedEventArgs>? StateChanged;

    /// <summary>Fired whenever a new item enters the queue — including from the
    /// browser extension via the local pipe, not just the UI's own Add button —
    /// so any listening UI can add a view model for it regardless of source.</summary>
    public event EventHandler<DownloadItem>? ItemAdded;

    public DownloadQueueManager(IDownloadEngine engine, DownloadRepository repository, int maxConcurrentDownloads = 3)
    {
        _engine = engine;
        _repository = repository;
        MaxConcurrentDownloads = maxConcurrentDownloads;
        _concurrencyGate = new SemaphoreSlim(maxConcurrentDownloads);

        _engine.ProgressChanged += (s, e) => ProgressChanged?.Invoke(this, e);
        _engine.StateChanged += async (s, e) =>
        {
            StateChanged?.Invoke(this, e);
            if (_items.TryGetValue(e.DownloadId, out var item))
                await _repository.UpsertAsync(item);
        };
    }

    public async Task LoadFromDiskAsync()
    {
        foreach (var item in await _repository.GetAllAsync())
        {
            // Anything that was mid-flight when the app last closed comes back as Paused,
            // not Downloading — we never resume network activity without the user asking.
            if (item.State is DownloadState.Downloading or DownloadState.Connecting or DownloadState.Merging)
                item.State = DownloadState.Paused;

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
        // Only trust a URL-path-derived guess if it actually looks like a filename
        // (has an extension) — otherwise leave it for the engine to fill in from
        // Content-Disposition/redirect once headers arrive, instead of showing
        // something like "get" or "download" from a redirect endpoint.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
            (parsedUrl.Scheme != Uri.UriSchemeHttp && parsedUrl.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Only HTTP and HTTPS download URLs are supported.", nameof(url));

        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("A destination directory is required.", nameof(destinationDirectory));

        destinationDirectory = Path.GetFullPath(destinationDirectory);
        var urlGuess = Path.GetFileName(parsedUrl.LocalPath);
        var looksLikeRealFileName = !string.IsNullOrWhiteSpace(urlGuess) && Path.HasExtension(urlGuess);

        suggestedFileName = SanitizeFileName(suggestedFileName);

        var fileName = suggestedFileName
            ?? (looksLikeRealFileName ? urlGuess : "Fetching name…");

        var item = new DownloadItem
        {
            Url = url,
            FileName = fileName,
            FileNameIsExplicit = suggestedFileName is not null,
            DestinationDirectory = destinationDirectory,
            Category = category ?? DownloadCategoryResolver.Resolve(fileName),
            State = startImmediately ? DownloadState.Queued : DownloadState.Paused,
            SpeedLimitBytesPerSecond = speedLimitBytesPerSecond,
            SegmentCount = segmentCount,
            ReferrerUrl = referrer,
            CookieHeader = cookie,
            UserAgent = userAgent
        };

        _items[item.Id] = item;
        _itemCancellation[item.Id] = new CancellationTokenSource();
        _lifecycleLocks[item.Id] = new SemaphoreSlim(1, 1);
        await _repository.UpsertAsync(item);
        ItemAdded?.Invoke(this, item);

        if (startImmediately)
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
        if (!_itemCancellation.TryGetValue(item.Id, out var itemCts))
            return;

        try
        {
            await _concurrencyGate.WaitAsync(itemCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (itemCts.IsCancellationRequested || item.State != DownloadState.Queued)
                return;

            await _engine.StartAsync(item);
        }
        finally
        {
            _concurrencyGate.Release();
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
            item.State = DownloadState.Paused;
            await PersistStateAsync(item, DownloadState.Paused);
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
            item.State = DownloadState.Cancelled;
            await PersistStateAsync(item, DownloadState.Cancelled);
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
        if (!_items.TryGetValue(id, out var item) || item.State is not (DownloadState.Paused or DownloadState.Failed))
            return;

        using var guard = await AcquireLifecycleLockAsync(id);
        if (_runningTasks.TryGetValue(id, out var previousRun))
            await IgnoreCompletionAsync(previousRun);
        if (_itemCancellation.TryRemove(id, out var previous))
            previous.Dispose();
        _itemCancellation[id] = new CancellationTokenSource();
        item.State = DownloadState.Queued;
        await PersistStateAsync(item, DownloadState.Queued);
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

    private async Task PersistStateAsync(DownloadItem item, DownloadState state)
    {
        StateChanged?.Invoke(this, new DownloadStateChangedEventArgs { DownloadId = item.Id, State = state });
        await _repository.UpsertAsync(item);
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
