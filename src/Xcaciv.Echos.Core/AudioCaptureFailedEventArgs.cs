namespace Xcaciv.Echos.Core;

public sealed class AudioCaptureFailedEventArgs : EventArgs
{
    public Exception Error { get; }

    public AudioCaptureFailedEventArgs(Exception error) =>
        Error = error ?? throw new ArgumentNullException(nameof(error));
}
