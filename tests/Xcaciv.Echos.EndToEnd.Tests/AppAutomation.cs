using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Xcaciv.Echos.EndToEnd.Tests;

internal static class AppAutomation
{
    public static AutomationElement Find(IntPtr window, string automationId, TimeSpan timeout)
    {
        var root = AutomationElement.FromHandle(window);
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var element = root.FindFirst(TreeScope.Descendants, condition);
            if (element is not null)
                return element;
            if (clock.Elapsed >= timeout)
                throw new TimeoutException("Automation element '" + automationId + "' was not found.");
            Thread.Sleep(200);
        }
    }

    public static bool IsEnabled(AutomationElement element) => element.Current.IsEnabled;

    public static ToggleState GetToggleState(AutomationElement element) =>
        ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;

    public static void Toggle(AutomationElement element) =>
        ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Toggle();

    public static void Invoke(AutomationElement element) =>
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    public static string Name(AutomationElement element) => element.Current.Name;

    public static async Task WaitForNameAsync(IntPtr window, string automationId, Func<string, bool> predicate, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var element = Find(window, automationId, TimeSpan.FromSeconds(5));
            if (predicate(Name(element)))
                return;
            if (clock.Elapsed >= timeout)
                throw new TimeoutException("Element '" + automationId + "' never reached the expected state.");
            await Task.Delay(200);
        }
    }

    public static async Task WaitForHiddenAsync(IntPtr window, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (IsWindowVisible(window))
        {
            if (clock.Elapsed >= timeout)
                throw new TimeoutException("The window was not hidden in time.");
            await Task.Delay(100);
        }
    }

    public static Rectangle GetWindowBounds(IntPtr window)
    {
        if (!GetWindowRect(window, out var rect))
            throw new InvalidOperationException("GetWindowRect failed.");
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static void MoveWindow(IntPtr window, Point location)
    {
        const uint flags = SwpNoSize | SwpNoZOrder | SwpNoActivate;
        if (!SetWindowPos(window, IntPtr.Zero, location.X, location.Y, 0, 0, flags))
            throw new InvalidOperationException("SetWindowPos failed.");
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
