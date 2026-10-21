namespace Xcaciv.Echos.Core;

public interface ITranscriptionEngine
{
    Task<IReadOnlyList<AudioTranscriptSegment>> TranscribeAsync(
        IReadOnlyList<AudioChunkEventArgs> chunks, CancellationToken ct = default);
}
