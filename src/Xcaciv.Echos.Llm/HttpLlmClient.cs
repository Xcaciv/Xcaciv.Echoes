using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.Llm;

/// <summary>
/// Sends the prompt to a chat-completions endpoint with an optional bearer token.
/// The caller owns HttpClient and should disable automatic redirects on its handler;
/// redirected responses are rejected here regardless. Timeouts come from <see cref="LlmOptions.RequestTimeout"/>.
/// </summary>
public sealed class HttpLlmClient : ILlmClient
{
    private readonly TimeSpan _requestTimeout;
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly int _maximumResponseBytes;

    public HttpLlmClient(HttpClient client, LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _client = client;
        _endpoint = options.Endpoint!;
        _model = options.Model;
        _apiKey = options.ApiKey;
        _maximumResponseBytes = options.MaximumResponseBytes;
        _requestTimeout = options.RequestTimeout;
    }

    public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Prompt);
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_requestTimeout);
        var token = timeout.Token;
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        if (!string.IsNullOrWhiteSpace(_apiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        httpRequest.Content = JsonContent.Create(new
        {
            model = _model,
            messages = new[] { new { role = "user", content = request.Prompt } }
        });
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead,
            token).ConfigureAwait(false);
        var responseUri = response.RequestMessage?.RequestUri;
        if (((int)response.StatusCode >= 300 && (int)response.StatusCode < 400) ||
            (responseUri is not null && responseUri != _endpoint))
            throw new InvalidOperationException("The LLM endpoint redirected the request.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > _maximumResponseBytes)
            throw new InvalidDataException("The LLM response exceeds the configured limit.");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            if (body.Length + count > _maximumResponseBytes)
                throw new InvalidDataException("The LLM response exceeds the configured limit.");
            body.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return new LlmResult(string.Empty);
        var choice = choices[0];
        if (choice.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            return new LlmResult(content.GetString() ?? string.Empty);
        if (choice.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            return new LlmResult(text.GetString() ?? string.Empty);
        return new LlmResult(string.Empty);
    }
}
