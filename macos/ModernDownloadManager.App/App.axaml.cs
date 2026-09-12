using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ModernDownloadManager.Core.Engine;
using ModernDownloadManager.Core.Ipc;
using ModernDownloadManager.Core.Models;
using ModernDownloadManager.Core.Persistence;
using ModernDownloadManager.Core.Platform;
using ModernDownloadManager.Core.Scheduling;
using System.Net;

namespace ModernDownloadManager.MacOS;

public partial class App : Application
{
    private CancellationTokenSource? _ipcCancellation;
    private readonly ILocalIpc _localIpc = new NamedPipeIpc();
    private DownloadQueueManager? _queue;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var paths = new DefaultPlatformPaths();
        Directory.CreateDirectory(paths.ApplicationDataDirectory);
        var settingsStore = new AppSettingsStore(Path.Combine(paths.ApplicationDataDirectory, "settings.json"));
        InitializeAsync(settingsStore, paths, desktop).GetAwaiter().GetResult();
        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAsync(AppSettingsStore settingsStore, IPlatformPaths paths,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var settings = await settingsStore.LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.DefaultDownloadFolder))
            settings.DefaultDownloadFolder = paths.DefaultDownloadDirectory;
        var client = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 16,
            AutomaticDecompression = DecompressionMethods.All
        });
        var engine = new SegmentedDownloader(client);
        _queue = new DownloadQueueManager(engine,
            await DownloadRepository.CreateAsync(Path.Combine(paths.ApplicationDataDirectory, "downloads.db3")),
            settings.MaxConcurrentDownloads);
        await _queue.LoadFromDiskAsync();

        var window = new MainWindow(_queue, settings, settingsStore);
        desktop.MainWindow = window;
        _ipcCancellation = new CancellationTokenSource();
        _ = _localIpc.RunServerAsync(request => window.AddBrowserDownloadAsync(request, settings), _ipcCancellation.Token);
        desktop.ShutdownRequested += async (_, _) =>
        {
            _ipcCancellation.Cancel();
            if (_queue is not null) await _queue.DisposeAsync();
        };
        window.Show();
    }
}
