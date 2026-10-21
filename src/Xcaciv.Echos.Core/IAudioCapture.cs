namespace Xcaciv.Echos.Core;

public interface IAudioCapture
{
    event EventHandler<AudioChunkEventArgs>? AudioChunkCaptured;
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}
