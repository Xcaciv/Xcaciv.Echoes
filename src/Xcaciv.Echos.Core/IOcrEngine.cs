using System.Drawing;

namespace Xcaciv.Echos.Core;

public interface IOcrEngine
{
    Task<IReadOnlyList<ScreenTextRegion>> ExtractTextAsync(Bitmap bitmap, CancellationToken ct = default);
}
