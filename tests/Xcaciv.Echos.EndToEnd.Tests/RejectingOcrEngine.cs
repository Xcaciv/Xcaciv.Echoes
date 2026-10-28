using System.Drawing;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.EndToEnd.Tests;

internal sealed class RejectingOcrEngine : IOcrEngine
{
    public Task<IReadOnlyList<ScreenTextRegion>> ExtractTextAsync(Bitmap bitmap, CancellationToken ct = default) =>
        throw new InvalidOperationException("OCR must not run without consent.");
}
