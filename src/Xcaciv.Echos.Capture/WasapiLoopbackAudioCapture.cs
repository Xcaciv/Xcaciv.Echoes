using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.Capture;

/// <summary>
/// Captures the default render device. Chunks are signed PCM16, little endian, interleaved
/// at the device's sample rate and channel count. No capture begins until StartAsync.
/// Subscribers execute on the capture thread and must not block or call Dispose.
/// </summary>
public sealed class WasapiLoopbackAudioCapture : IAudioCapture, IDisposable, IAsyncDisposable
{
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private readonly object gate = new();
    private Session? active;
    private bool disposed;
    [ThreadStatic] private static bool inCallback;

    public event EventHandler<AudioChunkEventArgs>? AudioChunkCaptured;
    public event EventHandler<AudioCaptureFailedEventArgs>? CaptureFailed;
    public Exception? LastError { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active is not null)
                throw new InvalidOperationException("Capture is already running; stop it before restarting.");
            var capture = CreateCapture();
            var session = new Session(capture);
            capture.DataAvailable += (_, args) => OnDataAvailable(session, args);
            capture.RecordingStopped += (_, args) =>
            {
                if (args.Exception is not null)
                    ReportFailure(session, args.Exception);
                session.Stopped.TrySetResult();
            };
            active = session;
            LastError = null;
            try
            {
                ValidateFormat(capture.WaveFormat);
                cancellationToken.ThrowIfCancellationRequested();
                capture.StartRecording();
                session.Cancellation = cancellationToken.Register(RequestStopOnCancellation);
            }
            catch
            {
                active = null;
                capture.Dispose();
                throw;
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            if (active is null)
                return Task.CompletedTask;
            var session = active;
            if (session.StopTask is not null)
                return session.StopTask;
            if (!session.Stopped.Task.IsCompleted && Interlocked.Exchange(ref session.StopRequested, 1) == 0)
                session.Capture.StopRecording();
            // Never dispose a cancellation registration while holding gate: its callback
            // may itself be waiting to enter StopAsync.
            return session.StopTask = Task.Run(() => FinishStopAsync(session));
        }
    }

    private void RequestStopOnCancellation()
    {
        try
        {
            // Fire and forget: the stop task is observed by whoever calls StopAsync/Dispose.
            _ = StopAsync();
        }
        catch (Exception error) when (error is InvalidOperationException or COMException or ObjectDisposedException)
        {
            // The device may already be gone or stopping; cancellation must not throw into the canceller.
            LastError = error;
        }
    }

    private async Task FinishStopAsync(Session session)
    {
        await session.Stopped.Task.ConfigureAwait(false);
        CancellationTokenRegistration cancellation;
        lock (gate)
            cancellation = session.Cancellation;
        try
        {
            cancellation.Dispose();
            session.Capture.Dispose();
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(active, session))
                    active = null;
            }
        }
    }

    private static WasapiLoopbackCapture CreateCapture()
    {
        // NAudio otherwise posts RecordingStopped to a captured UI context; synchronously
        // disposing on that UI thread would then deadlock while waiting for the event.
        var context = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new WasapiLoopbackCapture(device);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    public void Dispose()
    {
        if (inCallback)
            throw new InvalidOperationException("Dispose cannot run inside an audio callback; use StopAsync and dispose from the owner.");
        lock (gate)
            disposed = true;
        // StopAsync only signals the device to stop under the gate; the native teardown runs on a
        // thread-pool task (Task.Run + ConfigureAwait(false)) and the capture was created with no
        // SynchronizationContext, so RecordingStopped is never posted back to the caller's context.
        // Waiting here therefore cannot deadlock on a UI thread, but the wait is bounded as a safeguard.
        var stop = StopAsync();
        try
        {
            if (!stop.Wait(TimeSpan.FromSeconds(5)))
                LastError = new TimeoutException("Audio capture did not stop within the dispose timeout.");
        }
        catch (AggregateException error)
        {
            LastError = error.GetBaseException();
        }
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
            disposed = true;
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void OnDataAvailable(Session session, WaveInEventArgs args)
    {
        if (args.BytesRecorded <= 0)
            return;
        inCallback = true;
        try
        {
            var format = session.Capture.WaveFormat;
            if (args.BytesRecorded > 4 * 1024 * 1024 || args.BytesRecorded % format.BlockAlign != 0)
                throw new InvalidDataException("Unaligned or oversized audio capture chunk.");
            var sampleBytes = format.BitsPerSample / 8;
            var samples = args.BytesRecorded / sampleBytes;
            var pcm = new byte[checked(samples * 2)];
            var floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                format is WaveFormatExtensible extensible && extensible.SubFormat == FloatSubFormat;
            for (var index = 0; index < samples; index++)
            {
                var source = args.Buffer.AsSpan(index * sampleBytes, sampleBytes);
                short value;
                if (floating)
                {
                    var sample = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));
                    value = float.IsFinite(sample) ? (short)Math.Clamp(sample * 32768f, short.MinValue, short.MaxValue) : (short)0;
                }
                else
                {
                    value = sampleBytes switch
                    {
                        2 => BinaryPrimitives.ReadInt16LittleEndian(source),
                        3 => (short)((source[1] | source[2] << 8)),
                        4 => (short)(BinaryPrimitives.ReadInt32LittleEndian(source) >> 16),
                        _ => throw new InvalidDataException("Unsupported audio bit depth.")
                    };
                }
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 2), value);
            }
            var handlers = AudioChunkCaptured;
            if (handlers is null)
                return;
            var chunk = new AudioChunkEventArgs(pcm, format.SampleRate, format.Channels);
            foreach (EventHandler<AudioChunkEventArgs> handler in handlers.GetInvocationList())
            {
                try { handler(this, chunk); }
                catch (Exception handlerError) when (handlerError is not (OutOfMemoryException or AccessViolationException))
                {
                    // A subscriber must not terminate the audio capture thread; fatal runtime errors are not caught.
                }
            }
        }
        catch (Exception error)
        {
            ReportFailure(session, error);
            if (Interlocked.Exchange(ref session.StopRequested, 1) == 0)
                session.Capture.StopRecording();
        }
        finally
        {
            inCallback = false;
        }
    }

    private void ReportFailure(Session session, Exception error)
    {
        LastError = error;
        if (Interlocked.Exchange(ref session.FailureReported, 1) != 0)
            return;
        var handlers = CaptureFailed;
        if (handlers is null)
            return;
        var args = new AudioCaptureFailedEventArgs(error);
        foreach (EventHandler<AudioCaptureFailedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception handlerError) when (handlerError is not (OutOfMemoryException or AccessViolationException))
            {
            }
        }
    }

    private static void ValidateFormat(WaveFormat format)
    {
        var floating = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat;
        var pcm = format.Encoding == WaveFormatEncoding.Pcm ||
            format is WaveFormatExtensible extensible && extensible.SubFormat == PcmSubFormat;
        if ((!floating && !pcm) || floating && format.BitsPerSample != 32 ||
            pcm && format.BitsPerSample is not (16 or 24 or 32) || format.Channels < 1 ||
            format.Channels > 8 || format.SampleRate < 8000 || format.SampleRate > 384000 ||
            format.BlockAlign != format.Channels * (format.BitsPerSample / 8))
            throw new NotSupportedException("Loopback device must expose PCM16/24/32 or float32 with 1–8 channels.");
    }

    private sealed class Session
    {
        public Session(WasapiLoopbackCapture capture) => Capture = capture;
        public WasapiLoopbackCapture Capture { get; }
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Cancellation { get; set; }
        public Task? StopTask { get; set; }
        public int FailureReported;
        public int StopRequested;
    }
}
