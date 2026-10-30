using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.EndToEnd.Tests;

internal sealed class RejectingTranscriptionEngine : ITranscriptionEngine
{
    public Task<IReadOnlyList<AudioTranscriptSegment>> TranscribeAsync(
        IReadOnlyList<AudioChunkEventArgs> chunks, CancellationToken ct = default) =>
        throw new InvalidOperationException("Transcription must not run without consent.");
}
