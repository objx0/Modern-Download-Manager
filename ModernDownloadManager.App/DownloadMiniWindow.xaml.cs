using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ModernDownloadManager.App.ViewModels;
using ModernDownloadManager.Core.Engine;
using ModernDownloadManager.Core.Models;
using ModernDownloadManager.Core.Scheduling;
using WinRT.Interop;
using System.Runtime.InteropServices;

namespace ModernDownloadManager.App;

public sealed partial class DownloadMiniWindow : Window
{
    private readonly DownloadQueueManager _queue;
    private WindowProcDelegate? _windowProc;
    private nint _originalWindowProc;
    public DownloadItemViewModel ViewModel { get; }

    public DownloadMiniWindow(DownloadItem item, DownloadQueueManager queue)
    {
        _queue = queue;
        ViewModel = new DownloadItemViewModel(item, queue);
        InitializeComponent();
        Root.DataContext = ViewModel;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(MiniTitleBar);
        AppWindow.Title = "Modern Download Manager";
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }
        ViewModel.ApplyState(item.State);
        ViewModel.ApplyProgress(item.DownloadedBytes, item.TotalBytes, 0, null);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 320));
        InstallCloseHandler();
        Closed += OnClosed;
        _queue.ProgressChanged += OnProgressChanged;
        _queue.StateChanged += OnStateChanged;
        Activate();
    }

    internal void RestoreFromManager()
    {
        AppWindow.Show();
        Activate();
        var hwnd = WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, 9);
        SetForegroundWindow(hwnd);
    }

    private void OnProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        if (e.DownloadId != ViewModel.Id) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            try { ViewModel.ApplyProgress(e.DownloadedBytes, e.TotalBytes, e.BytesPerSecond, e.Eta); }
            catch (Exception exception) { CrashLog.Write("Mini window progress update", exception); }
        });
    }

    private void OnStateChanged(object? sender, DownloadStateChangedEventArgs e)
    {
        if (e.DownloadId != ViewModel.Id) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            try { ViewModel.ApplyState(e.State); }
            catch (Exception exception) { CrashLog.Write("Mini window state update", exception); }
        });
    }

    private void InstallCloseHandler()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        _windowProc = MiniWindowProc;
        _originalWindowProc = SetWindowLongPtr(hwnd, -4, Marshal.GetFunctionPointerForDelegate(_windowProc));
    }

    private nint MiniWindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        const uint WmClose = 0x0010;
        if (message == WmClose)
        {
            AppWindow.Hide();
            return 0;
        }

        return CallWindowProc(_originalWindowProc, hwnd, message, wParam, lParam);
    }

    private void OpenManager_Click(object sender, RoutedEventArgs e)
    {
        if (App.MainAppWindow is MainWindow main)
        {
            main.AppWindow.Show();
            main.Activate();
            SetForegroundWindow(WindowNative.GetWindowHandle(main));
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _queue.ProgressChanged -= OnProgressChanged;
        _queue.StateChanged -= OnStateChanged;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    private delegate nint WindowProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newValue);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint previousProc, nint hwnd, uint message, nint wParam, nint lParam);
}
