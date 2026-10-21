namespace Xcaciv.Echos.Tests;

internal sealed class NonSeekableMemoryStream : MemoryStream
{
    public NonSeekableMemoryStream(byte[] buffer) : base(buffer, writable: false) { }
    public override bool CanSeek => false;
}
