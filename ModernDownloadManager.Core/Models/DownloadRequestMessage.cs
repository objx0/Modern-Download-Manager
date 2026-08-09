namespace ModernDownloadManager.Core.Models;

/// <summary>
/// What the browser extension sends over native messaging / the local pipe.
/// The browser already resolved the real filename via its own download
/// heuristics (Content-Disposition, redirect chain, MIME sniffing) — far more
/// reliable than us re-deriving it from a bare HTTP response, so SuggestedFileName
/// here is trusted as explicit rather than re-guessed by the engine.
/// </summary>
public class DownloadRequestMessage
{
    public string Url { get; set; } = string.Empty;
    public string? SuggestedFileName { get; set; }
    public string? Referrer { get; set; }
    public string? Cookie { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>Known browser-reported size. -1 means unknown.</summary>
    public long TotalBytes { get; set; } = -1;
}
