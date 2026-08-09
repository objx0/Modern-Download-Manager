using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModernDownloadManager.Core.Engine;
using ModernDownloadManager.Core.Models;
using ModernDownloadManager.Core.Persistence;
using ModernDownloadManager.Core.Scheduling;

namespace ModernDownloadManager.App.ViewModels;

public enum FilterMode
{
    All,
    Active,
    Completed,
    Category
}

public partial class MainViewModel : ObservableObject
{
    private readonly DownloadQueueManager _queue;
    private readonly AppSettings _settings;
    private readonly Dictionary<Guid, DownloadItemViewModel> _byId = new();

    public SettingsViewModel Settings { get; }

    public Func<DownloadItemViewModel, Task<RemoveDecision>>? ConfirmRemoveAsync { get; set; }

    [ObservableProperty]
    private bool isSettingsView;

    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = new();

    /// <summary>What the ListView actually binds to — recomputed whenever the
    /// filter or the underlying list changes, since NavigationView selection
    /// doesn't trigger re-evaluation of a plain LINQ property on its own.</summary>
    public ObservableCollection<DownloadItemViewModel> VisibleDownloads { get; } = new();

    [ObservableProperty]
    private FilterMode currentFilter = FilterMode.All;

    [ObservableProperty]
    private DownloadCategory? currentCategory;

    [ObservableProperty]
    private string headerText = "All Downloads";

    partial void OnCurrentFilterChanged(FilterMode value) => UpdateHeaderText();
    partial void OnCurrentCategoryChanged(DownloadCategory? value) => UpdateHeaderText();

    private void UpdateHeaderText()
    {
        HeaderText = CurrentFilter switch
        {
            FilterMode.Active => "Active",
            FilterMode.Completed => "Completed",
            FilterMode.Category => CurrentCategory?.ToString() ?? "Category",
            _ => "All Downloads"
        };
        RefreshVisibleDownloads();
    }

    private void RefreshVisibleDownloads()
    {
        VisibleDownloads.Clear();
        foreach (var vm in FilteredDownloads)
            VisibleDownloads.Add(vm);
    }

    [ObservableProperty]
    private string newUrlText = string.Empty;

    [ObservableProperty]
    private string statusText = "Ready";

    public MainViewModel(DownloadQueueManager queue, AppSettings settings, AppSettingsStore settingsStore)
    {
        _queue = queue;
        _settings = settings;
        Settings = new SettingsViewModel(settings, settingsStore);
        _queue.ProgressChanged += OnProgressChanged;
        _queue.StateChanged += OnStateChanged;
        _queue.ItemAdded += OnItemAdded;

        foreach (var item in _queue.Items)
            AddViewModel(item);
        RefreshVisibleDownloads();
    }

    private void OnItemAdded(object? sender, DownloadItem item)
    {
        // Covers items added from anywhere — the UI's own Add button and the
        // browser extension (via the local pipe) both flow through here, so
        // a download started from the browser shows up without extra wiring.
        App.MainAppWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_byId.ContainsKey(item.Id))
                AddViewModel(item);
        });
    }

    private void AddViewModel(DownloadItem item)
    {
        var vm = new DownloadItemViewModel(item, _queue);
        vm.ConfirmRemoveAsync = ConfirmRemoveAsync;
        vm.Removed += OnDownloadRemoved;
        vm.ApplyState(item.State);
        vm.ApplyProgress(item.DownloadedBytes, item.TotalBytes, 0, null);
        _byId[item.Id] = vm;
        Downloads.Insert(0, vm);
        RefreshVisibleDownloads();
    }

    private void OnDownloadRemoved(object? sender, EventArgs e)
    {
        if (sender is not DownloadItemViewModel vm)
            return;

        App.MainAppWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            _byId.Remove(vm.Id);
            Downloads.Remove(vm);
            VisibleDownloads.Remove(vm);
            StatusText = $"Removed {vm.FileName}";
        });
    }

    private void OnProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        if (_byId.TryGetValue(e.DownloadId, out var vm))
        {
            // Marshal to UI thread — DispatcherQueue is set on MainWindow; kept
            // simple here via App.MainAppWindow's dispatcher.
            App.MainAppWindow?.DispatcherQueue.TryEnqueue(() =>
                vm.ApplyProgress(e.DownloadedBytes, e.TotalBytes, e.BytesPerSecond, e.Eta));
        }
    }

    private void OnStateChanged(object? sender, DownloadStateChangedEventArgs e)
    {
        if (_byId.TryGetValue(e.DownloadId, out var vm))
        {
            App.MainAppWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                vm.ApplyState(e.State);
                StatusText = e.State == DownloadState.Failed
                    ? $"{vm.FileName} failed: {e.ErrorMessage}"
                    : $"{vm.FileName} — {e.State}";
                RefreshVisibleDownloads();
            });
        }
    }

    public async Task EnqueueDownloadAsync(string url, string destinationDirectory,
        DownloadCategory category, bool startNow, string? suggestedFileName = null)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusText = "Enter a valid HTTP or HTTPS URL first.";
            return;
        }

        // No need to add a view model here — ItemAdded (subscribed above) fires
        // for every enqueue regardless of source and handles it uniformly.
        var item = await _queue.EnqueueAsync(
            url,
            destinationDirectory,
            suggestedFileName,
            speedLimitBytesPerSecond: _settings.DefaultSpeedLimitBytesPerSecond,
            segmentCount: _settings.DefaultSegmentCount,
            category: category,
            startImmediately: startNow);

        NewUrlText = string.Empty;
    }

    public IEnumerable<DownloadItemViewModel> FilteredDownloads => CurrentFilter switch
    {
        FilterMode.Active => Downloads.Where(d => d.State is DownloadState.Downloading
            or DownloadState.Connecting or DownloadState.Queued or DownloadState.Merging or DownloadState.Paused),
        FilterMode.Completed => Downloads.Where(d => d.State == DownloadState.Completed),
        FilterMode.Category => Downloads.Where(d => d.Category == CurrentCategory),
        _ => Downloads
    };
}
