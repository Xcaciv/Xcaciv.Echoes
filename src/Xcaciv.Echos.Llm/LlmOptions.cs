namespace Xcaciv.Echos.Llm;

public sealed class LlmOptions
{
    public Uri? Endpoint { get; set; }
    public string Model { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public int MaximumResponseBytes { get; set; } = 1024 * 1024;

    public void Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri ||
            (Endpoint.Scheme != Uri.UriSchemeHttps &&
             !(Endpoint.Scheme == Uri.UriSchemeHttp && Endpoint.IsLoopback)) ||
            !string.IsNullOrEmpty(Endpoint.UserInfo) || !string.IsNullOrEmpty(Endpoint.Fragment))
        {
            throw new ArgumentException("An HTTPS endpoint (or loopback HTTP endpoint) is required.", nameof(Endpoint));
        }
        if (string.IsNullOrWhiteSpace(Model))
            throw new ArgumentException("A model name is required.", nameof(Model));
        if (ApiKey?.Contains('\r') == true || ApiKey?.Contains('\n') == true)
            throw new ArgumentException("The API key must not contain line breaks.", nameof(ApiKey));
        if (MaximumResponseBytes <= 0 || MaximumResponseBytes > 10 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
    }
}
