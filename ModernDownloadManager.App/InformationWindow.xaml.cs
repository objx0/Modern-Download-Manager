using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using ModernDownloadManager.App.ViewModels;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace ModernDownloadManager.App;

public sealed partial class InformationWindow : Window
{
    private readonly MainViewModel _viewModel;

    public InformationWindow(MainViewModel viewModel, Window owner, bool options)
    {
        _viewModel = viewModel;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        Root.DataContext = viewModel;
        AboutPanel.Visibility = options ? Visibility.Collapsed : Visibility.Visible;
        OptionsPanel.Visibility = options ? Visibility.Visible : Visibility.Collapsed;
        AppWindow.Title = options ? "Options - Modern Download Manager" : "About Modern Download Manager";
        var hwnd = WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(hwnd, -8, WindowNative.GetWindowHandle(owner));
        var scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)((options ? 840 : 720) * scale),
            (int)((options ? 720 : 420) * scale)));
        AppWindow.Move(new Windows.Graphics.PointInt32(
            owner.AppWindow.Position.X + Math.Max(0, (owner.AppWindow.Size.Width - AppWindow.Size.Width) / 2),
            owner.AppWindow.Position.Y + Math.Max(0, (owner.AppWindow.Size.Height - AppWindow.Size.Height) / 2)));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenRepository_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_viewModel.RepositoryUrl) { UseShellExecute = true });

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) _viewModel.Settings.DefaultDownloadFolder = folder.Path;
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
