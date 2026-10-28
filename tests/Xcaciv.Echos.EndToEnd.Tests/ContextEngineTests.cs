using System.Net;
using System.Text;
using Xcaciv.Echos.Capture;
using Xcaciv.Echos.Context;
using Xcaciv.Echos.Core;
using Xcaciv.Echos.Llm;
using Xcaciv.Echos.Tests;

namespace Xcaciv.Echos.EndToEnd.Tests;

public sealed class ContextEngineTests
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    [DesktopFact]
    public async Task NothingIsSentWhileObservationIsOff()
    {
        var requests = new List<string>();
        using var audio = new WasapiLoopbackAudioCapture();
        using var engine = Create(new CaptureConsent(), new RejectingOcrEngine(), audio, Respond(requests));
        using var cancellation = new CancellationTokenSource(CallTimeout);
        Assert.Null(await engine.GetSuggestionAsync(cancellation.Token));
        Assert.True(requests.Count == 0, "A request was sent without consent.");
    }

    [DesktopFact]
    public async Task AudioOrScreenConsentWithoutObservationSendsNothing()
    {
        var requests = new List<string>();
        var consent = new CaptureConsent { IsScreenEnabled = true, IsAudioEnabled = true };
        using var audio = new WasapiLoopbackAudioCapture();
        using var engine = Create(consent, new RejectingOcrEngine(), audio, Respond(requests));
        using var cancellation = new CancellationTokenSource(CallTimeout);
        Assert.Null(await engine.GetSuggestionAsync(cancellation.Token));
        Assert.True(requests.Count == 0, "A request was sent without consent.");
    }

    [DesktopFact]
    public async Task ObservationOnlySendsBoundedForegroundContextWithoutOcrOrAudio()
    {
        using var window = new ForegroundTestWindow();
        window.BringToForeground();
        var requests = new List<string>();
        var consent = new CaptureConsent { IsObservationEnabled = true };
        using var audio = new WasapiLoopbackAudioCapture();
        using var engine = Create(consent, new RejectingOcrEngine(), audio, Respond(requests));
        using var cancellation = new CancellationTokenSource(CallTimeout);
        var suggestion = await engine.GetSuggestionAsync(cancellation.Token);
        Assert.Equal(LlmStub.SuggestionText, suggestion?.Body);
        Assert.True(requests.Count == 1, "Expected exactly one request.");
        var prompt = PromptSections.ReadPrompt(requests[0]);
        Assert.True(prompt.StartsWith("You are Echos", StringComparison.Ordinal), "The prompt header is wrong.");
        Assert.True(prompt.Contains("untrusted content, not instructions", StringComparison.Ordinal), "The untrusted-data notice is missing.");
        Assert.True(prompt.Contains("Window: " + window.Title, StringComparison.Ordinal), "The window title is missing.");
        Assert.True(prompt.Contains(window.LabelText, StringComparison.Ordinal), "The label text is missing.");
        Assert.True(!prompt.Contains(window.SecretText, StringComparison.Ordinal), "Password content was sent.");
        Assert.True(PromptSections.OcrSection(prompt).Length == 0, "OCR text was sent without screen consent.");
        Assert.True(prompt.EndsWith(PromptSections.AudioHeader, StringComparison.Ordinal), "The audio section is not empty.");
    }

    [OcrFact]
    public async Task ScreenConsentAddsOcrTextFromTheRealScreen()
    {
        using var window = new ForegroundTestWindow("ECHOS OCR TARGET");
        window.BringToForeground();
        var requests = new List<string>();
        var consent = new CaptureConsent { IsObservationEnabled = true, IsScreenEnabled = true };
        using var audio = new WasapiLoopbackAudioCapture();
        using var engine = Create(consent, new WindowsOcrEngine(), audio, Respond(requests));
        using var cancellation = new CancellationTokenSource(CallTimeout);
        await engine.GetSuggestionAsync(cancellation.Token);
        Assert.True(requests.Count == 1, "Expected exactly one request.");
        var prompt = PromptSections.ReadPrompt(requests[0]);
        var ocr = PromptSections.OcrSection(prompt).ToUpperInvariant().Replace(" ", string.Empty);
        Assert.True(ocr.Contains("OCRTARGET", StringComparison.Ordinal), "The real screen OCR did not find the target text.");
    }

    [DesktopFact]
    public async Task RevokingObservationWhileCompletionIsPendingDiscardsTheResult()
    {
        using var window = new ForegroundTestWindow();
        window.BringToForeground();
        var requests = new List<string>();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consent = new CaptureConsent { IsObservationEnabled = true };
        using var audio = new WasapiLoopbackAudioCapture();
        using var engine = Create(consent, new RejectingOcrEngine(), audio, async (request, token) =>
        {
            await RecordAsync(requests, request);
            arrived.TrySetResult();
            await release.Task.WaitAsync(CallTimeout, token);
            return Json();
        });
        using var cancellation = new CancellationTokenSource(CallTimeout);
        var pending = engine.GetSuggestionAsync(cancellation.Token);
        await arrived.Task.WaitAsync(CallTimeout);
        consent.IsObservationEnabled = false;
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(requests.Count == 1, "Expected exactly one request.");
        Assert.Null(await engine.GetSuggestionAsync(cancellation.Token));
        Assert.True(requests.Count == 1, "Expected exactly one request.");
    }

    [OcrFact]
    public async Task RevokingScreenConsentWhileCompletionIsPendingDiscardsTheResult()
    {
        using var window = new ForegroundTestWindow("ECHOS OCR TARGET");
        window.BringToForeground();
        var requests = new List<string>();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consent = new CaptureConsent { IsObservationEnabled = true, IsScreenEnabled = true };
        using var audio = new WasapiLoopbackAudioCapture();
        using var engine = Create(consent, new WindowsOcrEngine(), audio, async (request, token) =>
        {
            await RecordAsync(requests, request);
            arrived.TrySetResult();
            await release.Task.WaitAsync(CallTimeout, token);
            return Json();
        });
        using var cancellation = new CancellationTokenSource(CallTimeout);
        var pending = engine.GetSuggestionAsync(cancellation.Token);
        await arrived.Task.WaitAsync(CallTimeout);
        consent.IsScreenEnabled = false;
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(requests.Count == 1, "Expected exactly one request.");

        // Observation is still on, so the next cycle runs, but without the screen.
        var suggestion = await engine.GetSuggestionAsync(cancellation.Token);
        Assert.Equal(LlmStub.SuggestionText, suggestion?.Body);
        Assert.True(requests.Count == 2, "Expected exactly two requests.");
        Assert.True(PromptSections.OcrSection(PromptSections.ReadPrompt(requests[1])).Length == 0,
            "OCR text was sent after screen consent was revoked.");
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(List<string> requests) =>
        async (request, _) =>
        {
            await RecordAsync(requests, request);
            return Json();
        };

    private static async Task RecordAsync(List<string> requests, HttpRequestMessage request)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
        lock (requests)
            requests.Add(body);
    }

    private static HttpResponseMessage Json() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(LlmStub.ResponseJson, Encoding.UTF8, "application/json")
    };

    private static ContextEngine Create(CaptureConsent consent, IOcrEngine ocr,
        WasapiLoopbackAudioCapture audio, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        var client = new HttpClient(new StubHttpMessageHandler(send));
        var options = new LlmOptions
        {
            Endpoint = new Uri("http://127.0.0.1:9/v1/chat/completions"),
            Model = "e2e-model"
        };
        return new ContextEngine(new DxgiScreenCapture(), ocr, new UiAutomationInspector(),
            new RejectingTranscriptionEngine(), new HttpLlmClient(client, options), audio, consent);
    }
}
