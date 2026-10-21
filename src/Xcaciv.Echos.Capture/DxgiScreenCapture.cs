using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.Capture;

/// <summary>
/// Compatibility screen capturer using GDI CopyFromScreen, not DXGI duplication.
/// Captures visible pixels only; protected content and secure desktops are unavailable.
/// The caller owns and must dispose the returned bitmap.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class DxgiScreenCapture : IScreenCapture
{
    private readonly ScreenCaptureOptions options;

    public DxgiScreenCapture(ScreenCaptureOptions? options = null)
    {
        this.options = options ?? new ScreenCaptureOptions();
        if (this.options.MaximumPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public Task<Bitmap> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bounds = options.CaptureVirtualDesktop
            ? SystemInformation.VirtualScreen
            : Screen.PrimaryScreen?.Bounds ?? throw new InvalidOperationException("No primary display is available.");
        if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > options.MaximumPixels)
            throw new InvalidOperationException("Display size exceeds the configured capture limit.");

        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(bitmap);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
