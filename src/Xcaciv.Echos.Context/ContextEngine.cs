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
    // Lock order: CaptureConsent's private lock, then _gate. Never take consent state while holding _gate.
    private readonly object _gate = new();
    private readonly Queue<AudioChunkEventArgs> _chunks = new();
    private CancellationTokenSource _epoch = new();
    private int _generation;
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

    public int BufferedAudioBytes { get { lock (_gate) return _bufferBytes; } }

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
        ThrowIfDisposed();
        CancellationTokenSource? cycle = null;
        _consent.TryStart(false, false, () =>
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _epoch.Token);
            }
        });
        return cycle;
    }

    private void ThrowIfDisposed()
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async Task<ContextSnapshot> GatherCoreAsync(CancellationToken token)
    {
        // UI Automation is synchronous and may be slow: run it on the pool, never inside a lock.
        var ui = await StartAuthorized(() => Task.Run(() => _inspector.GetCurrentSnapshot(), token), token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        IReadOnlyList<ScreenTextRegion> screenText = Array.Empty<ScreenTextRegion>();
        if (_consent.GetFlags().Screen)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
                throw new PlatformNotSupportedException("Screen bitmap capture requires Windows.");
            using var bitmap = await StartAuthorized(() => _screen.CaptureAsync(token), token, screen: true)
                .ConfigureAwait(false);
            screenText = await StartAuthorized(() => _ocr.ExtractTextAsync(bitmap, token), token, screen: true)
                .ConfigureAwait(false);
        }
        IReadOnlyList<AudioTranscriptSegment> transcript = Array.Empty<AudioTranscriptSegment>();
        IReadOnlyList<AudioChunkEventArgs> audioData = Array.Empty<AudioChunkEventArgs>();
        token.ThrowIfCancellationRequested();
        _consent.TryStart(false, true, () => audioData = DrainAudio());
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

    // The check and the (quick) start happen atomically inside CaptureConsent.TryStart, which closes the
    // check/send revocation race. Changed is raised after consent state is updated, so anything started between
    // the update and the epoch cancel is rejected here.
    private Task<T> StartAuthorized<T>(Func<Task<T>> start, CancellationToken token, bool screen = false, bool audio = false)
    {
        token.ThrowIfCancellationRequested();
        Task<T>? started = null;
        var authorized = _consent.TryStart(screen, audio, () =>
        {
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            started = start();
        });
        if (!authorized)
            throw new OperationCanceledException("Capture consent was revoked.", token);
        return started!;
    }

    private void OnAudioChunk(object? sender, AudioChunkEventArgs chunk)
    {
        // Read consent before taking _gate; a generation bump (consent changed meanwhile) drops the chunk.
        var generation = Volatile.Read(ref _generation);
        var flags = _consent.GetFlags();
        if (!flags.Observation || !flags.Audio)
            return;
        lock (_gate)
        {
            if (_disposed || generation != _generation ||
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
        lock (_gate)
        {
            var result = _chunks.Select(chunk =>
                new AudioChunkEventArgs(chunk.Buffer, chunk.SampleRate, chunk.Channels)).ToArray();
            // Buffers are now owned by the caller; drop references without clearing them.
            _chunks.Clear();
            _bufferBytes = 0;
            return result;
        }
    }

    // Caller must hold _gate.
    private void ClearAudio()
    {
        while (_chunks.Count > 0)
            Array.Clear(_chunks.Dequeue().Buffer);
        _bufferBytes = 0;
    }

    private void OnConsentChanged(object? sender, EventArgs args)
    {
        var flags = _consent.GetFlags();
        CancellationTokenSource previous;
        lock (_gate)
        {
            if (_disposed) return;
            previous = _epoch;
            _epoch = new CancellationTokenSource();
            _generation++;
            if (!flags.Observation || !flags.Audio)
                ClearAudio();
        }
        // Cancel outside the lock: registered callbacks are arbitrary code.
        try { previous.Cancel(); }
        finally { previous.Dispose(); }
    }

    public void Dispose()
    {
        CancellationTokenSource epoch;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _audio.AudioChunkCaptured -= OnAudioChunk;
            _consent.Changed -= OnConsentChanged;
            ClearAudio();
            epoch = _epoch;
        }
        try { epoch.Cancel(); }
        finally { epoch.Dispose(); }
    }
}
