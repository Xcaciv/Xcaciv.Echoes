namespace Xcaciv.Echos.Core;

public interface IContextEngine
{
    Task<AssistantSuggestion?> GetSuggestionAsync(CancellationToken ct = default);
}
