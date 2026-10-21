using System.Drawing;
using Xcaciv.Echos.Capture;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.App;

internal sealed class LazyWindowsOcrEngine : IOcrEngine
{
    private readonly Lazy<WindowsOcrEngine> engine = new(() => new WindowsOcrEngine());

    public Task<IReadOnlyList<ScreenTextRegion>> ExtractTextAsync(Bitmap bitmap,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return engine.Value.ExtractTextAsync(bitmap, cancellationToken);
    }
}
