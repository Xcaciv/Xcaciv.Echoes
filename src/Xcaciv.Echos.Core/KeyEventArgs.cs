namespace Xcaciv.Echos.Core;

public sealed class KeyEventArgs : EventArgs
{
    public int VirtualKeyCode { get; }
    public bool IsKeyDown { get; }
    public DateTimeOffset Timestamp { get; }

    public KeyEventArgs(int virtualKeyCode, bool isKeyDown, DateTimeOffset timestamp)
    {
        VirtualKeyCode = virtualKeyCode;
        IsKeyDown = isKeyDown;
        Timestamp = timestamp;
    }
}
