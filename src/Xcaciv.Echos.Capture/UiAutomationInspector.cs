using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.Capture;

/// <summary>
/// Inspects the foreground window with bounded UI Automation traversal. Password elements
/// and their descendants are excluded. Budgets are cooperative; a stalled external UIA
/// provider can still block an individual COM call. Invoke away from the application's UI thread.
/// </summary>
public sealed class UiAutomationInspector : IUiInspector
{
    private readonly UiInspectionOptions options;

    public UiAutomationInspector(UiInspectionOptions? options = null)
    {
        this.options = options ?? new UiInspectionOptions();
        if (this.options.MaximumElements < 1 || this.options.MaximumElements > 2000 ||
            this.options.MaximumDepth < 0 || this.options.MaximumDepth > 32 ||
            this.options.MaximumTextLength < 1 || this.options.MaximumTextLength > 4096 ||
            this.options.TimeBudget <= TimeSpan.Zero || this.options.TimeBudget > TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public UiSnapshot GetCurrentSnapshot()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return new UiSnapshot(string.Empty, null, Array.Empty<string>());
        GetWindowThreadProcessId(window, out var processId);
        var title = new StringBuilder(options.MaximumTextLength + 1);
        GetWindowText(window, title, title.Capacity);
        var processName = string.Empty;
        try
        {
            using var process = Process.GetProcessById(unchecked((int)processId));
            processName = Limit(process.ProcessName);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // A foreground process can exit between the native calls.
        }
        return new UiSnapshot(processName, title.ToString(), InspectVisibleTexts(window));
    }

    private IReadOnlyList<string> InspectVisibleTexts(IntPtr window)
    {
        var texts = new List<string>();
        var timer = Stopwatch.StartNew();
        try
        {
            var root = AutomationElement.FromHandle(window);
            if (root is not null)
            {
                var pending = new Stack<(AutomationElement Element, int Depth)>();
                pending.Push((root, 0));
                var walker = TreeWalker.ControlViewWalker;
                var visited = 0;
                while (pending.Count > 0 && visited < options.MaximumElements &&
                    texts.Count < options.MaximumElements && timer.Elapsed < options.TimeBudget)
                {
                    var (element, depth) = pending.Pop();
                    visited++;
                    try
                    {
                        var current = element.Current;
                        var password = current.IsPassword;
                        if (!password && !current.IsOffscreen)
                        {
                            var name = Limit(current.Name);
                            if (!string.IsNullOrWhiteSpace(name))
                                texts.Add(name);
                            if (texts.Count < options.MaximumElements &&
                                element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                            {
                                var value = Limit(((ValuePattern)pattern).Current.Value);
                                if (!string.IsNullOrWhiteSpace(value) && !string.Equals(name, value, StringComparison.Ordinal))
                                    texts.Add(value);
                            }
                        }
                        if (timer.Elapsed >= options.TimeBudget)
                            break;
                        if (depth > 0)
                        {
                            var sibling = walker.GetNextSibling(element);
                            if (sibling is not null)
                                pending.Push((sibling, depth));
                        }
                        if (!password && depth < options.MaximumDepth && timer.Elapsed < options.TimeBudget)
                        {
                            var child = walker.GetFirstChild(element);
                            if (child is not null)
                                pending.Push((child, depth + 1));
                        }
                    }
                    catch (Exception error) when (IsProviderError(error))
                    {
                        // Providers may disappear or deny access while a window is changing; skip this element.
                        // Note: a failed element is indistinguishable from an empty one in the snapshot.
                    }
                }
            }
        }
        catch (Exception error) when (IsProviderError(error))
        {
            // Return the native foreground metadata when UI Automation is unavailable. Note: this
            // "inspection failed" case is indistinguishable from "window has no visible text" to callers.
        }
        return texts.ToArray();
    }

    private string Limit(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= options.MaximumTextLength
            ? text : text[..options.MaximumTextLength];

    private static bool IsProviderError(Exception error) =>
        error is ElementNotAvailableException or ElementNotEnabledException or InvalidOperationException
            or System.UnauthorizedAccessException or COMException;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
}
