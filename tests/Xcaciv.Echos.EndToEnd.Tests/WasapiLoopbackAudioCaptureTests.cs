using System.Diagnostics;
using System.Collections.Concurrent;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xcaciv.Echos.Capture;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.EndToEnd.Tests;

public sealed class WasapiLoopbackAudioCaptureTests
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    [AudioFact]
    public async Task StartAndStopReleaseTheDefaultRenderDevice()
    {
        using var capture = new WasapiLoopbackAudioCapture();
        var failures = new ConcurrentQueue<Exception>();
        capture.CaptureFailed += (_, args) => failures.Enqueue(args.Error);
        await capture.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await capture.StopAsync().WaitAsync(StopTimeout);
        Assert.True(failures.IsEmpty, "Capture reported a failure.");
        Assert.Null(capture.LastError);

        await capture.StartAsync();
        await capture.StopAsync().WaitAsync(StopTimeout);
    }

    [AudioFact]
    public async Task ChunksArePcm16AndFrameAligned()
    {
        using var capture = new WasapiLoopbackAudioCapture();
        var chunks = new ConcurrentQueue<AudioChunkEventArgs>();
        capture.AudioChunkCaptured += (_, args) => chunks.Enqueue(args);
        await capture.StartAsync();
        try
        {
            using var output = new WasapiOut();
            var tone = new SignalGenerator { Frequency = 440, Gain = 0.2 }.Take(TimeSpan.FromSeconds(1.5));
            output.Init(tone);
            output.Play();
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(3))
                await Task.Delay(100);
            output.Stop();
        }
        finally
        {
            await capture.StopAsync().WaitAsync(StopTimeout);
        }
        Assert.NotEmpty(chunks);
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.Buffer.Length > 0, "A chunk was empty.");
            Assert.True(chunk.Buffer.Length % (2 * chunk.Channels) == 0, "A chunk was not frame aligned.");
            Assert.InRange(chunk.SampleRate, 8000, 384000);
            Assert.InRange(chunk.Channels, 1, 8);
        }
    }

    [AudioFact]
    public async Task StartingTwiceIsRejected()
    {
        using var capture = new WasapiLoopbackAudioCapture();
        await capture.StartAsync();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => capture.StartAsync());
        }
        finally
        {
            await capture.StopAsync().WaitAsync(StopTimeout);
        }
    }

    [AudioFact]
    public async Task CancellationStopsCapture()
    {
        using var capture = new WasapiLoopbackAudioCapture();
        using var cancellation = new CancellationTokenSource();
        await capture.StartAsync(cancellation.Token);
        cancellation.Cancel();
        var clock = Stopwatch.StartNew();
        var restarted = false;
        while (!restarted)
        {
            try
            {
                await capture.StartAsync();
                restarted = true;
            }
            catch (InvalidOperationException) when (clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                // The asynchronous stop has not finished yet.
                await Task.Delay(100);
            }
        }
        await capture.StopAsync().WaitAsync(StopTimeout);
    }

    [AudioFact]
    public async Task DisposeStopsCapture()
    {
        var capture = new WasapiLoopbackAudioCapture();
        await capture.StartAsync();
        capture.Dispose();
        Assert.Null(capture.LastError);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => capture.StartAsync());
    }
}
