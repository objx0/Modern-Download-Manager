using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ModernDownloadManager.Core.Engine;
using ModernDownloadManager.Core.Models;
using ModernDownloadManager.Core.Scheduling;
using ModernDownloadManager.App.ViewModels;
using WinRT.Interop;

namespace ModernDownloadManager.App;

public sealed partial class DownloadDialogWindow : Window
{
    private readonly DownloadQueueManager _queue;
    private readonly Func<MainWindow.DownloadDialogResult, Task<DownloadItem>> _enqueue;
    private readonly Func<DownloadCategory, string> _folderForCategory;
    private readonly string? _suggestedFileName;
    private DownloadItemViewModel? _viewModel;
    private DownloadItem? _item;
    private bool _progressMode;
    private bool _closing;

    public Task<DownloadItem?> Completion => _completion.Task;
    private readonly TaskCompletionSource<DownloadItem?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DownloadDialogWindow(string url, string? suggestedFileName, DownloadQueueManager queue,
        Func<DownloadCategory, string> folderForCategory,
        Func<MainWindow.DownloadDialogResult, Task<DownloadItem>> enqueue)
    {
        _queue = queue;
        _folderForCategory = folderForCategory;
        _enqueue = enqueue;
        _suggestedFileName = suggestedFileName;
        InitializeComponent();

        UrlBox.Text = url;
        CategoryBox.ItemsSource = Enum.GetValues<DownloadCategory>();
        CategoryBox.SelectedItem = DownloadCategoryResolver.Resolve(suggestedFileName ?? url);
        SaveAsBox.Text = folderForCategory((DownloadCategory)CategoryBox.SelectedItem!);
        CategoryBox.SelectionChanged += (_, _) =>
        {
            if (CategoryBox.SelectedItem is DownloadCategory category)
                SaveAsBox.Text = _folderForCategory(category);
        };

        AppWindow.Title = "Download File";
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(680, 510));
        Closed += OnClosed;
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_progressMode) { await AcceptAsync(startNow: true); return; }
        if (_item is null) return;
        if (_item.State is DownloadState.Paused or DownloadState.Failed)
        {
            await _queue.ResumeAsync(_item.Id);
            PrimaryButton.Content = "Pause";
        }
        else
        {
            await _queue.PauseAsync(_item.Id);
            PrimaryButton.Content = "Resume";
        }
    }

    private async void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_progressMode) { await AcceptAsync(startNow: false); return; }
        if (_item is not null)
            await _queue.CancelAsync(_item.Id);
        ProgressState.Text = "Cancelled";
        PrimaryButton.IsEnabled = false;
        SecondaryButton.IsEnabled = false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task AcceptAsync(bool startNow)
    {
        var category = CategoryBox.SelectedItem is DownloadCategory selected
            ? selected : DownloadCategory.General;
        var folder = string.IsNullOrWhiteSpace(SaveAsBox.Text)
            ? _folderForCategory(category) : SaveAsBox.Text.Trim();
        var result = new MainWindow.DownloadDialogResult(folder, category, startNow,
            RememberBox.IsChecked == true, _suggestedFileName);

        _item = await _enqueue(result);
        _queue.ProgressChanged += OnProgressChanged;
        _queue.StateChanged += OnStateChanged;
        _viewModel = new DownloadItemViewModel(_item, _queue);
        _viewModel.ApplyState(_item.State);
        _viewModel.ApplyProgress(_item.DownloadedBytes, _item.TotalBytes, 0, null);
        _progressMode = true;
        SetupPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        PrimaryButton.Content = startNow ? "Pause" : "Resume";
        SecondaryButton.Content = "Cancel";
        CloseButton.Content = "Close";
        ProgressFileName.Text = _item.FileName;
        UpdateProgress(_item.DownloadedBytes, _item.TotalBytes, 0, null);
    }

    private void OnProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        if (_item?.Id != e.DownloadId) return;
        DispatcherQueue.TryEnqueue(() => UpdateProgress(e.DownloadedBytes, e.TotalBytes, e.BytesPerSecond, e.Eta));
    }

    private void OnStateChanged(object? sender, DownloadStateChangedEventArgs e)
    {
        if (_item?.Id != e.DownloadId) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            ProgressState.Text = e.State.ToString();
            if (e.State == DownloadState.Completed)
            {
                ProgressBar.Value = 100;
                PrimaryButton.IsEnabled = false;
                SecondaryButton.IsEnabled = false;
            }
            else if (e.State == DownloadState.Paused)
                PrimaryButton.Content = "Resume";
            else if (e.State is DownloadState.Downloading or DownloadState.Connecting)
                PrimaryButton.Content = "Pause";
        });
    }

    private void UpdateProgress(long downloaded, long total, double speed, TimeSpan? eta)
    {
        ProgressBar.Value = total > 0 ? Math.Clamp((double)downloaded / total * 100, 0, 100) : 0;
        ProgressSize.Text = $"{FormatBytes(downloaded)} / {FormatBytes(total)}";
        ProgressSpeed.Text = speed > 0 ? $"{FormatBytes((long)speed)}/s" : string.Empty;
        ProgressEta.Text = eta is { } value ? $"{(int)value.TotalSeconds}s left" : string.Empty;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _queue.ProgressChanged -= OnProgressChanged;
        _queue.StateChanged -= OnStateChanged;
        if (!_closing)
        {
            _closing = true;
            _completion.TrySetResult(_item);
        }
    }

}
