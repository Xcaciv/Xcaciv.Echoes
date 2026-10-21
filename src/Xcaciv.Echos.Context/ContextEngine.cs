using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.Context;

/// <summary>Collects only opted-in sources. Audio start/stop and dependency ownership belong to the UI.</summary>
public sealed class ContextEngine : IContextEngine, IDisposable
{
    public const int MaximumAudioBufferBytes = 1024 * 1024;
    private readonly IUiInspector _inspector;
    private readonly IScreenCapture _screen;
    private readonly IOcrEngine _ocr;
    private readonly IAudioCapture _audio;
    private readonly ITranscriptionEngine _transcription;
    private readonly ILlmClient _llm;
    private readonly CaptureConsent _consent;
    private readonly Queue<AudioChunkEventArgs> _chunks = new();
    private CancellationTokenSource _epoch = new();
    private int _bufferBytes;
    private int _sampleRate;
    private int _channels;
    private bool _disposed;

    public ContextEngine(IScreenCapture screen, IOcrEngine ocr, IUiInspector inspector,
        ITranscriptionEngine transcription, ILlmClient llm, IAudioCapture audio, CaptureConsent consent)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _transcription = transcription ?? throw new ArgumentNullException(nameof(transcription));
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _audio.AudioChunkCaptured += OnAudioChunk;
        _consent.Changed += OnConsentChanged;
    }

    public int BufferedAudioBytes { get { lock (_consent) return _bufferBytes; } }

    public async Task<AssistantSuggestion?> GetSuggestionAsync(CancellationToken ct = default)
    {
        using var linked = CreateCycle(ct);
        if (linked is null)
            return null;
        var snapshot = await GatherCoreAsync(linked.Token).ConfigureAwait(false);
        var result = await StartAuthorized(() => _llm.CompleteAsync(new LlmRequest(PromptBuilder.Build(snapshot)), linked.Token),
            linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        return string.IsNullOrWhiteSpace(result.Text) ? null : new AssistantSuggestion("Suggestion", result.Text, null);
    }

    public async Task<ContextSnapshot> GatherAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CreateCycle(cancellationToken);
        if (linked is null)
            return new ContextSnapshot(new UiSnapshot("", null, Array.Empty<string>()),
                Array.Empty<AudioTranscriptSegment>(), Array.Empty<ScreenTextRegion>(), DateTimeOffset.UtcNow);
        return await GatherCoreAsync(linked.Token).ConfigureAwait(false);
    }

    private CancellationTokenSource? CreateCycle(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_consent)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _consent.IsObservationEnabled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _epoch.Token)
                : null;
        }
    }

    private async Task<ContextSnapshot> GatherCoreAsync(CancellationToken token)
    {
        var ui = await StartAuthorized(() => Task.FromResult(_inspector.GetCurrentSnapshot()), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        IReadOnlyList<ScreenTextRegion> screenText = Array.Empty<ScreenTextRegion>();
        bool screenEnabled;
        lock (_consent) screenEnabled = _consent.IsScreenEnabled;
        if (screenEnabled)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
                throw new PlatformNotSupportedException("Screen bitmap capture requires Windows.");
            using var bitmap = await StartAuthorized(() => _screen.CaptureAsync(token), token, screen: true)
                .ConfigureAwait(false);
            screenText = await StartAuthorized(() => _ocr.ExtractTextAsync(bitmap, token), token, screen: true)
                .ConfigureAwait(false);
        }
        IReadOnlyList<AudioTranscriptSegment> transcript = Array.Empty<AudioTranscriptSegment>();
        IReadOnlyList<AudioChunkEventArgs> audioData;
        lock (_consent)
        {
            token.ThrowIfCancellationRequested();
            audioData = _consent.IsAudioEnabled ? DrainAudio() : Array.Empty<AudioChunkEventArgs>();
        }
        try
        {
            if (audioData.Count > 0)
                transcript = await StartAuthorized(
                    () => _transcription.TranscribeAsync(audioData, token), token, audio: true)
                    .ConfigureAwait(false);
        }
        finally
        {
            foreach (var chunk in audioData)
                Array.Clear(chunk.Buffer);
        }
        token.ThrowIfCancellationRequested();
        return new ContextSnapshot(ui, transcript, screenText, DateTimeOffset.UtcNow);
    }

    // Starting each operation while holding the consent lock closes the check/send revocation race.
    private Task<T> StartAuthorized<T>(Func<Task<T>> start, CancellationToken token, bool screen = false, bool audio = false)
    {
        lock (_consent)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_consent.IsObservationEnabled || screen && !_consent.IsScreenEnabled || audio && !_consent.IsAudioEnabled)
                throw new OperationCanceledException("Capture consent was revoked.", token);
            return start();
        }
    }

    private void OnAudioChunk(object? sender, AudioChunkEventArgs chunk)
    {
        lock (_consent)
        {
            if (_disposed || !_consent.IsObservationEnabled || !_consent.IsAudioEnabled ||
                chunk.SampleRate <= 0 || chunk.Channels <= 0 || chunk.Channels > 8)
                return;
            var alignment = chunk.Channels * 2;
            if (chunk.Buffer.Length == 0 || chunk.Buffer.Length % alignment != 0)
                return;
            if (_sampleRate != chunk.SampleRate || _channels != chunk.Channels)
                ClearAudio();
            _sampleRate = chunk.SampleRate;
            _channels = chunk.Channels;
            var count = Math.Min(chunk.Buffer.Length, MaximumAudioBufferBytes / alignment * alignment);
            var copy = new AudioChunkEventArgs(chunk.Buffer.AsSpan(chunk.Buffer.Length - count, count).ToArray(),
                chunk.SampleRate, chunk.Channels);
            while (_bufferBytes + count > MaximumAudioBufferBytes && _chunks.Count > 0)
            {
                var removed = _chunks.Dequeue();
                _bufferBytes -= removed.Buffer.Length;
                Array.Clear(removed.Buffer);
            }
            _chunks.Enqueue(copy);
            _bufferBytes += copy.Buffer.Length;
        }
    }

    private IReadOnlyList<AudioChunkEventArgs> DrainAudio()
    {
        var result = _chunks.Select(chunk =>
            new AudioChunkEventArgs(chunk.Buffer, chunk.SampleRate, chunk.Channels)).ToArray();
        ClearAudio();
        return result;
    }

    private void ClearAudio()
    {
        while (_chunks.Count > 0)
            Array.Clear(_chunks.Dequeue().Buffer);
        _bufferBytes = 0;
    }

    private void OnConsentChanged(object? sender, EventArgs args)
    {
        lock (_consent)
        {
            var previous = _epoch;
            _epoch = new CancellationTokenSource();
            previous.Cancel();
            previous.Dispose();
            if (!_consent.IsObservationEnabled || !_consent.IsAudioEnabled)
                ClearAudio();
        }
    }

    public void Dispose()
    {
        lock (_consent)
        {
            if (_disposed) return;
            _disposed = true;
            _audio.AudioChunkCaptured -= OnAudioChunk;
            _consent.Changed -= OnConsentChanged;
            ClearAudio();
            _epoch.Cancel();
            _epoch.Dispose();
        }
    }
}
