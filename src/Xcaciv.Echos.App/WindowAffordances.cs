using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Xcaciv.Echos.App;

internal sealed class WindowAffordances : IDisposable
{
    private const uint TrayMessage = 0x8001;
    private const int HotKeyId = 0x4543;
    private readonly nint handle;
    private readonly SubclassProc callback;
    private readonly Action toggle;
    private readonly Action exit;
    private bool disposed;
    private NotifyIconData icon;

    public bool HasHotKey { get; }
    public bool HasTray { get; }

    public static double GetScale(Window window)
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
        return dpi == 0 ? 1 : dpi / 96d;
    }

    public WindowAffordances(Window window, Action toggle, Action exit)
    {
        this.toggle = toggle;
        this.exit = exit;
        handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        callback = WindowProc;
        if (!SetWindowSubclass(handle, callback, 1, 0))
            return;

        HasHotKey = RegisterHotKey(handle, HotKeyId, 0x4000 | 0x0002 | 0x0004, 0x45);
        icon = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = handle,
            Id = 1,
            Flags = 1 | 2 | 4,
            CallbackMessage = TrayMessage,
            Icon = LoadIcon(0, (nint)32512),
            Tip = "Echos — click to show/hide; right-click to exit",
            Info = string.Empty,
            InfoTitle = string.Empty
        };
        HasTray = Shell_NotifyIcon(0, ref icon);
    }

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0312 && wParam == HotKeyId)
        {
            toggle();
            return 0;
        }

        if (message == TrayMessage)
        {
            var mouseMessage = (uint)((long)lParam & 0xffff);
            if (mouseMessage == 0x0202)
                toggle();
            else if (mouseMessage == 0x0205)
                ShowMenu();
            return 0;
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
            return;
        try
        {
            AppendMenu(menu, 0, 1, "Show / hide Echos");
            AppendMenu(menu, 0, 2, "Exit (stop observation)");
            GetCursorPos(out var point);
            SetForegroundWindow(handle);
            var command = TrackPopupMenu(menu, 0x0100 | 0x0002, point.X, point.Y, 0, handle, 0);
            if (command == 1)
                toggle();
            else if (command == 2)
                exit();
        }
        finally
        {
            DestroyMenu(menu);
            PostMessage(handle, 0, 0, 0);
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        if (HasHotKey)
            UnregisterHotKey(handle, HotKeyId);
        if (HasTray)
            Shell_NotifyIcon(2, ref icon);
        RemoveWindowSubclass(handle, callback, 1);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
}
