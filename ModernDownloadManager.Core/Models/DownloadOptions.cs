namespace ModernDownloadManager.Core.Models;

/// <summary>Configuration supplied when a download is created.</summary>
public sealed class DownloadOptions
{
    public string DestinationDirectory { get; set; } = string.Empty;
    public string? SuggestedFileName { get; set; }
    public long SpeedLimitBytesPerSecond { get; set; }
    public int SegmentCount { get; set; } = 8;
    public string? ReferrerUrl { get; set; }
    public string? CookieHeader { get; set; }
    public string? UserAgent { get; set; }
    public DownloadCategory? Category { get; set; }
    public bool StartImmediately { get; set; } = true;
}
