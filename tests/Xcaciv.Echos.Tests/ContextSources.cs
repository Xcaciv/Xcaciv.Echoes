using System.Drawing;
using System.Runtime.Versioning;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.Tests;

internal sealed class ContextSources : IUiInspector, IScreenCapture, IOcrEngine, IAudioCapture,
    ITranscriptionEngine, ILlmClient
{
    private EventHandler<AudioChunkEventArgs>? _chunks;
    public event EventHandler<AudioChunkEventArgs>? AudioChunkCaptured
    {
        add { _chunks += value; Subscribers++; }
        remove { _chunks -= value; Subscribers--; }
    }
    public event EventHandler<AudioCaptureFailedEventArgs>? CaptureFailed;
    public int Subscribers, Inspections, ScreenCaptures, Transcriptions, Completions, Starts, Stops;
    public byte[]? TranscribedCopy;
    public Bitmap? Captured;
    public Func<UiSnapshot>? Snapshot;
    public Func<CancellationToken, Task<Bitmap>>? Capture;
    public Func<Bitmap, CancellationToken, Task<IReadOnlyList<ScreenTextRegion>>>? Ocr;
    public Func<CancellationToken, Task<LlmResult>>? Complete;
    public Func<IReadOnlyList<AudioChunkEventArgs>, CancellationToken, Task<IReadOnlyList<AudioTranscriptSegment>>>? Transcribe;
    public void FailAudio(Exception error) => CaptureFailed?.Invoke(this, new AudioCaptureFailedEventArgs(error));
    public void Emit(byte[] data) => Emit(new AudioChunkEventArgs(data, 16000, 1));
    public void Emit(AudioChunkEventArgs args) => _chunks?.Invoke(this, args);
    public UiSnapshot GetCurrentSnapshot()
    {
        Inspections++;
        return Snapshot?.Invoke() ?? new UiSnapshot("app", "window", Array.Empty<string>());
    }
    public Task<Bitmap> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        ScreenCaptures++;
        if (Capture is not null)
            return Capture(cancellationToken);
        Captured = new Bitmap(1, 1);
        return Task.FromResult(Captured);
    }
    [SupportedOSPlatform("windows6.1")]
    public Task<IReadOnlyList<ScreenTextRegion>> ExtractTextAsync(Bitmap bitmap, CancellationToken ct = default) =>
        Ocr?.Invoke(bitmap, ct) ?? throw new InvalidOperationException("OCR failed");
    public Task StartAsync(CancellationToken cancellationToken = default) { Starts++; return Task.CompletedTask; }
    public Task StopAsync() { Stops++; return Task.CompletedTask; }
    public Task<IReadOnlyList<AudioTranscriptSegment>> TranscribeAsync(IReadOnlyList<AudioChunkEventArgs> chunks, CancellationToken ct = default)
    {
        Transcriptions++;
        TranscribedCopy = chunks.SelectMany(chunk => chunk.Buffer).ToArray();
        return Transcribe?.Invoke(chunks, ct) ?? Task.FromResult<IReadOnlyList<AudioTranscriptSegment>>(
            new[] { new AudioTranscriptSegment("transcript", DateTimeOffset.UtcNow) });
    }
    public Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        Completions++;
        return Complete?.Invoke(ct) ?? Task.FromResult(new LlmResult("suggestion"));
    }
}
