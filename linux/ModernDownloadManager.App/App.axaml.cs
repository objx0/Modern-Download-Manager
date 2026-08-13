using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ModernDownloadManager.Core.Engine;
using ModernDownloadManager.Core.Ipc;
using ModernDownloadManager.Core.Models;
using ModernDownloadManager.Core.Persistence;
using ModernDownloadManager.Core.Scheduling;
using System.Net;

namespace ModernDownloadManager.Linux;

public partial class App : Application
{
    private CancellationTokenSource? _ipcCancellation;
    private DownloadQueueManager? _queue;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        var dataDirectory = GetDataDirectory();
        Directory.CreateDirectory(dataDirectory);
        var settings = new AppSettingsStore(Path.Combine(dataDirectory, "settings.json"));
        var startup = InitializeAsync(settings, dataDirectory, desktop);
        startup.GetAwaiter().GetResult();
        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAsync(AppSettingsStore settingsStore, string dataDirectory,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        var settings = await settingsStore.LoadAsync();
        var client = new HttpClient(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 16,
            AutomaticDecompression = DecompressionMethods.All
        });
        var engine = new SegmentedDownloader(client);
        var repository = await DownloadRepository.CreateAsync(Path.Combine(dataDirectory, "downloads.db3"));
        _queue = new DownloadQueueManager(engine, repository, settings.MaxConcurrentDownloads);
        await _queue.LoadFromDiskAsync();

        var window = new MainWindow(_queue, settings, settingsStore);
        desktop.MainWindow = window;
        _ipcCancellation = new CancellationTokenSource();
        _ = DownloadPipe.RunServerAsync(request => EnqueueFromBrowserAsync(window, settings, request), _ipcCancellation.Token);
        desktop.ShutdownRequested += async (_, _) =>
        {
            _ipcCancellation.Cancel();
            if (_queue is not null)
                await _queue.DisposeAsync();
        };
        window.Show();
    }

    private static async Task EnqueueFromBrowserAsync(MainWindow window, AppSettings settings,
        DownloadRequestMessage request)
    {
        await Dispatcher.UIThread.InvokeAsync(() => window.AddBrowserDownloadAsync(request, settings));
    }

    private static string GetDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernDownloadManager");
}
