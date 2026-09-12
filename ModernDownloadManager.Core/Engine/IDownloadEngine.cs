using ModernDownloadManager.Core.Models;

namespace ModernDownloadManager.Core.Engine;

public class DownloadProgressEventArgs : EventArgs
{
    public required Guid DownloadId { get; init; }
    public long DownloadedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public TimeSpan? Eta { get; init; }
}

public class DownloadStateChangedEventArgs : EventArgs
{
    public required Guid DownloadId { get; init; }
    public DownloadState State { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Contract for a download engine capable of running a single DownloadItem.
/// Implementations: SegmentedDownloader (multi-connection, resumable),
/// with automatic fallback to single-stream when the server doesn't support ranges.
/// </summary>
public interface IDownloadEngine
{
    event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    event EventHandler<DownloadStateChangedEventArgs>? StateChanged;

    Task StartAsync(DownloadItem item, CancellationToken cancellationToken = default);
    Task StartAsync(DownloadJob job, CancellationToken cancellationToken = default);
    Task PauseAsync(Guid downloadId);
    Task CancelAsync(Guid downloadId);
}
