using Xcaciv.Echos.Llm;

namespace Xcaciv.Echos.Tests;

public sealed class LlmOptionsTests
{
    [Theory]
    [InlineData("https://example.invalid/v1/chat/completions")]
    [InlineData("http://localhost:8080/v1/chat/completions")]
    [InlineData("http://127.0.0.1:8080/v1/chat/completions")]
    [InlineData("http://[::1]:8080/v1/chat/completions")]
    public void AcceptsSecureOrLoopbackEndpoint(string endpoint)
    {
        new LlmOptions { Endpoint = new Uri(endpoint), Model = "test" }.Validate();
    }

    [Theory]
    [InlineData("http://example.invalid/v1/chat/completions")]
    [InlineData("ftp://localhost/file")]
    [InlineData("https://user@example.invalid/")]
    [InlineData("https://example.invalid/#fragment")]
    public void RejectsUnsafeEndpoint(string endpoint)
    {
        Assert.Throws<ArgumentException>(() =>
            new LlmOptions { Endpoint = new Uri(endpoint), Model = "test" }.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(601)]
    public void RejectsInvalidRequestTimeout(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmOptions
        {
            Endpoint = new Uri("https://example.invalid/"),
            Model = "test",
            RequestTimeout = TimeSpan.FromSeconds(seconds)
        }.Validate());
    }

    [Fact]
    public void AcceptsDefaultAndMaximumRequestTimeout()
    {
        var options = new LlmOptions { Endpoint = new Uri("https://example.invalid/"), Model = "test" };
        Assert.Equal(TimeSpan.FromSeconds(45), options.RequestTimeout);
        options.Validate();
        options.RequestTimeout = TimeSpan.FromMinutes(10);
        options.Validate();
    }

    [Fact]
    public void RequiresExplicitEndpointAndModel()
    {
        Assert.Throws<ArgumentException>(() => new LlmOptions().Validate());
        Assert.Throws<ArgumentException>(() =>
            new LlmOptions { Endpoint = new Uri("https://example.invalid/") }.Validate());
    }
}
