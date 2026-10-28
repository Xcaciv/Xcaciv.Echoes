namespace Xcaciv.Echos.Core;

public interface IAudioCapture
{
    event EventHandler<AudioChunkEventArgs>? AudioChunkCaptured;
    event EventHandler<AudioCaptureFailedEventArgs>? CaptureFailed;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}
