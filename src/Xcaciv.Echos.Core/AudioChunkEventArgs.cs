namespace Xcaciv.Echos.Core;

public sealed class AudioChunkEventArgs : EventArgs
{
    public byte[] Buffer { get; }
    public int SampleRate { get; }
    public int Channels { get; }

    public AudioChunkEventArgs(byte[] buffer, int sampleRate, int channels)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Buffer = (byte[])buffer.Clone();
        SampleRate = sampleRate;
        Channels = channels;
    }
}
