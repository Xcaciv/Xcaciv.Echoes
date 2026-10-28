using System.Net;
using System.Text.Json;
using Xcaciv.Echos.Llm;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.Tests;

public sealed class HttpLlmClientTests
{
    [Theory]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"hello\"}}]}", "hello")]
    [InlineData("{\"choices\":[{\"text\":\"legacy\"}]}", "legacy")]
    [InlineData("{\"choices\":[]}", "")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":null}}]}", "")]
    public async Task ReadsCompletionAndDisposesResponse(string json, string expected)
    {
        var body = new TrackingHttpContent(json);
        using var http = new HttpClient(new StubHttpMessageHandler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("test-model", payload.RootElement.GetProperty("model").GetString());
            Assert.Equal("question", payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
        }));
        var client = Create(http);
        Assert.Equal(expected, (await client.CompleteAsync(new LlmRequest("question"))).Text);
        Assert.True(body.IsDisposed);
    }

    [Fact]
    public async Task FailureDisposesResponseAndThrows()
    {
        var body = new TrackingHttpContent("failure");
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = body })));
        await Assert.ThrowsAsync<HttpRequestException>(() => Create(http).CompleteAsync(new LlmRequest("question")));
        Assert.True(body.IsDisposed);
    }

    [Fact]
    public async Task ForwardsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new StubHttpMessageHandler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(http).CompleteAsync(new LlmRequest("question"), cancellation.Token));
    }

    [Fact]
    public async Task TimeoutCoversStalledResponseBodyAndDisposesIt()
    {
        var stream = new CancellationBlockingStream();
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(http, TimeSpan.FromMilliseconds(100)).CompleteAsync(new LlmRequest("question")).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task RejectsOversizedResponseAndDisposesIt()
    {
        var body = new TrackingHttpContent(new string('x', 1024 * 1024 + 1));
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body })));
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(http).CompleteAsync(new LlmRequest("question")));
        Assert.True(body.IsDisposed);
    }

    [Fact]
    public async Task BoundsStreamingResponseWithoutContentLength()
    {
        var stream = new NonSeekableMemoryStream(new byte[1024 * 1024 + 1]);
        var body = new StreamContent(stream);
        Assert.Null(body.Headers.ContentLength);
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body })));
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(http).CompleteAsync(new LlmRequest("question")));
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task RejectsRedirectStatusBeforeReadingBody()
    {
        var body = new TrackingHttpContent("{}");
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Content = body })));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(http).CompleteAsync(new LlmRequest("question")));
        Assert.Equal("The LLM endpoint redirected the request.", ex.Message);
        Assert.True(body.IsDisposed);
    }

    [Fact]
    public async Task RejectsResponseFromDifferentUri()
    {
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[]}"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://other.invalid/v1/chat/completions")
            })));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(http).CompleteAsync(new LlmRequest("question")));
        Assert.Equal("The LLM endpoint redirected the request.", ex.Message);
    }

    private static HttpLlmClient Create(HttpClient http, TimeSpan? timeout = null) => new(http, new LlmOptions
    {
        RequestTimeout = timeout ?? TimeSpan.FromSeconds(45),
        Endpoint = new Uri("https://example.invalid/v1/chat/completions"),
        Model = "test-model",
        ApiKey = "test-placeholder"
    });

}
