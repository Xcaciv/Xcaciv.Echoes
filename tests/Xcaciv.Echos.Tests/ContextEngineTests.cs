using System.Drawing;
using Xcaciv.Echos.Context;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.Tests;

public sealed class ContextEngineTests
{
    [Fact]
    public async Task ObservationOffNeverInspectsCapturesOrSends()
    {
        var sources = new ContextSources();
        var consent = new CaptureConsent { IsScreenEnabled = true, IsAudioEnabled = true };
        using var engine = Create(sources, consent);
        sources.Emit(new byte[] { 1, 2 });
        Assert.Null(await engine.GetSuggestionAsync());
        Assert.Empty((await engine.GatherAsync()).Ui.VisibleTexts);
        Assert.Equal(0, sources.Inspections + sources.ScreenCaptures + sources.Transcriptions + sources.Completions);
        Assert.Equal(0, engine.BufferedAudioBytes);
    }

    [Fact]
    public async Task ScreenAndAudioOffStillAllowsUiObservation()
    {
        var sources = new ContextSources();
        var consent = new CaptureConsent { IsObservationEnabled = true };
        using var engine = Create(sources, consent);
        sources.Emit(new byte[] { 1, 2 });
        Assert.Equal("suggestion", (await engine.GetSuggestionAsync())?.Body);
        Assert.Equal(1, sources.Inspections);
        Assert.Equal(1, sources.Completions);
        Assert.Equal(0, sources.ScreenCaptures + sources.Transcriptions);
    }

    [Fact]
    public async Task AudioIsCopiedBoundedAndDrainedWithoutStartingCapture()
    {
        var sources = new ContextSources();
        var consent = new CaptureConsent { IsObservationEnabled = true, IsAudioEnabled = true };
        using var engine = Create(sources, consent);
        var original = new byte[] { 1, 2, 3, 4 };
        var args = new AudioChunkEventArgs(original, 16000, 1);
        sources.Emit(args);
        args.Buffer[0] = 9;
        original[1] = 9;
        await engine.GetSuggestionAsync();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, sources.TranscribedCopy);
        Assert.Equal(0, engine.BufferedAudioBytes);
        Assert.Equal(0, sources.Starts + sources.Stops);
        for (var index = 0; index < 20; index++)
            sources.Emit(new byte[128 * 1024]);
        Assert.InRange(engine.BufferedAudioBytes, 1, ContextEngine.MaximumAudioBufferBytes);
    }

    [Fact]
    public async Task RevocationClearsRetainedAudioAndDoesNotReplayAfterReenable()
    {
        var sources = new ContextSources();
        var consent = new CaptureConsent { IsObservationEnabled = true, IsAudioEnabled = true };
        using var engine = Create(sources, consent);
        sources.Emit(new byte[] { 1, 2 });
        consent.IsAudioEnabled = false;
        Assert.Equal(0, engine.BufferedAudioBytes);
        consent.IsAudioEnabled = true;
        await engine.GetSuggestionAsync();
        Assert.Equal(0, sources.Transcriptions);
        sources.Emit(new byte[] { 1, 2 });
        consent.IsObservationEnabled = false;
        Assert.Equal(0, engine.BufferedAudioBytes);
    }

    [Fact]
    public async Task RevokingConsentDuringInspectionPreventsLaterNetworkCalls()
    {
        var sources = new ContextSources();
        var consent = new CaptureConsent { IsObservationEnabled = true, IsAudioEnabled = true };
        sources.Snapshot = () =>
        {
            consent.IsObservationEnabled = false;
            return new UiSnapshot("app", "window", Array.Empty<string>());
        };
        using var engine = Create(sources, consent);
        sources.Emit(new byte[] { 1, 2 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.GetSuggestionAsync());
        Assert.Equal(0, sources.Transcriptions + sources.Completions);
    }

    [Fact]
    public async Task CancellationAfterInspectionPreventsSendingAndPrecancelledCallsDoNotInspect()
    {
        var sources = new ContextSources();
        var consent = new CaptureConsent { IsObservationEnabled = true };
        using var engine = Create(sources, consent);
        using var cancellation = new CancellationTokenSource();
        sources.Snapshot = () =>
        {
            cancellation.Cancel();
            return new UiSnapshot("app", "window", Array.Empty<string>());
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.GetSuggestionAsync(cancellation.Token));
        Assert.Equal(0, sources.Completions);
        var calls = sources.Inspections;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.GetSuggestionAsync(cancellation.Token));
        Assert.Equal(calls, sources.Inspections);
    }

    [Fact]
    public async Task RevocationDuringTranscriptionNeverSendsCompletion()
    {
        var sources = new ContextSources();
        var consent = new CaptureConsent { IsObservationEnabled = true, IsAudioEnabled = true };
        sources.Transcribe = (_, token) =>
        {
            consent.IsAudioEnabled = false;
            Assert.True(token.IsCancellationRequested);
            return Task.FromResult<IReadOnlyList<AudioTranscriptSegment>>(
                new[] { new AudioTranscriptSegment("private", DateTimeOffset.UtcNow) });
        };
        using var engine = Create(sources, consent);
        sources.Emit(new byte[] { 1, 2 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.GetSuggestionAsync());
        Assert.Equal(0, sources.Completions);
        Assert.Equal(0, engine.BufferedAudioBytes);
    }

    [Fact]
    public async Task RevocationDuringCompletionDoesNotReturnStaleSuggestion()
    {
        var sources = new ContextSources();
        var consent = new CaptureConsent { IsObservationEnabled = true };
        sources.Complete = token =>
        {
            consent.IsObservationEnabled = false;
            Assert.True(token.IsCancellationRequested);
            return Task.FromResult(new LlmResult("stale"));
        };
        using var engine = Create(sources, consent);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.GetSuggestionAsync());
    }

    [Fact]
    public async Task DisposeUnsubscribesAndClearsBufferWithoutDisposingBorrowedSources()
    {
        var sources = new ContextSources();
        var consent = new CaptureConsent { IsObservationEnabled = true, IsAudioEnabled = true };
        var engine = Create(sources, consent);
        Assert.Equal(1, sources.Subscribers);
        sources.Emit(new byte[] { 1, 2 });
        engine.Dispose();
        engine.Dispose();
        Assert.Equal(0, sources.Subscribers);
        Assert.Equal(0, engine.BufferedAudioBytes);
        Assert.Equal(0, sources.Starts + sources.Stops);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.GetSuggestionAsync());
    }

    [WindowsFact]
    public async Task CapturedBitmapIsDisposedWhenOcrThrows()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return;
        var sources = new ContextSources();
        var consent = new CaptureConsent { IsObservationEnabled = true, IsScreenEnabled = true };
        using var engine = Create(sources, consent);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.GetSuggestionAsync());
        Assert.NotNull(sources.Captured);
        Assert.ThrowsAny<Exception>(() =>
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
                sources.Captured!.GetPixel(0, 0);
        });
        Assert.Equal(0, sources.Completions);
    }

    private static ContextEngine Create(ContextSources sources, CaptureConsent consent) =>
        new(sources, sources, sources, sources, sources, sources, consent);
}
