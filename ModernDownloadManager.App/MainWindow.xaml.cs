using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ModernDownloadManager.App.ViewModels;
using ModernDownloadManager.Core.Models;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using WinRT; // required for Window.As<T>() used by the Mica backdrop
using WinRT.Interop;

namespace ModernDownloadManager.App;

[SupportedOSPlatform("windows10.0.17763.0")]
public sealed partial class MainWindow : Window
{
    public sealed record DownloadDialogResult(string DestinationDirectory, DownloadCategory Category,
        bool StartNow, bool RememberCategory, string? SuggestedFileName);

    public MainViewModel ViewModel { get; }

    private MicaController? _micaController;
    private SystemBackdropConfiguration? _backdropConfig;
    private WindowProcDelegate? _windowProc;
    private nint _originalWindowProc;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Title = "Modern Download Manager";
        AppWindow.Title = "Modern Download Manager";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        RootGrid.DataContext = ViewModel;
        ViewModel.ConfirmRemoveAsync = ConfirmRemoveAsync;
        ViewModel.OpenDownloadWindowAsync = OpenDownloadWindowAsync;
        foreach (var item in ViewModel.Downloads)
        {
            item.ConfirmRemoveAsync = ConfirmRemoveAsync;
            item.OpenDownloadWindowAsync = OpenDownloadWindowAsync;
        }
        ViewModel.Settings.TrayIconSettingChanged = enabled => (Application.Current as App)?.SetTrayIconEnabled(enabled);
        ViewModel.Settings.StartupSettingChanged = enabled => (Application.Current as App)?.ConfigureStartup(enabled);
        ViewModel.Settings.PreventSleepSettingChanged = enabled => (Application.Current as App)?.ApplyPreventSleepSetting(enabled);

        Closed += (_, _) =>
        {
            _optionsWindow?.Close();
            _aboutWindow?.Close();
        };
        SetupTitleBar();
        TrySetMicaBackdrop();
        CategoriesTree.SelectedNode = CategoriesTree.RootNodes[0];
    }

    internal void InstallCloseToTrayHandler()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        _windowProc = NativeWindowProc;
        _originalWindowProc = SetWindowLongPtr(hwnd, -4, Marshal.GetFunctionPointerForDelegate(_windowProc));
    }

    private nint NativeWindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        const uint WmClose = 0x0010;
        if (message == WmClose && (Application.Current as App)?.ShouldHideOnClose == true)
        {
            AppWindow.Hide();
            return 0;
        }

        return CallWindowProc(_originalWindowProc, hwnd, message, wParam, lParam);
    }

    internal void RequestExitFromTray() => (Application.Current as App)?.ExitFromTray();

    /// <summary>
    /// Replaces the default plain system title bar with a custom Fluent-styled
    /// title-bar surface that integrates with NavigationView and Mica.
    /// </summary>
    private void SetupTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Make the system caption buttons (min/max/close) transparent so the
        // Mica backdrop shows through them instead of sitting on a solid plate.
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
    }

    private void AppTitleBar_PaneToggleRequested(object sender, RoutedEventArgs args) =>
        CategoriesPane.Visibility = CategoriesPane.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;

    private async void AddDownload_Click(object sender, RoutedEventArgs e)
    {
        var url = ViewModel.NewUrlText?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            var urlInput = new TextBox
            {
                PlaceholderText = "https://example.com/file.zip",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 420
            };
            var urlDialog = new ContentDialog
            {
                Title = "New download",
                Content = urlInput,
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            if (await urlDialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            url = urlInput.Text.Trim();
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            ViewModel.StatusText = "Enter a valid HTTP or HTTPS URL.";
            return;
        }

        await ShowDownloadDialogAndEnqueueAsync(url);
        ViewModel.NewUrlText = string.Empty;
    }

    public async Task<DownloadItem?> ShowDownloadDialogAndEnqueueAsync(string url, string? suggestedFileName = null,
        string? referrer = null, string? cookie = null, string? userAgent = null, long totalBytes = -1, bool waitForAcceptance = false)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var completion = new TaskCompletionSource<DownloadItem?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherQueue.TryEnqueue(async () =>
            {
                try { completion.SetResult(await ShowDownloadDialogAndEnqueueAsync(url, suggestedFileName, referrer, cookie, userAgent, totalBytes, waitForAcceptance)); }
                catch (Exception ex) { completion.SetException(ex); }
            });
            return await completion.Task;
        }

        // Native messaging can arrive while the browser still owns focus.
        AppWindow.Hide();
        var progressWindow = new DownloadDialogWindow(url, suggestedFileName, App.QueueManager!, GetRememberedFolder,
            async result =>
            {
                RememberFolder(result);
                return await App.QueueManager!.EnqueueAsync(url, result.DestinationDirectory,
                    result.SuggestedFileName, ViewModel.Settings.CurrentEffectiveSettings.DefaultSpeedLimitBytesPerSecond,
                    ViewModel.Settings.CurrentEffectiveSettings.DefaultSegmentCount, referrer, cookie, userAgent,
                    result.Category, result.StartNow);
            }, totalBytes);
        progressWindow.Closed += (_, _) => AppWindow.Show();
        progressWindow.ShowAndFocus();
        var item = await (waitForAcceptance ? progressWindow.Acceptance : progressWindow.Completion);
        if (!waitForAcceptance) AppWindow.Show();
        return item;
    }

    private async Task OpenDownloadWindowAsync(DownloadItemViewModel viewModel)
    {
        var item = viewModel.Model;
        if (!viewModel.CanShowDownloadWindow || App.QueueManager is null)
            return;

        AppWindow.Hide();
        var progressWindow = new DownloadDialogWindow(item, App.QueueManager);
        progressWindow.ShowAndFocus();
        await progressWindow.Completion;
        AppWindow.Show();
    }

    private delegate nint WindowProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newValue);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint previousProc, nint hwnd, uint message, nint wParam, nint lParam);

    private string GetRememberedFolder(DownloadCategory category) =>
        ViewModel.Settings.CurrentEffectiveSettings.CategoryDownloadFolders.TryGetValue(category.ToString(), out var folder)
            ? folder : ViewModel.Settings.CurrentEffectiveSettings.DefaultDownloadFolder;

    private void RememberFolder(DownloadDialogResult result)
    {
        if (!result.RememberCategory)
            return;
        ViewModel.Settings.CurrentEffectiveSettings.CategoryDownloadFolders[result.Category.ToString()] = result.DestinationDirectory;
        _ = App.SettingsStore?.SaveAsync(ViewModel.Settings.CurrentEffectiveSettings);
    }

    private void TrySetMicaBackdrop()
    {
        if (!MicaController.IsSupported())
            return; // falls back to the default solid background on older Windows builds

        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
        Activated += (_, e) =>
            _backdropConfig!.IsInputActive = e.WindowActivationState != WindowActivationState.Deactivated;
        Closed += (_, _) =>
        {
            _micaController?.Dispose();
            _micaController = null;
        };

        _micaController = new MicaController { Kind = MicaKind.BaseAlt };
        _micaController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
        _micaController.SetSystemBackdropConfiguration(_backdropConfig);
    }

    private void Categories_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node || node.Content is not string name)
            return;
        ViewModel.IsSettingsView = false;
        ViewModel.IsAboutView = false;
        if (Enum.TryParse<DownloadCategory>(name, out var category))
        {
            ViewModel.CurrentCategory = category;
            ViewModel.CurrentFilter = FilterMode.Category;
        }
        else
        {
            ViewModel.CurrentFilter = name switch
            {
                "Unfinished" => FilterMode.Active,
                "Finished" => FilterMode.Completed,
                "Queued" => FilterMode.Queued,
                _ => FilterMode.All
            };
        }
        ViewModel.RefreshHeaderText();
    }

    private void AllDownloads_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsSettingsView = false;
        ViewModel.IsAboutView = false;
        ViewModel.CurrentFilter = FilterMode.All;
        ViewModel.RefreshHeaderText();
        CategoriesTree.SelectedNode = CategoriesTree.RootNodes[0];
    }

    private InformationWindow? _optionsWindow;
    private InformationWindow? _aboutWindow;

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        if (_optionsWindow is null)
        {
            _optionsWindow = new InformationWindow(ViewModel, this, options: true);
            _optionsWindow.Closed += (_, _) => _optionsWindow = null;
        }
        _optionsWindow.AppWindow.Show();
        _optionsWindow.Activate();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        if (_aboutWindow is null)
        {
            _aboutWindow = new InformationWindow(ViewModel, this, options: false);
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        }
        _aboutWindow.AppWindow.Show();
        _aboutWindow.Activate();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => RequestExitFromTray();

    private async void DownloadsTable_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedDownload is not { } item) return;
        if (item.CanOpenFile) item.OpenFileCommand.Execute(null);
        else if (item.CanShowDownloadWindow) await OpenDownloadWindowAsync(item);
    }

    private async void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        var completed = ViewModel.Downloads.Where(d => d.State == DownloadState.Completed).ToArray();
        if (completed.Length == 0)
        {
            ViewModel.StatusText = "No completed downloads to clear.";
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "Clear completed downloads?",
            Content = $"Remove {completed.Length} completed download(s) from the list? Your files will be kept.",
            PrimaryButtonText = "Clear list",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var item in completed)
            await item.RemoveFromListAsync();
    }

    private async Task<RemoveDecision> ConfirmRemoveAsync(DownloadItemViewModel item)
    {
        var dialog = new ContentDialog
        {
            Title = "Remove download?",
            Content = $"What should happen to \"{item.FileName}\"?",
            PrimaryButtonText = "Delete file",
            SecondaryButtonText = "Remove from list",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => RemoveDecision.DeleteFile,
            ContentDialogResult.Secondary => RemoveDecision.RemoveFromList,
            _ => RemoveDecision.Cancel
        };
    }
}
