using System.Diagnostics;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
