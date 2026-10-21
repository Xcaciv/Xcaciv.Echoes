namespace Xcaciv.Echos.Core;

public sealed class MouseEventArgs : EventArgs
{
    public int X { get; }
    public int Y { get; }
    public string Button { get; }
    public bool IsButtonDown { get; }
    public DateTimeOffset Timestamp { get; }

    public MouseEventArgs(int x, int y, string button, bool isButtonDown, DateTimeOffset timestamp)
    {
        X = x;
        Y = y;
        Button = button;
        IsButtonDown = isButtonDown;
        Timestamp = timestamp;
    }
}
