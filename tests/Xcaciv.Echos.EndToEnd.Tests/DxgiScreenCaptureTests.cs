using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Xcaciv.Echos.Capture;

namespace Xcaciv.Echos.EndToEnd.Tests;

public sealed class DxgiScreenCaptureTests
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    [DesktopFact]
    public async Task CapturesPrimaryDisplayAtNativeSize()
    {
        using var cancellation = new CancellationTokenSource(CallTimeout);
        using var bitmap = await new DxgiScreenCapture().CaptureAsync(cancellation.Token);
        Assert.Equal(Screen.PrimaryScreen!.Bounds.Size, bitmap.Size);
        Assert.Equal(PixelFormat.Format32bppArgb, bitmap.PixelFormat);
    }

    [DesktopFact]
    public async Task CapturedPixelsIncludeTheForegroundWindow()
    {
        using var window = new ForegroundTestWindow();
        window.BringToForeground();
        using var cancellation = new CancellationTokenSource(CallTimeout);
        using var bitmap = await new DxgiScreenCapture().CaptureAsync(cancellation.Token);
        var origin = Screen.PrimaryScreen!.Bounds.Location;
        var pixel = bitmap.GetPixel(window.SolidColorSampleOnScreen.X - origin.X, window.SolidColorSampleOnScreen.Y - origin.Y);
        // RDP and colour-depth conversion can shift channels slightly.
        const int tolerance = 24;
        Assert.InRange(Math.Abs(pixel.R - window.SolidColor.R), 0, tolerance);
        Assert.InRange(Math.Abs(pixel.G - window.SolidColor.G), 0, tolerance);
        Assert.InRange(Math.Abs(pixel.B - window.SolidColor.B), 0, tolerance);
    }

    [DesktopFact]
    public async Task CancelledTokenIsHonoured()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DxgiScreenCapture().CaptureAsync(cancellation.Token));
    }

    [DesktopFact]
    public async Task DisplayLargerThanConfiguredLimitIsRejected()
    {
        using var cancellation = new CancellationTokenSource(CallTimeout);
        var capture = new DxgiScreenCapture(new ScreenCaptureOptions { MaximumPixels = 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.CaptureAsync(cancellation.Token));
    }
}
