using ModernDownloadManager.Core.Engine;
using ModernDownloadManager.Core.Models;

namespace ModernDownloadManager.Core.Scheduling;

/// <summary>Application-facing queue API. UIs and browser adapters depend on
/// this contract rather than on scheduler implementation details.</summary>
public interface IDownloadManager
{
    IReadOnlyCollection<DownloadItem> Items { get; }
    event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    event EventHandler<DownloadStateChangedEventArgs>? StateChanged;
    event EventHandler<DownloadItem>? ItemAdded;

    Task LoadFromDiskAsync();
    Task<DownloadItem> EnqueueAsync(string url, string destinationDirectory,
        string? suggestedFileName = null, long speedLimitBytesPerSecond = 0,
        int segmentCount = 8, string? referrer = null, string? cookie = null,
        string? userAgent = null, DownloadCategory? category = null,
        bool startImmediately = true);
    Task<DownloadItem> EnqueueAsync(string url, DownloadOptions options);
    Task PauseAsync(Guid id);
    Task ResumeAsync(Guid id);
    Task CancelAsync(Guid id);
    Task RemoveAsync(Guid id, bool deleteFile = false);
}
