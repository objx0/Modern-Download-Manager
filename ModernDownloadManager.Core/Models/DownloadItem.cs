using SQLite;

namespace ModernDownloadManager.Core.Models;

/// <summary>
/// A single download job. This is the persisted unit of work; segments are
/// runtime-only detail owned by the engine while a download is active.
/// </summary>
public class DownloadItem
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DestinationDirectory { get; set; } = string.Empty;

    /// <summary>
    /// True if the person (or the extension) explicitly named this file.
    /// When false, the engine is free to replace FileName with whatever the
    /// server reports via Content-Disposition or the post-redirect URL —
    /// this is what fixes URLs like ".../file/get?id=123" downloading as "get".
    /// </summary>
    public bool FileNameIsExplicit { get; set; }

    public string FullPath => Path.Combine(DestinationDirectory, FileName);

    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }

    public DownloadState State { get; set; } = DownloadState.Queued;
    public DownloadCategory Category { get; set; } = DownloadCategory.General;

    /// <summary>Whether the server confirmed Accept-Ranges: bytes on HEAD/first response.</summary>
    public bool SupportsResume { get; set; }

    /// <summary>Validators used to detect if a resumed file changed on the server since we started.</summary>
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }

    public int SegmentCount { get; set; } = 8;

    /// <summary>Bytes/sec cap for this item; 0 = unlimited.</summary>
    public long SpeedLimitBytesPerSecond { get; set; }

    public string? ReferrerUrl { get; set; }
    /// <summary>Authentication cookies are intentionally session-only and are
    /// never written to the download history database.</summary>
    [Ignore]
    public string? CookieHeader { get; set; }
    public string? UserAgent { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }

    [Ignore]
    public double ProgressPercent => TotalBytes > 0
        ? Math.Clamp((double)DownloadedBytes / TotalBytes * 100.0, 0, 100)
        : 0;

    /// <summary>Creates the domain aggregate represented by this persistence row.</summary>
    public DownloadJob ToJob() => DownloadJob.FromRecord(this);
}
