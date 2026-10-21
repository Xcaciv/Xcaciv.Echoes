namespace Xcaciv.Echos.Capture;

public sealed class UiInspectionOptions
{
    public int MaximumElements { get; init; } = 200;
    public int MaximumDepth { get; init; } = 8;
    public int MaximumTextLength { get; init; } = 512;
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromMilliseconds(250);
}
