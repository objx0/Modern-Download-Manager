namespace ModernDownloadManager.Core.Models;

/// <summary>Central lifecycle policy shared by every application host.</summary>
public static class DownloadLifecycle
{
    public static bool CanTransition(DownloadState from, DownloadState to) =>
        from == to || (from, to) switch
        {
            (DownloadState.Queued, DownloadState.Connecting or DownloadState.Paused or DownloadState.Cancelled) => true,
            (DownloadState.Connecting, DownloadState.Downloading or DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled) => true,
            (DownloadState.Downloading, DownloadState.Merging or DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled) => true,
            (DownloadState.Merging, DownloadState.Completed or DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled) => true,
            (DownloadState.Paused, DownloadState.Queued or DownloadState.Cancelled) => true,
            (DownloadState.Failed, DownloadState.Queued or DownloadState.Cancelled) => true,
            (DownloadState.Cancelled, DownloadState.Queued) => true,
            _ => false
        };

    public static void EnsureCanTransition(DownloadState from, DownloadState to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Invalid download state transition: {from} -> {to}.");
    }
}
