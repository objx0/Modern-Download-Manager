namespace ModernDownloadManager.Core.Models;

/// <summary>
/// User-configurable preferences, persisted as JSON. Kept separate from
/// DownloadItem/queue state since it changes rarely and has different
/// lifecycle needs (loaded once at startup, edited from a Settings page).
/// </summary>
public class AppSettings
{
    public int MaxConcurrentDownloads { get; set; } = 3;
    public string DefaultDownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>0 = unlimited. Applied to newly-added downloads as their initial cap.</summary>
    public long DefaultSpeedLimitBytesPerSecond { get; set; }

    public int DefaultSegmentCount { get; set; } = 8;

    /// <summary>
    /// Minimum known file size for browser-captured downloads. A value of zero
    /// captures every file. The browser extension uses the same value so small
    /// browser downloads are left alone before they ever reach the app.
    /// </summary>
    public long MinimumCaptureSizeBytes { get; set; } = 0;

    public bool BrowserCaptureEnabled { get; set; } = true;

    public bool ShowCompletionNotifications { get; set; } = true;

    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Keep Windows awake while one or more downloads are active.</summary>
    public bool PreventSleepDuringDownloads { get; set; } = true;

    /// <summary>Launch the unpackaged desktop app when the user signs in.</summary>
    public bool StartWithWindows { get; set; }

    public Dictionary<string, string> CategoryDownloadFolders { get; set; } = new();
}
