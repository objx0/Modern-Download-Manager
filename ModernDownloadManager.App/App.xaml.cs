using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Runtime.Versioning;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using ModernDownloadManager.App.ViewModels;
using ModernDownloadManager.Core.Engine;
using ModernDownloadManager.Core.Ipc;
using ModernDownloadManager.Core.Models;
using ModernDownloadManager.Core.Persistence;
using ModernDownloadManager.Core.Scheduling;

namespace ModernDownloadManager.App;

/// <summary>
/// Composition root. Kept intentionally simple (manual wiring, no DI container) —
/// there are only a handful of services and it keeps startup easy to follow.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public partial class App : Application
{
    public static Window? MainAppWindow { get; private set; }
    public static DownloadQueueManager? QueueManager { get; private set; }
    public static AppSettingsStore? SettingsStore { get; private set; }

    private readonly CancellationTokenSource _pipeServerCts = new();
    private AppSettings? _settings;
    private TrayIconService? _trayIcon;
    private readonly Dictionary<Guid, DownloadMiniWindow> _miniWindows = new();
    private bool _exitRequested;
    private static Mutex? _singleInstanceMutex;
    private string? _appDataDir;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashLog.Write("WinUI unhandled exception", e.Exception);
    }

    private void OnDomainUnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            CrashLog.Write("AppDomain unhandled exception", exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLog.Write("Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(true, "Local\\ModernDownloadManager.SingleInstance", out var ownsMutex);
        if (!ownsMutex)
        {
            await ForwardToExistingInstanceAsync(Environment.GetCommandLineArgs());
            Environment.Exit(0);
            return;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDataDir = Path.Combine(localAppData, "ModernDownloadManager");
        Directory.CreateDirectory(appDataDir);
        _appDataDir = appDataDir;

        SettingsStore = new AppSettingsStore(Path.Combine(appDataDir, "settings.json"));
        _settings = await SettingsStore.LoadAsync();

        var httpClient = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 16,
            AutomaticDecompression = DecompressionMethods.All
        });

        var engine = new SegmentedDownloader(httpClient);
        var repository = await DownloadRepository.CreateAsync(Path.Combine(appDataDir, "downloads.db3"));

        // MaxConcurrentDownloads is read once at startup — changing it on the
        // Settings page is persisted immediately but takes effect on next launch,
        // since a live SemaphoreSlim can't safely shrink its capacity mid-run.
        QueueManager = new DownloadQueueManager(engine, repository, _settings.MaxConcurrentDownloads);
        await QueueManager.LoadFromDiskAsync();

        try { AppNotificationManager.Default.Register(); }
        catch (Exception) { /* Notifications can be unavailable on older Windows setups. */ }

        var window = new MainWindow(new MainViewModel(QueueManager, _settings, SettingsStore));
        MainAppWindow = window;
        window.Activate();
        SetTrayIconEnabled(_settings.ShowTrayIcon);
        window.InstallCloseToTrayHandler();
        window.AppWindow.Closing += OnWindowClosing;
        window.AppWindow.Changed += OnWindowChanged;

        QueueManager.StateChanged += OnQueueStateChanged;

        // Serves the browser extension's native-messaging host: if it's already
        // running (this instance), the host hands the download straight over
        // the pipe instead of launching a second process.
        _ = DownloadPipe.RunServerAsync(HandleExtensionRequestAsync, _pipeServerCts.Token);

        // Cold-start case: the native host launched us fresh with a pending
        // download because nothing was listening on the pipe yet.
        TryHandleLaunchArgs(Environment.GetCommandLineArgs());

        window.Closed += (_, _) =>
        {
            _pipeServerCts.Cancel();
            _trayIcon?.Dispose();
            _trayIcon = null;
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        };
    }

    private static async Task ForwardToExistingInstanceAsync(string[] commandLineArgs)
    {
        const string prefix = "--add-download=";
        var arg = Array.Find(commandLineArgs, a => a.StartsWith(prefix, StringComparison.Ordinal));
        if (arg is null)
            return;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(arg[prefix.Length..]));
            var request = JsonSerializer.Deserialize<DownloadRequestMessage>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (request is not null && !string.IsNullOrWhiteSpace(request.Url))
                await DownloadPipe.TrySendWithRetryAsync(request);
        }
        catch (FormatException) { }
    }

    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested || _trayIcon is null)
            return;

        args.Cancel = true;
        sender.Hide();
        _trayIcon?.UpdateTip(HasPendingDownloads()
            ? "Modern Download Manager - Downloading"
            : "Modern Download Manager");
    }

    private void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        // Keep normal Windows minimize behavior: the app remains visible in
        // the taskbar. Closing the window is the action that hides it to tray.
        if (args.DidPresenterChange && sender.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized && HasPendingDownloads())
            _trayIcon?.UpdateTip("Modern Download Manager - Downloading");
    }

    private bool HasPendingDownloads() => QueueManager?.Items.Any(item => item.State is
        DownloadState.Queued or DownloadState.Connecting or DownloadState.Downloading or DownloadState.Merging) == true;

    internal void ExitFromTray()
    {
        _exitRequested = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        MainAppWindow?.Close();
    }

    internal bool ShouldHideOnClose => !_exitRequested && _trayIcon is not null;

    internal void SetTrayIconEnabled(bool enabled)
    {
        if (MainAppWindow is not MainWindow window)
            return;

        if (!enabled)
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            return;
        }

        if (_trayIcon is not null)
            return;

        try
        {
            _trayIcon = new TrayIconService(window);
        }
        catch (Exception ex)
        {
            _trayIcon = null;
            try
            {
                File.AppendAllText(Path.Combine(_appDataDir ?? Path.GetTempPath(), "tray.log"),
                    $"{DateTimeOffset.Now:u} Tray registration failed: {ex}{Environment.NewLine}");
            }
            catch { }
        }
    }

    internal static void ShowMiniFor(Guid id)
    {
        if (Application.Current is not App app || QueueManager is null)
            return;

        var item = QueueManager.Items.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null)
            return;

        if (app._miniWindows.TryGetValue(id, out var existing))
        {
            existing.RestoreFromManager();
            return;
        }

        app._miniWindows[id] = new DownloadMiniWindow(item, QueueManager);
    }

    private Task HandleExtensionRequestAsync(DownloadRequestMessage request) =>
        EnqueueFromExtensionAsync(request);

    private void OnQueueStateChanged(object? sender, DownloadStateChangedEventArgs e)
    {
        if (e.State != DownloadState.Completed || QueueManager is null || _settings?.ShowCompletionNotifications != true)
            return;

        var item = QueueManager.Items.FirstOrDefault(i => i.Id == e.DownloadId);
        if (item is null)
            return;

        try
        {
            AppNotificationManager.Default.Show(new AppNotificationBuilder()
                .AddText("Download completed")
                .AddText(item.FileName)
                .BuildNotification());
        }
        catch (Exception) { /* A minimized app should never fail because a toast is unavailable. */ }

        if (!HasPendingDownloads())
            _trayIcon?.UpdateTip("Modern Download Manager - Download complete");
    }

    private void TryHandleLaunchArgs(string[] commandLineArgs)
    {
        const string prefix = "--add-download=";
        var arg = Array.Find(commandLineArgs, a => a.StartsWith(prefix, StringComparison.Ordinal));
        if (arg is null)
            return;

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(arg[prefix.Length..]));
            var request = JsonSerializer.Deserialize<DownloadRequestMessage>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (request is not null && !string.IsNullOrWhiteSpace(request.Url))
                _ = EnqueueFromExtensionAsync(request);
        }
        catch (FormatException) { /* malformed arg — ignore rather than crash startup */ }
    }

    private async Task EnqueueFromExtensionAsync(DownloadRequestMessage request)
    {
        if (QueueManager is null || _settings is null)
            return;

        if (request.TotalBytes >= 0 && request.TotalBytes < _settings.MinimumCaptureSizeBytes)
        {
            try
            {
                AppNotificationManager.Default.Show(new AppNotificationBuilder()
                    .AddText("Browser download left in place")
                    .AddText($"{request.SuggestedFileName ?? "File"} is smaller than the capture threshold.")
                    .BuildNotification());
            }
            catch (Exception) { }
            return;
        }

        if (MainAppWindow is not MainWindow window)
            return;

        var dialogResult = await window.ShowDownloadDialogAsync(request.Url, request.SuggestedFileName);
        if (dialogResult is null)
            return;

        var item = await QueueManager.EnqueueAsync(
            request.Url,
            dialogResult.DestinationDirectory,
            request.SuggestedFileName,
            _settings.DefaultSpeedLimitBytesPerSecond,
            _settings.DefaultSegmentCount,
            request.Referrer,
            request.Cookie,
            request.UserAgent,
            dialogResult.Category,
            dialogResult.StartNow);

        if (dialogResult.StartNow)
            ShowMiniFor(item.Id);

        if (dialogResult.RememberCategory)
        {
            _settings.CategoryDownloadFolders[dialogResult.Category.ToString()] = dialogResult.DestinationDirectory;
            await SettingsStore!.SaveAsync(_settings);
        }
    }
}
