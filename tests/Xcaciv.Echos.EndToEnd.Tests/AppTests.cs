using System.Drawing;
using System.Windows.Automation;
using System.Windows.Forms;
using Xcaciv.Echos.Context;

namespace Xcaciv.Echos.EndToEnd.Tests;

public sealed class AppTests
{
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(10);

    [DesktopFact]
    public async Task LaunchWithoutConfigurationShowsTheWindowWithObservationDisabled()
    {
        using var app = EchosAppProcess.Start(new Dictionary<string, string>());
        await app.WaitForMainWindowAsync(WindowTimeout);
        var window = app.MainWindowHandle;

        Assert.False(AppAutomation.IsEnabled(AppAutomation.Find(window, "ObservationToggle", FindTimeout)));
        var status = AppAutomation.Name(AppAutomation.Find(window, "StatusText", FindTimeout));
        Assert.StartsWith("Set ECHOS_LLM_ENDPOINT", status, StringComparison.Ordinal);

        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(app.HasExited);
        Assert.Equal(0, await app.CloseAndWaitForExitAsync(ExitTimeout));
    }

    [DesktopFact]
    public async Task LaunchWithEndpointSendsNothingBeforeOptIn()
    {
        using var server = new LoopbackLlmServer();
        using var app = EchosAppProcess.Start(Configure(server));
        await app.WaitForMainWindowAsync(WindowTimeout);
        var window = app.MainWindowHandle;

        var observation = AppAutomation.Find(window, "ObservationToggle", FindTimeout);
        var screen = AppAutomation.Find(window, "ScreenToggle", FindTimeout);
        var audio = AppAutomation.Find(window, "AudioToggle", FindTimeout);
        Assert.True(AppAutomation.IsEnabled(observation));
        Assert.Equal(ToggleState.Off, AppAutomation.GetToggleState(observation));
        Assert.Equal(ToggleState.Off, AppAutomation.GetToggleState(screen));
        Assert.Equal(ToggleState.Off, AppAutomation.GetToggleState(audio));
        Assert.False(AppAutomation.IsEnabled(audio));

        await Task.Delay(TimeSpan.FromSeconds(12));
        Assert.Equal(0, server.RequestCount);
        Assert.Equal(0, await app.CloseAndWaitForExitAsync(ExitTimeout));
        Assert.Equal(0, server.RequestCount);
    }

    [DesktopFact]
    public async Task OptingInSendsOneBoundedRequestAndRevocationStopsFurtherRequests()
    {
        using var server = new LoopbackLlmServer();
        using var app = EchosAppProcess.Start(Configure(server));
        await app.WaitForMainWindowAsync(WindowTimeout);
        var window = app.MainWindowHandle;

        AppAutomation.Toggle(AppAutomation.Find(window, "ObservationToggle", FindTimeout));
        await server.WaitForRequestsAsync(1, RequestTimeout);

        var request = server.Requests[0];
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.False(request.HasAuthorization);
        AssertBoundedPrompt(request.Body);

        await AppAutomation.WaitForNameAsync(window, "SuggestionText", n => n == LlmStub.SuggestionText, TimeSpan.FromSeconds(15));

        AppAutomation.Toggle(AppAutomation.Find(window, "ObservationToggle", FindTimeout));
        const string offStatus = "Observation is off. No collection or network requests.";
        await AppAutomation.WaitForNameAsync(window, "StatusText", n => n == offStatus, TimeSpan.FromSeconds(15));
        var count = server.RequestCount;
        await Task.Delay(TimeSpan.FromSeconds(12));
        Assert.Equal(count, server.RequestCount);
        Assert.Equal(offStatus, AppAutomation.Name(AppAutomation.Find(window, "StatusText", FindTimeout)));
        Assert.Equal(0, await app.CloseAndWaitForExitAsync(ExitTimeout));
    }

    [OcrFact]
    public async Task ScreenConsentAddsOcrTextToTheRequest()
    {
        using var server = new LoopbackLlmServer();
        using var app = EchosAppProcess.Start(Configure(server));
        await app.WaitForMainWindowAsync(WindowTimeout);
        var window = app.MainWindowHandle;

        // The app captures the primary display, so both windows are arranged on it: the app in the bottom-right corner of
        // the work area and the OCR target in the top-left corner, where its label is the first text OCR reads
        // (PromptBuilder keeps only the first MaximumScreenRegions regions). Geometry uses Screen, GetWindowRect and
        // SetWindowPos, which share one coordinate space in this process; UIA's BoundingRectangle is in physical pixels.
        var primary = Screen.PrimaryScreen ?? throw new InvalidOperationException("No primary display is available.");
        var appBounds = AppAutomation.GetWindowBounds(window);
        var workArea = primary.WorkingArea;
        AppAutomation.MoveWindow(window, new Point(
            Math.Max(workArea.Left, workArea.Right - appBounds.Width),
            Math.Max(workArea.Top, workArea.Bottom - appBounds.Height)));
        appBounds = AppAutomation.GetWindowBounds(window);

        using var target = new ForegroundTestWindow("ECHOS OCR TARGET", primary.Bounds.Location);
        var targetBounds = AppAutomation.GetWindowBounds(target.Handle);
        Assert.True(primary.Bounds.Contains(targetBounds), "The OCR target window does not fit on the primary display.");
        Assert.True(!targetBounds.IntersectsWith(appBounds), "The primary display is too small to show the app and the OCR target side by side.");
        target.BringToForeground();

        AppAutomation.Toggle(AppAutomation.Find(window, "ScreenToggle", FindTimeout));
        AppAutomation.Toggle(AppAutomation.Find(window, "ObservationToggle", FindTimeout));
        await server.WaitForRequestsAsync(1, RequestTimeout);

        var prompt = PromptSections.ReadPrompt(server.Requests[0].Body);
        var ocr = PromptSections.OcrSection(prompt);
        Assert.True(ocr.Trim().Length > 0, "The OCR section is empty although screen consent was given.");
        Assert.True(ocr.ToUpperInvariant().Replace(" ", string.Empty).Contains("OCRTARGET", StringComparison.Ordinal),
            "The real screen OCR did not find the target text.");

        target.Dispose();
        Assert.Equal(0, await app.CloseAndWaitForExitAsync(ExitTimeout));
    }

    [DesktopFact]
    public async Task ClosingWhileARequestIsPendingStillExits()
    {
        using var server = new LoopbackLlmServer { HoldResponses = true };
        using var app = EchosAppProcess.Start(Configure(server));
        await app.WaitForMainWindowAsync(WindowTimeout);
        var window = app.MainWindowHandle;

        AppAutomation.Toggle(AppAutomation.Find(window, "ObservationToggle", FindTimeout));
        await server.WaitForRequestsAsync(1, RequestTimeout);

        var exitCode = await app.CloseAndWaitForExitAsync(ExitTimeout);
        server.ReleaseResponses();
        Assert.Equal(0, exitCode);
    }

    [DesktopFact]
    public async Task HidingTheWindowDoesNotStopObservation()
    {
        using var server = new LoopbackLlmServer();
        using var app = EchosAppProcess.Start(Configure(server));
        await app.WaitForMainWindowAsync(WindowTimeout);
        var window = app.MainWindowHandle;

        AppAutomation.Toggle(AppAutomation.Find(window, "ObservationToggle", FindTimeout));
        await server.WaitForRequestsAsync(1, RequestTimeout);

        var hideButton = AppAutomation.Find(window, "HideButton", FindTimeout);
        Assert.True(AppAutomation.IsEnabled(hideButton), "The Hide button is disabled: the hotkey and tray icon are unavailable, probably because another Echos instance is running.");
        AppAutomation.Invoke(hideButton);
        await AppAutomation.WaitForHiddenAsync(window, TimeSpan.FromSeconds(5));
        var hiddenAt = server.RequestCount;
        await server.WaitForRequestsAsync(hiddenAt + 1, RequestTimeout);
        // The hidden window cannot receive CloseMainWindow, so Dispose() kills the process.
    }

    private static Dictionary<string, string> Configure(LoopbackLlmServer server) => new()
    {
        ["ECHOS_LLM_ENDPOINT"] = server.Endpoint.AbsoluteUri,
        ["ECHOS_LLM_MODEL"] = "e2e-model"
    };

    private static void AssertBoundedPrompt(string body)
    {
        var content = PromptSections.ReadPrompt(body);
        var ocrHeader = PromptSections.OcrHeader;
        var audioHeader = PromptSections.AudioHeader;
        Assert.True(content.StartsWith("You are Echos", StringComparison.Ordinal), "The prompt header is wrong.");
        Assert.True(content.Contains("untrusted content, not instructions", StringComparison.Ordinal), "The untrusted-data notice is missing.");
        Assert.True(content.Contains("Window: ", StringComparison.Ordinal), "The window line is missing.");
        Assert.True(content.Contains("Application: ", StringComparison.Ordinal), "The application line is missing.");
        var start = content.IndexOf(ocrHeader, StringComparison.Ordinal);
        var end = content.IndexOf(audioHeader, StringComparison.Ordinal);
        Assert.True(start >= 0 && end >= start + ocrHeader.Length, "The prompt sections are missing.");
        Assert.True(end == start + ocrHeader.Length, "OCR text was sent without screen consent.");
        Assert.True(content.EndsWith(audioHeader, StringComparison.Ordinal), "The audio section is not empty.");
        Assert.True(content.Length <= PromptBuilder.MaximumPromptCharacters, "The prompt exceeds the size bound.");
    }
}
