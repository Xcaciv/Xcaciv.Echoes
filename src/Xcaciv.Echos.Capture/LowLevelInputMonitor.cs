using System.ComponentModel;
using System.Runtime.InteropServices;
using Xcaciv.Echos.Core;
using KeyEventArgs = Xcaciv.Echos.Core.KeyEventArgs;
using MouseEventArgs = Xcaciv.Echos.Core.MouseEventArgs;

namespace Xcaciv.Echos.Capture;

/// <summary>
/// Opt-in native global hooks. Start must be called explicitly. Subscribers run on the hook
/// thread and must return immediately; queue any substantial work outside the callback.
/// </summary>
public sealed class LowLevelInputMonitor : IInputMonitor, IDisposable
{
    private const uint QuitMessage = 0x0012;
    private readonly object gate = new();
    private readonly HookProc keyboardCallback;
    private readonly HookProc mouseCallback;
    private Thread? thread;
    private uint threadId;
    private IntPtr keyboardHook;
    private IntPtr mouseHook;
    private bool disposed;
    private bool stopping;

    public LowLevelInputMonitor()
    {
        keyboardCallback = KeyboardHook;
        mouseCallback = MouseHook;
    }

    public event EventHandler<KeyEventArgs>? KeyPressed;
    public event EventHandler<MouseEventArgs>? MouseActivity;

    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (thread?.IsAlive == true)
            {
                if (stopping)
                    throw new InvalidOperationException("The previous input hook session is still stopping.");
                return;
            }
            stopping = false;
            var ready = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            thread = new Thread(() =>
            {
                try
                {
                    threadId = GetCurrentThreadId();
                    PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
                    keyboardHook = SetWindowsHookEx(13, keyboardCallback, GetModuleHandle(null), 0);
                    if (keyboardHook == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    mouseHook = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
                    if (mouseHook == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    ready.TrySetResult(null);
                    while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                    {
                        TranslateMessage(ref message);
                        DispatchMessage(ref message);
                    }
                }
                catch (Exception error)
                {
                    ready.TrySetResult(error);
                }
                finally
                {
                    if (keyboardHook != IntPtr.Zero)
                        UnhookWindowsHookEx(keyboardHook);
                    if (mouseHook != IntPtr.Zero)
                        UnhookWindowsHookEx(mouseHook);
                    keyboardHook = mouseHook = IntPtr.Zero;
                    ready.TrySetResult(new InvalidOperationException("Input hook thread stopped during initialization."));
                }
            }) { IsBackground = true, Name = "Echos input hooks" };
            thread.Start();
            var startupError = ready.Task.GetAwaiter().GetResult();
            if (startupError is not null)
            {
                thread.Join();
                throw new InvalidOperationException("Could not install input hooks.", startupError);
            }
        }
    }

    public void Stop()
    {
        Thread? current;
        lock (gate)
        {
            current = thread;
            if (current?.IsAlive != true)
                return;
            if (!PostThreadMessage(threadId, QuitMessage, UIntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            stopping = true;
        }
        if (current != Thread.CurrentThread)
            current.Join();
    }

    public void Dispose()
    {
        lock (gate)
            disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code >= 0 && (message.ToInt64() is 0x100 or 0x101 or 0x104 or 0x105))
            {
                var key = Marshal.PtrToStructure<KeyboardData>(data);
                Publish(KeyPressed, new KeyEventArgs((int)key.VirtualKey, message.ToInt64() is 0x100 or 0x104,
                    DateTimeOffset.UtcNow));
            }
        }
        catch
        {
            // Exceptions must never cross the unmanaged hook boundary.
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code >= 0)
            {
                var mouse = Marshal.PtrToStructure<MouseData>(data);
                var kind = message.ToInt64();
                var button = kind switch
                {
                    0x201 or 0x202 => "Left",
                    0x204 or 0x205 => "Right",
                    0x207 or 0x208 => "Middle",
                    0x20B or 0x20C => (mouse.MouseDataValue >> 16) == 1 ? "X1" : "X2",
                    0x20A => "Wheel",
                    0x20E => "HorizontalWheel",
                    _ => "None"
                };
                Publish(MouseActivity, new MouseEventArgs(mouse.Position.X, mouse.Position.Y, button,
                    kind is 0x201 or 0x204 or 0x207 or 0x20B, DateTimeOffset.UtcNow));
            }
        }
        catch
        {
            // Exceptions must never cross the unmanaged hook boundary.
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private void Publish<T>(EventHandler<T>? handlers, T args) where T : EventArgs
    {
        if (handlers is null)
            return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch { /* A subscriber cannot interrupt input dispatch or other subscribers. */ }
        }
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointData { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        public PointData Position;
        public uint MouseDataValue;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public PointData Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);
}
