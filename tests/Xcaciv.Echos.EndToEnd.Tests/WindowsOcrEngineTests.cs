using System.Drawing;
using System.Drawing.Text;
using Xcaciv.Echos.Capture;

namespace Xcaciv.Echos.EndToEnd.Tests;

public sealed class WindowsOcrEngineTests
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    [OcrFact]
    public async Task RecognisesRenderedText()
    {
        using var bitmap = new Bitmap(1000, 240);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Arial", 48, FontStyle.Bold))
        {
            graphics.Clear(Color.White);
            graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
            graphics.DrawString("ECHOS WINDOWS TEST", font, Brushes.Black, 20, 60);
        }
        using var cancellation = new CancellationTokenSource(CallTimeout);
        var regions = await new WindowsOcrEngine().ExtractTextAsync(bitmap, cancellation.Token);
        var text = string.Concat(regions.Select(region => region.Text)).ToUpperInvariant().Replace(" ", string.Empty);
        Assert.Contains("ECHOS", text);
        var area = new Rectangle(Point.Empty, bitmap.Size);
        Assert.All(regions, region =>
        {
            Assert.False(string.IsNullOrWhiteSpace(region.Text));
            Assert.True(region.Bounds.Width > 0 && region.Bounds.Height > 0);
            Assert.True(area.Contains(region.Bounds));
        });
    }

    [OcrFact]
    public async Task BlankImageYieldsNoText()
    {
        using var bitmap = WhiteBitmap(400, 200);
        using var cancellation = new CancellationTokenSource(CallTimeout);
        var regions = await new WindowsOcrEngine().ExtractTextAsync(bitmap, cancellation.Token);
        Assert.DoesNotContain(regions, region => !string.IsNullOrWhiteSpace(region.Text));
    }

    [OcrFact]
    public async Task CancelledTokenIsHonoured()
    {
        using var bitmap = WhiteBitmap(400, 200);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsOcrEngine().ExtractTextAsync(bitmap, cancellation.Token));
    }

    [OcrFact]
    public async Task OversizedImageIsRejected()
    {
        using var bitmap = WhiteBitmap(20, 20);
        using var cancellation = new CancellationTokenSource(CallTimeout);
        var engine = new WindowsOcrEngine(maximumPixels: 100);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.ExtractTextAsync(bitmap, cancellation.Token));
    }

    private static Bitmap WhiteBitmap(int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        return bitmap;
    }
}
