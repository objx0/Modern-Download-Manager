using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModernDownloadManager.Core.Models;
using ModernDownloadManager.Core.Persistence;

namespace ModernDownloadManager.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly AppSettingsStore _store;

    public SettingsViewModel(AppSettings settings, AppSettingsStore store)
    {
        _settings = settings;
        _store = store;

        maxConcurrentDownloads = settings.MaxConcurrentDownloads;
        defaultDownloadFolder = settings.DefaultDownloadFolder;
        defaultSpeedLimitMBps = settings.DefaultSpeedLimitBytesPerSecond / 1_000_000.0;
        defaultSegmentCount = settings.DefaultSegmentCount;
        minimumCaptureSizeMB = settings.MinimumCaptureSizeBytes / 1_000_000.0;
        browserCaptureEnabled = settings.BrowserCaptureEnabled;
        showCompletionNotifications = settings.ShowCompletionNotifications;
        showTrayIcon = settings.ShowTrayIcon;
        preventSleepDuringDownloads = settings.PreventSleepDuringDownloads;
        startWithWindows = settings.StartWithWindows;
    }

    [ObservableProperty]
    private int maxConcurrentDownloads;

    [ObservableProperty]
    private string defaultDownloadFolder;

    /// <summary>UI-friendly MB/s; 0 = unlimited. Converted to bytes/sec on save.</summary>
    [ObservableProperty]
    private double defaultSpeedLimitMBps;

    [ObservableProperty]
    private int defaultSegmentCount;

    /// <summary>Files smaller than this are left to the browser. 0 = capture all.</summary>
    [ObservableProperty]
    private double minimumCaptureSizeMB;

    [ObservableProperty]
    private bool browserCaptureEnabled;

    [ObservableProperty]
    private bool showCompletionNotifications;

    [ObservableProperty]
    private bool showTrayIcon;

    [ObservableProperty]
    private bool preventSleepDuringDownloads;

    [ObservableProperty]
    private bool startWithWindows;

    public Action<bool>? TrayIconSettingChanged { get; set; }
    public Action<bool>? StartupSettingChanged { get; set; }
    public Action<bool>? PreventSleepSettingChanged { get; set; }

    [ObservableProperty]
    private string saveStatusText = string.Empty;

    /// <summary>True once a save has happened this session and MaxConcurrentDownloads
    /// changed — used to show the "restart to apply" hint only when it's relevant.</summary>
    public bool MaxConcurrentDownloadsChangedPendingRestart =>
        MaxConcurrentDownloads != _settings.MaxConcurrentDownloads;

    public AppSettings CurrentEffectiveSettings => _settings;

    [RelayCommand]
    private async Task SaveAsync()
    {
        var concurrencyChanged = MaxConcurrentDownloads != _settings.MaxConcurrentDownloads;

        _settings.MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 16);
        _settings.DefaultDownloadFolder = string.IsNullOrWhiteSpace(DefaultDownloadFolder)
            ? _settings.DefaultDownloadFolder
            : DefaultDownloadFolder;
        _settings.DefaultSpeedLimitBytesPerSecond = (long)(Math.Max(0, DefaultSpeedLimitMBps) * 1_000_000);
        _settings.DefaultSegmentCount = Math.Clamp(DefaultSegmentCount, 1, 16);
        _settings.MinimumCaptureSizeBytes = (long)(Math.Max(0, MinimumCaptureSizeMB) * 1_000_000);
        _settings.BrowserCaptureEnabled = BrowserCaptureEnabled;
        _settings.ShowCompletionNotifications = ShowCompletionNotifications;
        _settings.ShowTrayIcon = ShowTrayIcon;
        _settings.PreventSleepDuringDownloads = PreventSleepDuringDownloads;
        _settings.StartWithWindows = StartWithWindows;

        await _store.SaveAsync(_settings);
        TrayIconSettingChanged?.Invoke(_settings.ShowTrayIcon);
        StartupSettingChanged?.Invoke(_settings.StartWithWindows);
        PreventSleepSettingChanged?.Invoke(_settings.PreventSleepDuringDownloads);

        SaveStatusText = concurrencyChanged
            ? "Saved. Restart the app for the concurrent-downloads limit to take effect."
            : "Settings saved.";
    }
}
