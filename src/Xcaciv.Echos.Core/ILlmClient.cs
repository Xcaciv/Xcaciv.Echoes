namespace Xcaciv.Echos.Core;

public interface ILlmClient
{
    Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}
