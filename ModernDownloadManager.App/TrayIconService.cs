using System.Runtime.InteropServices;
using ModernDownloadManager.Core.Models;

namespace ModernDownloadManager.App;

/// <summary>Small Win32 notification-area host for the unpackaged WinUI app.</summary>
internal sealed class TrayIconService : IDisposable
{
    private const int WmTray = 0x8001;
    private const int WmCommand = 0x0111;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDoubleClick = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int TrayId = 1;
    private const int RestoreCommand = 1001;
    private const int ExitCommand = 1002;
    private static readonly IntPtr IdiApplication = new(32512);

    private readonly MainWindow _window;
    private readonly WndProcDelegate _wndProc;
    private readonly string _className = "ModernDownloadManager.Tray." + Guid.NewGuid().ToString("N");
    private IntPtr _messageWindow;
    private IntPtr _icon;
    private bool _disposed;

    public TrayIconService(MainWindow window)
    {
        _window = window;
        _wndProc = WindowProc;
        var instance = GetModuleHandle(null);
        var wndClass = new WndClass
        {
            Size = (uint)Marshal.SizeOf<WndClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = instance,
            ClassName = _className
        };
        if (RegisterClassEx(ref wndClass) == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows could not register the tray window class.");
        // Use a normal hidden tool window rather than a message-only window.
        // Explorer accepts this HWND consistently for notification icons.
        _messageWindow = CreateWindowEx(0x00000080, _className, "Modern Download Manager", unchecked((int)0x80000000), 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_messageWindow == IntPtr.Zero)
            throw new InvalidOperationException("Windows could not create the tray window.");
        _icon = LoadIcon(IntPtr.Zero, IdiApplication);
        AddTrayIcon("Modern Download Manager");
    }

    public void UpdateTip(string tip) => AddTrayIcon(tip);

    private void AddTrayIcon(string tip)
    {
        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = _messageWindow,
            Id = TrayId,
            Flags = 0x00000001 | 0x00000002 | 0x00000004,
            CallbackMessage = WmTray,
            Icon = _icon,
            Tip = tip,
            Info = string.Empty,
            InfoTitle = string.Empty
        };
        if (!ShellNotifyIcon(0, ref data))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Shell could not add the Modern Download Manager tray icon.");

    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmTray)
        {
            var mouseMessage = unchecked((int)lParam.ToInt64());
            if (mouseMessage == WmLButtonUp || mouseMessage == WmLButtonDoubleClick)
                Restore();
            else if (mouseMessage == WmRButtonUp || mouseMessage == WmContextMenu)
                ShowMenu();
        }
        else if (message == WmContextMenu)
        {
            ShowMenu();
        }
        else if (message == WmCommand)
        {
            var command = unchecked((int)(wParam.ToInt64() & 0xffff));
            if (command == RestoreCommand) Restore();
            if (command == ExitCommand) _window.RequestExitFromTray();
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void Restore()
    {
        _window.AppWindow.Show();
        _window.Activate();
        var hwnd = WindowNativeHandle();
        ShowWindow(hwnd, 9);
        SetForegroundWindow(hwnd);
    }

    private void ShowMenu()
    {
        GetCursorPos(out var point);
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, RestoreCommand, "Open Modern Download Manager");
        AppendMenu(menu, 0, ExitCommand, "Exit");
        SetForegroundWindow(_messageWindow);
        TrackPopupMenu(menu, 0x0002, point.X, point.Y, 0, _messageWindow, IntPtr.Zero);
        DestroyMenu(menu);
        PostMessage(_messageWindow, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr WindowNativeHandle() => WinRT.Interop.WindowNative.GetWindowHandle(_window);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _messageWindow, Id = TrayId, Tip = string.Empty, Info = string.Empty, InfoTitle = string.Empty };
        ShellNotifyIcon(2, ref data);
        if (_messageWindow != IntPtr.Zero) DestroyWindow(_messageWindow);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        UnregisterClass(_className, GetModuleHandle(null));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Size;
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WndClass wndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string text);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true, SetLastError = true)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
}
