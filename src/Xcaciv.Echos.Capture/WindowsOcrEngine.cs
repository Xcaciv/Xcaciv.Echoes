using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.Capture;

/// <summary>Windows OCR; recognition requires an installed Windows OCR language pack.</summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly OcrEngine engine;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly int maximumPixels;

    public WindowsOcrEngine(string? languageTag = null, int maximumPixels = 16_777_216)
    {
        if (maximumPixels < 1 || maximumPixels > int.MaxValue / 4)
            throw new ArgumentOutOfRangeException(nameof(maximumPixels));
        this.maximumPixels = maximumPixels;
        engine = languageTag is null
            ? OcrEngine.TryCreateFromUserProfileLanguages()
            : OcrEngine.TryCreateFromLanguage(new Language(languageTag));
        if (engine is null)
            throw new InvalidOperationException("No supported Windows OCR language is installed.");
    }

    public async Task<IReadOnlyList<ScreenTextRegion>> ExtractTextAsync(Bitmap bitmap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.Width > OcrEngine.MaxImageDimension || bitmap.Height > OcrEngine.MaxImageDimension ||
            (long)bitmap.Width * bitmap.Height > maximumPixels)
            throw new ArgumentException("The image exceeds Windows OCR's maximum dimensions.", nameof(bitmap));
        cancellationToken.ThrowIfCancellationRequested();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var converted = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(converted))
                graphics.DrawImageUnscaled(bitmap, 0, 0);
            var data = converted.LockBits(new Rectangle(0, 0, converted.Width, converted.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] pixels;
            try
            {
                var rowBytes = checked(converted.Width * 4);
                pixels = new byte[checked(rowBytes * converted.Height)];
                for (var row = 0; row < converted.Height; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), pixels, row * rowBytes, rowBytes);
                }
            }
            finally
            {
                converted.UnlockBits(data);
            }

            using var writer = new DataWriter();
            writer.WriteBytes(pixels);
            using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(),
                BitmapPixelFormat.Bgra8, converted.Width, converted.Height, BitmapAlphaMode.Ignore);
            var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken).ConfigureAwait(false);
            var regions = new List<ScreenTextRegion>();
            foreach (var line in result.Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Words.Count == 0)
                    continue;
                var left = line.Words.Min(word => word.BoundingRect.Left);
                var top = line.Words.Min(word => word.BoundingRect.Top);
                var right = line.Words.Max(word => word.BoundingRect.Right);
                var bottom = line.Words.Max(word => word.BoundingRect.Bottom);
                regions.Add(new ScreenTextRegion(line.Text, Rectangle.FromLTRB((int)Math.Floor(left),
                    (int)Math.Floor(top), (int)Math.Ceiling(right), (int)Math.Ceiling(bottom))));
            }
            return regions;
        }
        finally
        {
            gate.Release();
        }
    }
}
