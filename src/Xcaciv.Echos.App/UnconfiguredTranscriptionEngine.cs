using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.App;

internal sealed class UnconfiguredTranscriptionEngine : ITranscriptionEngine
{
    public Task<IReadOnlyList<AudioTranscriptSegment>> TranscribeAsync(IReadOnlyList<AudioChunkEventArgs> chunks,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Configure the transcription endpoint and model before enabling audio.");
}
