namespace Xcaciv.Echos.Capture;

public sealed class ScreenCaptureOptions
{
    public bool CaptureVirtualDesktop { get; init; }
    public int MaximumPixels { get; init; } = 33_177_600;
}
