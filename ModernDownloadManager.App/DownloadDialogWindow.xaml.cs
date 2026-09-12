using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using ModernDownloadManager.Core.Engine;
using ModernDownloadManager.Core.Models;
using ModernDownloadManager.Core.Scheduling;
using ModernDownloadManager.App.ViewModels;
using WinRT.Interop;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace ModernDownloadManager.App;

public sealed partial class DownloadDialogWindow : Window
{
    private readonly DownloadQueueManager _queue;
    private readonly Func<MainWindow.DownloadDialogResult, Task<DownloadItem>> _enqueue;
    private readonly Func<DownloadCategory, string> _folderForCategory;
    private readonly string? _suggestedFileName;
    private bool _accepting;
    private readonly DateTimeOffset _captureOpenedAt = DateTimeOffset.UtcNow;
    private string _initialFileName = string.Empty;
    private DownloadItem? _item;
    private DispatcherQueueTimer? _stateTimer;
    private bool _progressMode;
    private DownloadState? _lastDisplayedState;
    private string? _lastDisplayedError;
    private bool _closing;

    private readonly TaskCompletionSource<DownloadItem?> _acceptance = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<DownloadItem?> Acceptance => _acceptance.Task;
    public Task<DownloadItem?> Completion => _completion.Task;
    private readonly TaskCompletionSource<DownloadItem?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DownloadDialogWindow(string url, string? suggestedFileName, DownloadQueueManager queue,
        Func<DownloadCategory, string> folderForCategory,
        Func<MainWindow.DownloadDialogResult, Task<DownloadItem>> enqueue, long totalBytes = -1)
    {
        _queue = queue;
        _folderForCategory = folderForCategory;
        _enqueue = enqueue;
        _suggestedFileName = suggestedFileName;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));

        UrlBox.Text = url;
        CaptureSize.Text = totalBytes >= 0 ? FormatBytes(totalBytes) : "Size unknown";
        CategoryBox.ItemsSource = Enum.GetValues<DownloadCategory>();
        CategoryBox.SelectedItem = DownloadCategoryResolver.Resolve(suggestedFileName ?? url);
        SaveAsBox.Text = Path.Combine(folderForCategory((DownloadCategory)CategoryBox.SelectedItem!), SuggestedName(url));
        _initialFileName = Path.GetFileName(SaveAsBox.Text);
        UpdateCategoryIcon((DownloadCategory)CategoryBox.SelectedItem!);
        CategoryBox.SelectionChanged += (_, _) =>
        {
            if (CategoryBox.SelectedItem is DownloadCategory category)
            {
                SaveAsBox.Text = Path.Combine(_folderForCategory(category), Path.GetFileName(SaveAsBox.Text));
                UpdateCategoryIcon(category);
            }
        };

        AppWindow.Title = "Download File Info";
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        // AppWindow.Resize includes the non-client title-bar area. Leave enough
        // client height for the footer so the action buttons are never clipped.
        ResizeDialog(850, 390);
        Closed += OnClosed;
    }

    internal DownloadDialogWindow(DownloadItem item, DownloadQueueManager queue)
        : this(item.Url, item.FileName, queue, _ => item.DestinationDirectory,
            _ => Task.FromResult(item))
    {
        ConfigureProgress(item);
    }

    internal void ShowAndFocus()
    {
        AppWindow.Show();
        Activate();
        DispatcherQueue.TryEnqueue(FocusWindow);
        FocusWindow();
    }

    private void FocusWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                       AttachThreadInput(foregroundThread, currentThread, true);
        try
        {
            ShowWindow(hwnd, SwRestore);
            AllowSetForegroundWindow(-1);
            BringWindowToTop(hwnd);
            SetActiveWindow(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(foregroundThread, currentThread, false);
        }
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_progressMode) { await AcceptAsync(startNow: true); return; }
        if (_item is null) return;
        if (_item.State == DownloadState.Completed) { OpenCompletedFile(); return; }
        _stateTimer?.Start();
        if (_item.State is DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled)
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
        if (_item?.State == DownloadState.Completed)
        {
            OpenCompletedFolder();
            return;
        }
        if (_item is not null)
            await _queue.CancelAsync(_item.Id);
        ProgressState.Text = "Cancelled";
        UpdateState(DownloadState.Cancelled, null);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_accepting) Close();
    }

    private async Task AcceptAsync(bool startNow)
    {
        if (_accepting) return;
        _accepting = true;
        SetupError.Text = string.Empty;
        var category = CategoryBox.SelectedItem is DownloadCategory selected
            ? selected : DownloadCategory.General;
        try
        {
            if (DateTimeOffset.UtcNow - _captureOpenedAt > TimeSpan.FromMinutes(9))
                throw new InvalidOperationException("This capture prompt has expired. Close it and retry the download from your browser.");
            var path = SaveAsBox.Text.Trim();
            if (!Path.IsPathFullyQualified(path) || string.IsNullOrWhiteSpace(Path.GetFileName(path)))
                throw new ArgumentException("Choose a full file path, including a file name.");
            var folder = Path.GetDirectoryName(path)!;
            var result = new MainWindow.DownloadDialogResult(folder, category, startNow,
                RememberBox.IsChecked == true, Path.GetFileName(path) == _initialFileName
                    ? _suggestedFileName : Path.GetFileName(path));
            PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = CloseButton.IsEnabled = false;
            _item = await _enqueue(result);
            _acceptance.TrySetResult(_item);
            if (!startNow) { Close(); return; }
            if (BackgroundBox.IsChecked == true) { Close(); return; }
            ConfigureProgress(_item);
        }
        catch (Exception ex) { SetupError.Text = ex.Message; }
        finally { _accepting = false; CloseButton.IsEnabled = true; if (!_progressMode) PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = true; }
    }

    private void ConfigureProgress(DownloadItem item)
    {
        _item = item;
        _queue.ProgressChanged += OnProgressChanged;
        _queue.StateChanged += OnStateChanged;
        _progressMode = true;
        ResizeDialog(850, 490);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsMinimizable = true;
        ProgressAddress.Text = item.Url;
        ConnectionInfo.Text = $"Configured connections: {item.SegmentCount}";
        PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = true;
        SetupScroller.Visibility = Visibility.Collapsed;
        ProgressScroller.Visibility = Visibility.Visible;
        SetupPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        PrimaryButton.Content = item.State == DownloadState.Cancelled
            ? "Download Now"
            : item.State is DownloadState.Paused or DownloadState.Failed ? "Resume" : "Pause";
        SecondaryButton.Content = "Cancel";
        CloseButton.Content = "Close";
        ProgressFileName.Text = item.FileName;
        ProgressLocation.Text = $"Saving to: {item.DestinationDirectory}";
        UpdateProgress(item.DownloadedBytes, item.TotalBytes, 0, null);
        UpdateState(item.State, item.ErrorMessage);

        // State events normally arrive immediately, but polling the shared
        // model also covers a completion event that was queued while the
        // window was being closed/reopened.
        _stateTimer ??= DispatcherQueue.CreateTimer();
        _stateTimer.Interval = TimeSpan.FromMilliseconds(250);
        _stateTimer.Tick -= StateTimer_Tick;
        _stateTimer.Tick += StateTimer_Tick;
        _stateTimer.Start();
    }

    private void StateTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_item is null) return;
        if (_lastDisplayedState != _item.State || _lastDisplayedError != _item.ErrorMessage)
            UpdateState(_item.State, _item.ErrorMessage);
        if (_item.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled)
            sender.Stop();
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
            UpdateState(e.State, e.ErrorMessage));
    }

    private void UpdateState(DownloadState state, string? error)
    {
        _lastDisplayedState = state;
        _lastDisplayedError = error;
        if (_item is not null)
        {
            ProgressFileName.Text = _item.FileName;
            ProgressLocation.Text = $"Saving to: {_item.DestinationDirectory}";
        }
        ProgressError.Text = state == DownloadState.Failed ? error ?? "The download failed." : string.Empty;
        ProgressState.Text = state switch
        {
            DownloadState.Downloading => "Receiving data...",
            DownloadState.Merging => "Finishing",
            DownloadState.Completed => "Completed",
            _ => state.ToString()
        };
        AppWindow.Title = state switch
        {
            DownloadState.Merging => "Finishing download",
            DownloadState.Completed => "Download complete",
            DownloadState.Failed => "Download failed",
            _ => $"{ProgressBar.Value:0}% {_item?.FileName}"
        };

        ResumeCapability.Text = _item?.SupportsResume == true ? "Yes" : "Not confirmed";
        PrimaryButton.IsEnabled = state is DownloadState.Downloading or DownloadState.Connecting
            or DownloadState.Queued or DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled;
        SecondaryButton.IsEnabled = state is not DownloadState.Cancelled and not DownloadState.Merging;
        if (state != DownloadState.Downloading)
        {
            ProgressSpeed.Text = "-";
            ProgressEta.Text = "-";
        }
        if (state == DownloadState.Merging)
        {
            ProgressBar.IsIndeterminate = true;
            ProgressSpeed.Text = string.Empty;
            ProgressEta.Text = string.Empty;
        }
        else
        {
            ProgressBar.IsIndeterminate = false;
        }

        if (state == DownloadState.Completed)
        {
            UpdateProgress(_item?.DownloadedBytes ?? 0, _item?.TotalBytes ?? 0, 0, null);
            ProgressBar.Value = 100;
            PrimaryButton.Content = "Open";
            PrimaryButton.IsEnabled = _item is not null && File.Exists(_item.FullPath);
            SecondaryButton.IsEnabled = true;
            CloseButton.IsEnabled = true;
            SecondaryButton.Content = "Open folder";
            CloseButton.Content = "Close";
        }
        else if (state == DownloadState.Failed)
        {
            PrimaryButton.Content = "Resume";
            SecondaryButton.Content = "Cancel";
        }
        else if (state == DownloadState.Paused)
            PrimaryButton.Content = "Resume";
        else if (state == DownloadState.Cancelled)
            PrimaryButton.Content = "Download Now";
        else if (state is DownloadState.Downloading or DownloadState.Connecting)
            PrimaryButton.Content = "Pause";
    }

    private void OpenCompletedFile()
    {
        if (_item is null || !File.Exists(_item.FullPath)) return;
        Process.Start(new ProcessStartInfo(_item.FullPath) { UseShellExecute = true });
    }

    private void OpenCompletedFolder()
    {
        if (_item is null || !Directory.Exists(_item.DestinationDirectory)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_item.FullPath}\"")
        {
            UseShellExecute = true
        });
    }

    private void UpdateProgress(long downloaded, long total, double speed, TimeSpan? eta)
    {
        if (_item?.State == DownloadState.Completed)
        {
            downloaded = Math.Max(_item.DownloadedBytes, total);
            speed = 0;
            eta = null;
        }
        if (_item?.State != DownloadState.Downloading) { speed = 0; eta = null; }
        ProgressBar.Value = total > 0 ? Math.Clamp((double)downloaded / total * 100, 0, 100) : 0;
        ProgressTotal.Text = total > 0 ? FormatBytes(total) : "Unknown";
        ProgressSize.Text = total > 0 ? $"{FormatBytes(downloaded)}  ({ProgressBar.Value:0.##}%)" : FormatBytes(downloaded);
        if (_item?.State == DownloadState.Downloading) AppWindow.Title = $"{ProgressBar.Value:0}% {_item.FileName}";
        ProgressSpeed.Text = speed > 0 ? $"{FormatBytes((long)speed)}/s" : "-";
        ProgressEta.Text = eta is { } value ? $"{(int)value.TotalMinutes} min {value.Seconds} sec" : "-";
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
        _stateTimer?.Stop();
        _queue.ProgressChanged -= OnProgressChanged;
        _queue.StateChanged -= OnStateChanged;
        if (!_closing)
        {
            _closing = true;
            _acceptance.TrySetResult(_item);
            _completion.TrySetResult(_item);
        }
    }

    private void UpdateCategoryIcon(DownloadCategory category)
    {
        FileTypeIcon.Glyph = category switch
        {
            DownloadCategory.Video => "\uE714",
            DownloadCategory.Music => "\uE8D6",
            DownloadCategory.Compressed => "\uE7B8",
            DownloadCategory.Images => "\uEB9F",
            DownloadCategory.Programs => "\uE756",
            _ => "\uE8A5"
        };
        RememberBox.Content = $"Remember path for the {category} category";
    }

    private string SuggestedName(string url)
    {
        if (!string.IsNullOrWhiteSpace(_suggestedFileName)) return Path.GetFileName(_suggestedFileName);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(Path.GetFileName(uri.LocalPath))
            ? Path.GetFileName(uri.LocalPath) : "download";
    }

    private async void BrowseSave_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) SaveAsBox.Text = Path.Combine(folder.Path, Path.GetFileName(SaveAsBox.Text));
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        var show = DetailsPanel.Visibility != Visibility.Visible;
        DetailsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Content = show ? "Hide details" : "Show details";
        ResizeDialog(850, show ? 650 : 490);
    }

    private void ResizeDialog(int width, int height)
    {
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

}
