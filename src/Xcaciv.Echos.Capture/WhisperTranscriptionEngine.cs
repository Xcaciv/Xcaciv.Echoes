using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.Capture;

/// <summary>
/// Sends bounded, interleaved signed 16-bit little-endian PCM as a WAV multipart upload.
/// The caller owns HttpClient and should disable automatic redirects on its handler.
/// </summary>
public sealed class WhisperTranscriptionEngine : ITranscriptionEngine
{
    private readonly HttpClient client;
    private readonly WhisperTranscriptionOptions options;

    public WhisperTranscriptionEngine(HttpClient client, WhisperTranscriptionOptions options)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
    }

    public async Task<IReadOnlyList<AudioTranscriptSegment>> TranscribeAsync(
        IReadOnlyList<AudioChunkEventArgs> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();
        if (chunks.Count > 4096)
            throw new ArgumentException("At most 4096 audio chunks can be transcribed in one request.", nameof(chunks));
        var snapshot = new AudioChunkEventArgs[chunks.Count];
        long totalBytes = 0;
        var formatGroups = 0;
        for (var index = 0; index < snapshot.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunks[index] ?? throw new ArgumentException("An audio chunk is null.", nameof(chunks));
            if (chunk.SampleRate < 8000 || chunk.SampleRate > 384000 || chunk.Channels < 1 || chunk.Channels > 8 ||
                chunk.Buffer.Length % (chunk.Channels * 2) != 0)
                throw new ArgumentException("Chunks must contain aligned PCM16 audio with a supported format.", nameof(chunks));
            snapshot[index] = chunk;
            totalBytes += chunk.Buffer.Length;
            if (totalBytes > options.MaximumAudioBytes)
                throw new ArgumentException("Audio exceeds the configured batch limit.", nameof(chunks));
            if (index == 0 || snapshot[index - 1].SampleRate != chunk.SampleRate ||
                snapshot[index - 1].Channels != chunk.Channels)
                formatGroups++;
        }
        if (formatGroups > 16)
            throw new ArgumentException("A batch can contain at most 16 contiguous format groups.", nameof(chunks));
        if (totalBytes == 0)
            return Array.Empty<AudioTranscriptSegment>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        var segments = new List<AudioTranscriptSegment>();
        for (var first = 0; first < snapshot.Length;)
        {
            var end = first + 1;
            var length = snapshot[first].Buffer.Length;
            while (end < snapshot.Length && snapshot[end].SampleRate == snapshot[first].SampleRate &&
                snapshot[end].Channels == snapshot[first].Channels)
                length = checked(length + snapshot[end++].Buffer.Length);
            var pcm = new byte[length];
            var offset = 0;
            for (var index = first; index < end; index++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                snapshot[index].Buffer.CopyTo(pcm, offset);
                offset += snapshot[index].Buffer.Length;
            }
            var text = await TranscribePcmAsync(pcm, snapshot[first].SampleRate,
                snapshot[first].Channels, timeout.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
                segments.Add(new AudioTranscriptSegment(text, DateTimeOffset.UtcNow));
            first = end;
        }
        return segments;
    }

    private async Task<string> TranscribePcmAsync(byte[] audioData, int sampleRate, int channels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audioData);
        if (sampleRate < 8000 || sampleRate > 384000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels < 1 || channels > 8)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (audioData.Length > options.MaximumAudioBytes || audioData.Length % (channels * 2) != 0)
            throw new ArgumentException("Audio exceeds the limit or is not aligned 16-bit PCM.", nameof(audioData));
        cancellationToken.ThrowIfCancellationRequested();
        if (audioData.Length == 0)
            return string.Empty;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        cancellationToken = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(CreateWave(audioData, sampleRate, channels));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "capture.wav");
        form.Add(new StringContent(options.Model), "model");
        form.Add(new StringContent("json"), "response_format");
        request.Content = form;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > options.MaximumResponseBytes)
            throw new InvalidDataException("Transcription response exceeds the configured limit.");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            if (body.Length + count > options.MaximumResponseBytes)
                throw new InvalidDataException("Transcription response exceeds the configured limit.");
            body.Write(buffer, 0, count);
        }
        using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        if (!document.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The transcription response does not contain a text string.");
        return text.GetString() ?? string.Empty;
    }

    private static byte[] CreateWave(byte[] pcm, int sampleRate, int channels)
    {
        var wave = new byte[checked(44 + pcm.Length)];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wave, 8);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), checked(sampleRate * channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wave, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), pcm.Length);
        pcm.CopyTo(wave, 44);
        return wave;
    }
}
