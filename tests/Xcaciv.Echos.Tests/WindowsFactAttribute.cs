namespace Xcaciv.Echos.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "System.Drawing.Bitmap capture requires Windows.";
    }
}
