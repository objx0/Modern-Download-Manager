namespace ModernDownloadManager.Core.Models;

/// <summary>Mutable execution state of a download, kept separate from its
/// request and user-configured options.</summary>
public sealed class DownloadRuntimeState
{
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public DownloadState State { get; set; } = DownloadState.Queued;
    public bool SupportsResume { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
