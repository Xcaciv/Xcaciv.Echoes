namespace Xcaciv.Echos.EndToEnd.Tests;

public sealed class DesktopFactAttribute : FactAttribute
{
    public DesktopFactAttribute() => Skip = DesktopRequirements.SkipReason();
}
