namespace Xcaciv.Echos.Core;

public record UiSnapshot(string ActiveApp, string? ActiveWindowTitle, IReadOnlyList<string> VisibleTexts);
