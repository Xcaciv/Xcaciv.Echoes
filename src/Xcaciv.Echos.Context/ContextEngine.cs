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
        using var linked = CreateCycle(ct, out var revision);
        if (linked is null)
            return null;
        var snapshot = await GatherCoreAsync(linked.Token, revision).ConfigureAwait(false);
        var result = await StartAuthorized(
            () => _llm.CompleteAsync(new LlmRequest(PromptBuilder.Build(snapshot)), linked.Token),
            linked.Token, revision).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        EnsureRevision(revision, linked.Token);
        return string.IsNullOrWhiteSpace(result.Text) ? null : new AssistantSuggestion("Suggestion", result.Text, null);
    }

    public async Task<ContextSnapshot> GatherAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CreateCycle(cancellationToken, out var revision);
        if (linked is null)
            return new ContextSnapshot(new UiSnapshot("", null, Array.Empty<string>()),
                Array.Empty<AudioTranscriptSegment>(), Array.Empty<ScreenTextRegion>(), DateTimeOffset.UtcNow);
        return await GatherCoreAsync(linked.Token, revision).ConfigureAwait(false);
    }

    private CancellationTokenSource? CreateCycle(CancellationToken cancellationToken, out long revision)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        revision = _consent.Revision;
        CancellationTokenSource? cycle = null;
        _consent.TryStart(revision, false, false, () =>
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

    private async Task<ContextSnapshot> GatherCoreAsync(CancellationToken token, long revision)
    {
        var ui = await StartAuthorized(() => Task.FromResult(_inspector.GetCurrentSnapshot()), token, revision)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        IReadOnlyList<ScreenTextRegion> screenText = Array.Empty<ScreenTextRegion>();
        if (_consent.GetFlags().Screen)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
                throw new PlatformNotSupportedException("Screen bitmap capture requires Windows.");
            using var bitmap = await StartAuthorized(
                    () => _screen.CaptureAsync(token), token, revision, screen: true)
                .ConfigureAwait(false);
            screenText = await StartAuthorized(
                    () => _ocr.ExtractTextAsync(bitmap, token), token, revision, screen: true)
                .ConfigureAwait(false);
        }
        IReadOnlyList<AudioTranscriptSegment> transcript = Array.Empty<AudioTranscriptSegment>();
        IReadOnlyList<AudioChunkEventArgs> audioData = Array.Empty<AudioChunkEventArgs>();
        token.ThrowIfCancellationRequested();
        _consent.TryStart(revision, false, true, () => audioData = DrainAudio());
        try
        {
            if (audioData.Count > 0)
                transcript = await StartAuthorized(
                    () => _transcription.TranscribeAsync(audioData, token), token, revision, audio: true)
                    .ConfigureAwait(false);
        }
        finally
        {
            foreach (var chunk in audioData)
                Array.Clear(chunk.Buffer);
        }
        token.ThrowIfCancellationRequested();
        EnsureRevision(revision, token);
        return new ContextSnapshot(ui, transcript, screenText, DateTimeOffset.UtcNow);
    }

    // The consent check and worker handoff happen atomically; the worker rechecks revision and flags immediately
    // before invoking the adapter. Changed is raised after consent state is updated, so stale handoffs are rejected.
    private Task<T> StartAuthorized<T>(Func<Task<T>> start, CancellationToken token, long revision,
        bool screen = false, bool audio = false)
    {
        token.ThrowIfCancellationRequested();
        Task<T>? started = null;
        var authorized = _consent.TryStart(revision, screen, audio, () =>
        {
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            started = Task.Run(async () =>
            {
                EnsureAuthorized(revision, token, screen, audio);
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                return await start().ConfigureAwait(false);
            }, token);
        });
        if (!authorized)
            throw new OperationCanceledException("Capture consent was revoked.", token);
        return started!;
    }

    private void EnsureAuthorized(long revision, CancellationToken token, bool screen, bool audio)
    {
        if (!_consent.TryStart(revision, screen, audio, static () => { }))
            throw new OperationCanceledException("Capture consent changed.", token);
    }

    private void EnsureRevision(long revision, CancellationToken token)
    {
        if (!_consent.TryStart(revision, false, false, static () => { }))
            throw new OperationCanceledException("Capture consent changed.", token);
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
            ClearAudio();
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
