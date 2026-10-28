using Xcaciv.Echos.Capture;

namespace Xcaciv.Echos.Capture.Tests;

public sealed class WhisperTranscriptionOptionsTests
{
    private static WhisperTranscriptionOptions Valid() =>
        new WhisperTranscriptionOptions { Endpoint = new Uri("https://example.test/v1/audio/transcriptions") };

    [Fact]
    public void Validate_AcceptsHttps() => Valid().Validate();

    [Fact]
    public void Validate_AcceptsOptedInHttpLoopback()
    {
        new WhisperTranscriptionOptions
        {
            Endpoint = new Uri("http://127.0.0.1:9000/v1/audio/transcriptions"),
            AllowInsecureLoopback = true
        }.Validate();
    }

    [Fact]
    public void Validate_RejectsNullEndpoint() =>
        Assert.Throws<ArgumentException>(() => new WhisperTranscriptionOptions().Validate());

    [Fact]
    public void Validate_RejectsRelativeEndpoint() =>
        Assert.Throws<ArgumentException>(() =>
            new WhisperTranscriptionOptions { Endpoint = new Uri("/v1/x", UriKind.Relative) }.Validate());

    [Theory]
    [InlineData("http://example.test/v1/audio/transcriptions", true)]
    [InlineData("http://127.0.0.1/v1/audio/transcriptions", false)]
    [InlineData("https://user:pw@example.test/v1", false)]
    [InlineData("https://example.test/v1#frag", false)]
    [InlineData("ftp://example.test/v1", true)]
    public void Validate_RejectsUnsafeEndpoints(string endpoint, bool allowLoopback)
    {
        var options = new WhisperTranscriptionOptions
        {
            Endpoint = new Uri(endpoint),
            AllowInsecureLoopback = allowLoopback
        };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsUserInfoOnLoopbackToo()
    {
        var options = new WhisperTranscriptionOptions
        {
            Endpoint = new Uri("http://u:p@localhost/v1"),
            AllowInsecureLoopback = true
        };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsBlankModel(string model) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WhisperTranscriptionOptions { Endpoint = Valid().Endpoint, Model = model }.Validate());

    [Fact]
    public void Validate_ModelLengthBoundary()
    {
        new WhisperTranscriptionOptions { Endpoint = Valid().Endpoint, Model = new string('m', 200) }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WhisperTranscriptionOptions { Endpoint = Valid().Endpoint, Model = new string('m', 201) }.Validate());
    }

    [Theory]
    [InlineData("key\r")]
    [InlineData("ke\ny")]
    public void Validate_RejectsLineBreaksInApiKey(string key) =>
        Assert.Throws<ArgumentException>(() =>
            new WhisperTranscriptionOptions { Endpoint = Valid().Endpoint, ApiKey = key }.Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100 * 1024 * 1024 + 1)]
    public void Validate_RejectsBadMaximumAudioBytes(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WhisperTranscriptionOptions { Endpoint = Valid().Endpoint, MaximumAudioBytes = value }.Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10 * 1024 * 1024 + 1)]
    public void Validate_RejectsBadMaximumResponseBytes(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WhisperTranscriptionOptions { Endpoint = Valid().Endpoint, MaximumResponseBytes = value }.Validate());

    [Fact]
    public void Validate_RejectsBadTimeouts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WhisperTranscriptionOptions { Endpoint = Valid().Endpoint, RequestTimeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WhisperTranscriptionOptions { Endpoint = Valid().Endpoint, RequestTimeout = TimeSpan.FromMinutes(11) }.Validate());
    }
}
