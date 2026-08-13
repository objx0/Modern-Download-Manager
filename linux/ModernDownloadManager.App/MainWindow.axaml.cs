using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ModernDownloadManager.Core.Models;
using ModernDownloadManager.Core.Persistence;
using ModernDownloadManager.Core.Scheduling;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace ModernDownloadManager.Linux;

public partial class MainWindow : Window
{
    private readonly DownloadQueueManager _queue;
    private readonly AppSettings _settings;
    private readonly AppSettingsStore _settingsStore;
    private readonly ObservableCollection<DownloadRow> _rows = new();
    private readonly Dictionary<Guid, DownloadRow> _rowsById = new();

    public MainWindow(DownloadQueueManager queue, AppSettings settings, AppSettingsStore settingsStore)
    {
        InitializeComponent();
        _queue = queue;
        _settings = settings;
        _settingsStore = settingsStore;
        Downloads.ItemsSource = _rows;
        foreach (var item in queue.Items)
            AddRow(item);
        queue.ItemAdded += (_, item) => Dispatcher.UIThread.Post(() => AddRow(item));
        queue.ProgressChanged += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (_rowsById.TryGetValue(e.DownloadId, out var row))
            {
                row.Progress = e.TotalBytes > 0 ? e.DownloadedBytes * 100d / e.TotalBytes : 0;
                row.State = _queue.Items.FirstOrDefault(i => i.Id == e.DownloadId)?.State.ToString() ?? row.State;
            }
        });
        queue.StateChanged += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            if (_rowsById.TryGetValue(e.DownloadId, out var row)) row.State = e.State.ToString();
            Status.Text = e.State == DownloadState.Failed ? e.ErrorMessage ?? "Download failed" : e.State.ToString();
        });
    }

    private void AddRow(DownloadItem item)
    {
        if (_rowsById.ContainsKey(item.Id)) return;
        var row = new DownloadRow(item.Id, item.FileName, item.State.ToString());
        _rowsById[item.Id] = row;
        _rows.Insert(0, row);
    }

    private async void AddDownload(object? sender, RoutedEventArgs e)
    {
        await AddAsync(UrlBox.Text, null);
        UrlBox.Text = string.Empty;
    }

    public async Task AddBrowserDownloadAsync(DownloadRequestMessage request, AppSettings settings)
    {
        await AddAsync(request.Url, request.SuggestedFileName, request.Referrer, request.Cookie, request.UserAgent);
        if (!IsVisible) Show();
        Activate();
    }

    private async Task AddAsync(string? url, string? suggestedFileName, string? referrer = null,
        string? cookie = null, string? userAgent = null)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Status.Text = "Enter a valid HTTP or HTTPS URL.";
            return;
        }
        await _queue.EnqueueAsync(url, _settings.DefaultDownloadFolder, suggestedFileName,
            _settings.DefaultSpeedLimitBytesPerSecond, _settings.DefaultSegmentCount, referrer,
            cookie, userAgent, startImmediately: true);
        await _settingsStore.SaveAsync(_settings);
        Status.Text = "Download queued";
    }

    private sealed class DownloadRow : System.ComponentModel.INotifyPropertyChanged
    {
        public DownloadRow(Guid id, string fileName, string state) { Id = id; FileName = fileName; State = state; }
        public Guid Id { get; }
        public string FileName { get; }
        private string _state = string.Empty;
        private double _progress;
        public string State { get => _state; set { _state = value; PropertyChanged?.Invoke(this, new(nameof(State))); } }
        public double Progress { get => _progress; set { _progress = value; PropertyChanged?.Invoke(this, new(nameof(Progress))); } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
