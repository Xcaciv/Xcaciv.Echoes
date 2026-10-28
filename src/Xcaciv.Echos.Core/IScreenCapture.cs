using System.Drawing;

namespace Xcaciv.Echos.Core;

public interface IScreenCapture
{
    Task<Bitmap> CaptureAsync(CancellationToken cancellationToken = default);
}
