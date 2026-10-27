using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xcaciv.Echos.Capture;
using Xcaciv.Echos.Core;
using Xcaciv.Echos.Tests;

namespace Xcaciv.Echos.Capture.Tests;

public sealed class WhisperTranscriptionEngineTests
{
    private static readonly Uri Endpoint = new Uri("https://example.test/v1/audio/transcriptions");

    private static WhisperTranscriptionOptions Options(int maxAudio = 1024 * 1024, int maxResponse = 1024 * 1024,
        string? apiKey = null) =>
        new WhisperTranscriptionOptions
        {
            Endpoint = Endpoint,
            ApiKey = apiKey,
            MaximumAudioBytes = maxAudio,
            MaximumResponseBytes = maxResponse
        };

    private static AudioChunkEventArgs Chunk(int bytes = 1600, int rate = 16000, int channels = 1) =>
        new AudioChunkEventArgs(new byte[bytes], rate, channels);

    private static HttpResponseMessage Json(string json) =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static WhisperTranscriptionEngine Engine(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        WhisperTranscriptionOptions? options = null) =>
        new WhisperTranscriptionEngine(new HttpClient(new StubHttpMessageHandler(send)), options ?? Options());

    private static WhisperTranscriptionEngine OkEngine(Action? onRequest = null) =>
        Engine((_, _) => { onRequest?.Invoke(); return Task.FromResult(Json("{\"text\":\"hello\"}")); });

    [Fact]
    public void Constructor_ValidatesOptions()
    {
        var client = new HttpClient(new StubHttpMessageHandler((_, _) => throw new InvalidOperationException()));
        Assert.Throws<ArgumentException>(() => new WhisperTranscriptionEngine(client,
            new WhisperTranscriptionOptions { Endpoint = new Uri("http://example.test/x") }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WhisperTranscriptionEngine(client,
            new WhisperTranscriptionOptions { Endpoint = Endpoint, Model = new string('x', 201) }));
        Assert.Throws<ArgumentNullException>(() => new WhisperTranscriptionEngine(null!, Options()));
        Assert.Throws<ArgumentNullException>(() => new WhisperTranscriptionEngine(client, null!));
    }

    [Fact]
    public async Task Constructor_AcceptsOptedInHttpLoopback()
    {
        var options = new WhisperTranscriptionOptions
        {
            Endpoint = new Uri("http://localhost:8080/v1/audio/transcriptions"),
            AllowInsecureLoopback = true
        };
        var engine = new WhisperTranscriptionEngine(
            new HttpClient(new StubHttpMessageHandler((_, _) => Task.FromResult(Json("{\"text\":\"x\"}")))), options);
        Assert.Single(await engine.TranscribeAsync(new[] { Chunk() }));
    }

    [Fact]
    public async Task NoChunks_ReturnsEmptyWithoutRequest()
    {
        var calls = 0;
        var result = await OkEngine(() => calls++).TranscribeAsync(Array.Empty<AudioChunkEventArgs>());
        Assert.Empty(result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ZeroLengthChunks_ReturnEmptyWithoutRequest()
    {
        var calls = 0;
        var result = await OkEngine(() => calls++).TranscribeAsync(new[] { Chunk(0) });
        Assert.Empty(result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TooManyChunks_Throws()
    {
        var chunks = new AudioChunkEventArgs[4097];
        Array.Fill(chunks, Chunk(2));
        await Assert.ThrowsAsync<ArgumentException>(() => OkEngine().TranscribeAsync(chunks));
    }

    [Fact]
    public async Task ExactlyMaxChunks_IsAccepted()
    {
        var chunks = new AudioChunkEventArgs[4096];
        Array.Fill(chunks, Chunk(2));
        var result = await OkEngine().TranscribeAsync(chunks);
        Assert.Single(result);
    }

    [Fact]
    public async Task MisalignedPcm_Throws()
    {
        // 2 channels need a multiple of 4 bytes.
        await Assert.ThrowsAsync<ArgumentException>(() => OkEngine().TranscribeAsync(new[] { Chunk(6, 16000, 2) }));
        await Assert.ThrowsAsync<ArgumentException>(() => OkEngine().TranscribeAsync(new[] { Chunk(3) }));
    }

    [Theory]
    [InlineData(7999, 1)]
    [InlineData(384001, 1)]
    [InlineData(16000, 0)]
    [InlineData(16000, 9)]
    public async Task UnsupportedFormat_Throws(int rate, int channels) =>
        await Assert.ThrowsAsync<ArgumentException>(() => OkEngine().TranscribeAsync(new[] { Chunk(16, rate, channels) }));

    [Fact]
    public async Task NullChunkEntry_Throws() =>
        await Assert.ThrowsAsync<ArgumentException>(() => OkEngine().TranscribeAsync(new AudioChunkEventArgs[] { null! }));

    [Fact]
    public async Task MoreThan16FormatGroups_Throws()
    {
        var chunks = new List<AudioChunkEventArgs>();
        for (var i = 0; i < 17; i++)
            chunks.Add(Chunk(16, i % 2 == 0 ? 16000 : 44100));
        await Assert.ThrowsAsync<ArgumentException>(() => OkEngine().TranscribeAsync(chunks));
    }

    [Fact]
    public async Task Exactly16FormatGroups_IsAccepted()
    {
        var calls = 0;
        var chunks = new List<AudioChunkEventArgs>();
        for (var i = 0; i < 16; i++)
            chunks.Add(Chunk(16, i % 2 == 0 ? 16000 : 44100));
        var result = await OkEngine(() => calls++).TranscribeAsync(chunks);
        Assert.Equal(16, calls);
        Assert.Equal(16, result.Count);
    }

    [Fact]
    public async Task AudioOverMaximum_Throws()
    {
        var engine = Engine((_, _) => Task.FromResult(Json("{\"text\":\"x\"}")), Options(maxAudio: 100));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.TranscribeAsync(new[] { Chunk(60), Chunk(60) }));
    }

    [Fact]
    public async Task AudioAtMaximum_IsAccepted()
    {
        var engine = Engine((_, _) => Task.FromResult(Json("{\"text\":\"x\"}")), Options(maxAudio: 100));
        Assert.Single(await engine.TranscribeAsync(new[] { Chunk(50), Chunk(50) }));
    }

    [Fact]
    public async Task SameFormatChunks_AreMergedIntoOneRequest()
    {
        var calls = 0;
        await OkEngine(() => calls++).TranscribeAsync(new[] { Chunk(), Chunk(), Chunk() });
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DifferentFormats_ProduceTwoRequests()
    {
        var calls = 0;
        var result = await OkEngine(() => calls++).TranscribeAsync(new[] { Chunk(1600, 16000, 1), Chunk(1600, 48000, 2) });
        Assert.Equal(2, calls);
        Assert.Equal(2, result.Count);
        Assert.All(result, s => Assert.Equal("hello", s.Text));
    }

    [Fact]
    public async Task WhitespaceTranscript_IsDropped()
    {
        var engine = Engine((_, _) => Task.FromResult(Json("{\"text\":\"  \"}")));
        Assert.Empty(await engine.TranscribeAsync(new[] { Chunk() }));
    }

    [Fact]
    public async Task Request_IsMultipartPostWithBearerAndWaveBody()
    {
        HttpRequestMessage? seen = null;
        byte[]? body = null;
        string? contentType = null;
        var engine = Engine(async (request, token) =>
        {
            seen = request;
            contentType = request.Content!.Headers.ContentType!.MediaType;
            body = await request.Content.ReadAsByteArrayAsync(token);
            return Json("{\"text\":\"hi\"}");
        }, Options(apiKey: "secret-key"));

        var result = await engine.TranscribeAsync(new[] { Chunk(3200, 16000, 1) });

        Assert.Equal("hi", Assert.Single(result).Text);
        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal(Endpoint, seen.RequestUri);
        Assert.Equal("Bearer", seen.Headers.Authorization!.Scheme);
        Assert.Equal("secret-key", seen.Headers.Authorization.Parameter);
        Assert.Equal("multipart/form-data", contentType);

        var text = Encoding.Latin1.GetString(body!);
        Assert.Contains("name=file", text);
        Assert.Contains("capture.wav", text);
        Assert.Contains("name=model", text);
        Assert.Contains("whisper-1", text);
        var riff = text.IndexOf("RIFF", StringComparison.Ordinal);
        Assert.True(riff >= 0, "RIFF header not found");
        Assert.Equal("WAVE", text.Substring(riff + 8, 4));
        Assert.Equal(3200 + 44, BitConverter.ToInt32(body!, riff + 4) + 8);
    }

    [Fact]
    public async Task Request_OmitsAuthorizationWithoutApiKey()
    {
        HttpRequestMessage? seen = null;
        var engine = Engine((request, _) => { seen = request; return Task.FromResult(Json("{\"text\":\"x\"}")); });
        await engine.TranscribeAsync(new[] { Chunk() });
        Assert.Null(seen!.Headers.Authorization);
    }

    [Fact]
    public async Task ErrorStatus_Throws()
    {
        var engine = Engine((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        await Assert.ThrowsAsync<HttpRequestException>(() => engine.TranscribeAsync(new[] { Chunk() }));
    }

    [Fact]
    public async Task ResponseOverLimit_ByContentLength_Throws()
    {
        var engine = Engine((_, _) => Task.FromResult(Json("{\"text\":\"" + new string('a', 200) + "\"}")),
            Options(maxResponse: 50));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.TranscribeAsync(new[] { Chunk() }));
    }

    [Fact]
    public async Task ResponseOverLimit_Streaming_Throws()
    {
        var payload = Encoding.UTF8.GetBytes("{\"text\":\"" + new string('a', 200) + "\"}");
        var engine = Engine((_, _) =>
        {
            var content = new StreamContent(new NonLengthStream(payload));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }, Options(maxResponse: 50));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.TranscribeAsync(new[] { Chunk() }));
    }

    [Fact]
    public async Task MalformedJson_ThrowsJsonException()
    {
        var engine = Engine((_, _) => Task.FromResult(Json("{not json")));
        await Assert.ThrowsAnyAsync<JsonException>(() => engine.TranscribeAsync(new[] { Chunk() }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":5}")]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"other\":\"x\"}")]
    public async Task MissingOrNonStringText_ThrowsInvalidData(string json)
    {
        var engine = Engine((_, _) => Task.FromResult(Json(json)));
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.TranscribeAsync(new[] { Chunk() }));
    }

    [Fact]
    public async Task AlreadyCancelledToken_ThrowsWithoutRequest()
    {
        var calls = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => OkEngine(() => calls++).TranscribeAsync(new[] { Chunk() }, cts.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task NullChunks_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => OkEngine().TranscribeAsync(null!));

    /// <summary>Non-seekable stream, so the response has no Content-Length.</summary>
    private sealed class NonLengthStream : Stream
    {
        private readonly MemoryStream inner;
        public NonLengthStream(byte[] data) => inner = new MemoryStream(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
