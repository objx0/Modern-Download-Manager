namespace ModernDownloadManager.Core.Models;

public enum DownloadState
{
    Queued,
    Connecting,
    Downloading,
    Paused,
    Merging,
    Completed,
    Failed,
    Cancelled
}

public enum SegmentState
{
    Pending,
    Downloading,
    Paused,
    Completed,
    Failed
}
