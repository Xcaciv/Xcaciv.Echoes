namespace Xcaciv.Echos.Core;

public record ContextSnapshot(
    UiSnapshot Ui,
    IReadOnlyList<AudioTranscriptSegment> Audio,
    IReadOnlyList<ScreenTextRegion> ScreenText,
    DateTimeOffset Timestamp);
