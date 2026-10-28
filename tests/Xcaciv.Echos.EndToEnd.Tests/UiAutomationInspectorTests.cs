using System.Diagnostics;
using Xcaciv.Echos.Capture;

namespace Xcaciv.Echos.EndToEnd.Tests;

public sealed class UiAutomationInspectorTests
{
    [DesktopFact]
    public void ForegroundWindowTitleAndVisibleTextsAreCaptured()
    {
        using var window = new ForegroundTestWindow();
        window.BringToForeground();
        var snapshot = new UiAutomationInspector().GetCurrentSnapshot();
        Assert.True(snapshot.ActiveWindowTitle == window.Title, "The active window title is wrong.");
        using var process = Process.GetCurrentProcess();
        Assert.True(snapshot.ActiveApp == process.ProcessName, "The active application is wrong.");
        Assert.True(snapshot.VisibleTexts.Contains(window.LabelText), "The label text was not captured.");
        Assert.True(snapshot.VisibleTexts.Contains(window.ValueText), "The text box value was not captured.");
    }

    [DesktopFact]
    public void PasswordFieldContentIsNeverCaptured()
    {
        using var window = new ForegroundTestWindow();
        window.BringToForeground();
        var snapshot = new UiAutomationInspector().GetCurrentSnapshot();
        Assert.True(snapshot.VisibleTexts.Count > 0, "Nothing was captured, so the password check proves nothing.");
        Assert.True(
            snapshot.VisibleTexts.All(text => !text.Contains(window.SecretText, StringComparison.Ordinal)),
            "Password field content was captured.");
        Assert.True(
            snapshot.ActiveWindowTitle?.Contains(window.SecretText, StringComparison.Ordinal) != true,
            "Password field content leaked into the title.");
    }

    [DesktopFact]
    public void ElementAndTextLengthLimitsAreEnforced()
    {
        using var window = new ForegroundTestWindow();
        window.BringToForeground();
        var options = new UiInspectionOptions
        {
            MaximumElements = 2,
            MaximumDepth = 4,
            MaximumTextLength = 8,
            TimeBudget = TimeSpan.FromSeconds(1)
        };
        var snapshot = new UiAutomationInspector(options).GetCurrentSnapshot();
        Assert.True(snapshot.VisibleTexts.Count > 0, "Nothing was captured, so the limits were not exercised.");
        Assert.True(snapshot.VisibleTexts.Count <= 2, "Too many elements were captured.");
        Assert.True(snapshot.VisibleTexts.All(text => text.Length <= 8), "A text exceeded the length limit.");
        Assert.True((snapshot.ActiveWindowTitle?.Length ?? 0) <= 8, "The title exceeded the length limit.");
    }

    [DesktopFact]
    public void InvalidOptionsAreRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new UiAutomationInspector(new UiInspectionOptions { MaximumElements = 0 }));
}
