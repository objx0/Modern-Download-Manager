using System.Collections.ObjectModel;
using System.Reflection;
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
    Category,
    Queued
}

public partial class MainViewModel : ObservableObject
{
    private readonly DownloadQueueManager _queue;
    private readonly AppSettings _settings;
    private readonly Dictionary<Guid, DownloadItemViewModel> _byId = new();
    private readonly Dictionary<Guid, double> _currentSpeeds = new();

    public SettingsViewModel Settings { get; }

    public Func<DownloadItemViewModel, Task<RemoveDecision>>? ConfirmRemoveAsync { get; set; }
    public Func<DownloadItemViewModel, Task>? OpenDownloadWindowAsync { get; set; }

    [ObservableProperty]
    private bool isSettingsView;

    [ObservableProperty]
    private bool isAboutView;

    public bool IsDownloadsView => !IsSettingsView && !IsAboutView;

    partial void OnIsSettingsViewChanged(bool value) => OnPropertyChanged(nameof(IsDownloadsView));
    partial void OnIsAboutViewChanged(bool value) => OnPropertyChanged(nameof(IsDownloadsView));

    public string VersionText => $"Version {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0-prealpha"}";
    public string RepositoryUrl => "https://github.com/objx0/modern-download-manager";

    public string ActiveDownloadsSummary { get; private set; } = "No active downloads";
    public string BandwidthSummary { get; private set; } = "0 B/s total";

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
            FilterMode.Active => "Unfinished",
            FilterMode.Queued => "Queued",
            FilterMode.Completed => "Finished",
            FilterMode.Category => CurrentCategory?.ToString() ?? "Category",
            _ => "All Downloads"
        };
        RefreshVisibleDownloads();
    }

    public void RefreshHeaderText() => UpdateHeaderText();

    private void RefreshVisibleDownloads()
    {
        var selected = SelectedDownload;
        var matching = SearchFilteredDownloads.ToArray();
        foreach (var item in VisibleDownloads.Where(d => !matching.Contains(d)).ToArray())
            VisibleDownloads.Remove(item);
        for (var index = 0; index < matching.Length; index++)
        {
            var existingIndex = VisibleDownloads.IndexOf(matching[index]);
            if (existingIndex < 0) VisibleDownloads.Insert(index, matching[index]);
            else if (existingIndex != index) VisibleDownloads.Move(existingIndex, index);
        }
        SelectedDownload = selected is not null && VisibleDownloads.Contains(selected) ? selected : null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DownloadItemViewModel? selectedDownload;

    public bool HasSelection => SelectedDownload is not null;

    [RelayCommand]
    private async Task StopAll()
    {
        foreach (var item in Downloads.Where(d => d.CanPause).ToArray())
            await item.PauseCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task ResumeAll()
    {
        foreach (var item in Downloads.Where(d => d.CanResume).ToArray())
            await item.ResumeCommand.ExecuteAsync(null);
    }

    [ObservableProperty]
    private string newUrlText = string.Empty;

    [ObservableProperty]
    private string searchText = string.Empty;

    partial void OnSearchTextChanged(string value) => RefreshVisibleDownloads();

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
        vm.OpenDownloadWindowAsync = OpenDownloadWindowAsync;
        vm.Removed += OnDownloadRemoved;
        vm.ApplyState(item.State);
        vm.ApplyProgress(item.DownloadedBytes, item.TotalBytes, 0, null);
        _byId[item.Id] = vm;
        Downloads.Insert(0, vm);
        _currentSpeeds[item.Id] = 0;
        RefreshVisibleDownloads();
        RefreshSummary();
    }

    private void OnDownloadRemoved(object? sender, EventArgs e)
    {
        if (sender is not DownloadItemViewModel vm)
            return;

        App.MainAppWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            _byId.Remove(vm.Id);
            _currentSpeeds.Remove(vm.Id);
            Downloads.Remove(vm);
            VisibleDownloads.Remove(vm);
            StatusText = $"Removed {vm.FileName}";
            RefreshSummary();
        });
    }

    private void OnProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        if (_byId.TryGetValue(e.DownloadId, out var vm))
        {
            // Marshal to UI thread — DispatcherQueue is set on MainWindow; kept
            // simple here via App.MainAppWindow's dispatcher.
            App.MainAppWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                vm.ApplyProgress(e.DownloadedBytes, e.TotalBytes, e.BytesPerSecond, e.Eta);
                _currentSpeeds[e.DownloadId] = e.BytesPerSecond;
                RefreshSummary();
            });
        }
    }

    private void OnStateChanged(object? sender, DownloadStateChangedEventArgs e)
    {
        if (_byId.TryGetValue(e.DownloadId, out var vm))
        {
            App.MainAppWindow?.DispatcherQueue.TryEnqueue(() =>
            {
                vm.ApplyState(e.State);
                if (e.State is not (DownloadState.Downloading or DownloadState.Connecting))
                    _currentSpeeds[e.DownloadId] = 0;
                StatusText = e.State == DownloadState.Failed
                    ? $"{vm.FileName} failed: {e.ErrorMessage}"
                    : $"{vm.FileName} — {e.State}";
                RefreshVisibleDownloads();
                RefreshSummary();
            });
        }
    }

    private void RefreshSummary()
    {
        var activeCount = Downloads.Count(d => d.State is DownloadState.Downloading
            or DownloadState.Connecting or DownloadState.Merging);
        ActiveDownloadsSummary = activeCount switch
        {
            0 => "No active downloads",
            1 => "1 active download",
            _ => $"{activeCount} active downloads"
        };
        BandwidthSummary = $"{FormatBytes(_currentSpeeds.Values.Sum())}/s total";
        OnPropertyChanged(nameof(ActiveDownloadsSummary));
        OnPropertyChanged(nameof(BandwidthSummary));
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return $"{bytes:0.#} {units[unit]}";
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
        FilterMode.Active => Downloads.Where(d => d.State != DownloadState.Completed),
        FilterMode.Queued => Downloads.Where(d => d.State == DownloadState.Queued),
        FilterMode.Completed => Downloads.Where(d => d.State == DownloadState.Completed),
        FilterMode.Category => Downloads.Where(d => d.Category == CurrentCategory),
        _ => Downloads
    };

    private IEnumerable<DownloadItemViewModel> SearchFilteredDownloads =>
        string.IsNullOrWhiteSpace(SearchText)
            ? FilteredDownloads
            : FilteredDownloads.Where(d =>
                d.FileName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                d.Url.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                d.CategoryText.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                d.StateText.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
}
