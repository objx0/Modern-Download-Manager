using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ModernDownloadManager.App.ViewModels;
using ModernDownloadManager.Core.Models;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using WinRT; // required for Window.As<T>() used by the Mica backdrop
using WinRT.Interop;

namespace ModernDownloadManager.App;

[SupportedOSPlatform("windows10.0.17763.0")]
public sealed partial class MainWindow : Window
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

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
        RootGrid.DataContext = ViewModel;
        ViewModel.ConfirmRemoveAsync = ConfirmRemoveAsync;
        foreach (var item in ViewModel.Downloads)
            item.ConfirmRemoveAsync = ConfirmRemoveAsync;
        ViewModel.Settings.TrayIconSettingChanged = enabled => (Application.Current as App)?.SetTrayIconEnabled(enabled);

        SetupTitleBar();
        TrySetMicaBackdrop();
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
        Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private async void AddDownload_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.NewUrlText))
        {
            ViewModel.StatusText = "Paste a URL first.";
            return;
        }

        var result = await ShowDownloadDialogAsync(ViewModel.NewUrlText);
        if (result is null)
            return;

        await ViewModel.EnqueueDownloadAsync(ViewModel.NewUrlText, result.DestinationDirectory,
            result.Category, result.StartNow, result.SuggestedFileName);
        RememberFolder(result);
    }

    public async Task<DownloadDialogResult?> ShowDownloadDialogAsync(string url, string? suggestedFileName = null)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            var completion = new TaskCompletionSource<DownloadDialogResult?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherQueue.TryEnqueue(async () =>
            {
                try { completion.SetResult(await ShowDownloadDialogAsync(url, suggestedFileName)); }
                catch (Exception ex) { completion.SetException(ex); }
            });
            return await completion.Task;
        }

        // Native messaging can arrive while the browser still owns focus.
        // Activate and foreground the app before creating the ContentDialog so
        // the first captured URL is visible instead of opening behind the tab.
        BringToFront();

        var inferredCategory = DownloadCategoryResolver.Resolve(suggestedFileName ?? url);
        var category = new ComboBox
        {
            ItemsSource = Enum.GetValues<DownloadCategory>(),
            SelectedItem = inferredCategory,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var saveAs = new TextBox
        {
            Text = GetRememberedFolder(inferredCategory),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var remember = new CheckBox { Content = "Remember path for this category", IsChecked = true };
        category.SelectionChanged += (_, _) =>
        {
            if (category.SelectedItem is DownloadCategory selected)
                saveAs.Text = GetRememberedFolder(selected);
        };
        var content = new StackPanel { Spacing = 6, MinWidth = 420 };
        content.Children.Add(new TextBlock { Text = "URL", Opacity = 0.75 });
        content.Children.Add(new TextBox { Text = url, IsReadOnly = true });
        content.Children.Add(new TextBlock { Text = "Category", Opacity = 0.75, Margin = new Thickness(0, 8, 0, 0) });
        content.Children.Add(category);
        content.Children.Add(new TextBlock { Text = "Save As", Opacity = 0.75, Margin = new Thickness(0, 8, 0, 0) });
        content.Children.Add(saveAs);
        content.Children.Add(remember);

        var dialog = new ContentDialog
        {
            Title = "Download File",
            Content = content,
            PrimaryButtonText = "Download Now",
            SecondaryButtonText = "Download Later",
            CloseButtonText = "Cancel",
            XamlRoot = RootGrid.XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.Opened += (_, _) => ForceForeground();
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
            return null;

        var selectedCategory = category.SelectedItem is DownloadCategory c ? c : inferredCategory;
        var folder = string.IsNullOrWhiteSpace(saveAs.Text) ? GetRememberedFolder(selectedCategory) : saveAs.Text.Trim();
        return new DownloadDialogResult(folder, selectedCategory,
            result == ContentDialogResult.Primary, remember.IsChecked == true, suggestedFileName);
    }

    private void BringToFront()
    {
        AppWindow.Show();
        Activate();
        var hwnd = WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SwRestore);
        ForceForeground(hwnd);
    }

    private void ForceForeground(nint? targetHandle = null)
    {
        var target = targetHandle ?? WindowNative.GetWindowHandle(this);
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                       AttachThreadInput(foregroundThread, currentThread, true);
        try
        {
            AllowSetForegroundWindow(-1);
            ShowWindow(target, SwRestore);
            BringWindowToTop(target);
            SetActiveWindow(target);
            SetForegroundWindow(target);
        }
        finally
        {
            if (attached)
                AttachThreadInput(foregroundThread, currentThread, false);
        }
    }

    private delegate nint WindowProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newValue);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint previousProc, nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

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

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ViewModel.IsSettingsView = true;
            return;
        }

        ViewModel.IsSettingsView = false;

        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string;
        if (string.IsNullOrEmpty(tag))
            return;

        if (tag.StartsWith("Category:"))
        {
            ViewModel.CurrentFilter = FilterMode.Category;
            ViewModel.CurrentCategory = Enum.Parse<DownloadCategory>(tag["Category:".Length..]);
        }
        else
        {
            ViewModel.CurrentFilter = Enum.Parse<FilterMode>(tag);
        }
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add("*");

        // Unpackaged WinUI 3 apps need the window handle wired to the picker
        // explicitly — without this it throws instead of opening.
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            ViewModel.Settings.DefaultDownloadFolder = folder.Path;
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
