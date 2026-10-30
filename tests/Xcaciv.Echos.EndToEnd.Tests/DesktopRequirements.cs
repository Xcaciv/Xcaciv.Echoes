namespace Xcaciv.Echos.EndToEnd.Tests;

internal static class DesktopRequirements
{
    public static string? SkipReason()
    {
        if (!OperatingSystem.IsWindows())
            return "End-to-end tests require Windows.";
        if (!Environment.UserInteractive)
            return "End-to-end tests require an interactive desktop session.";
        return null;
    }
}
