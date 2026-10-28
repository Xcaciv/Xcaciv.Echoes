namespace Xcaciv.Echos.Capture;

public sealed class WhisperTranscriptionOptions
{
    /// <summary>The complete OpenAI-compatible transcription URL, including /v1/audio/transcriptions.</summary>
    public Uri? Endpoint { get; init; }
    public string Model { get; init; } = "whisper-1";
    public string? ApiKey { get; init; }
    public int MaximumAudioBytes { get; init; } = 20 * 1024 * 1024;
    public int MaximumResponseBytes { get; init; } = 1024 * 1024;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public bool AllowInsecureLoopback { get; init; }

    public void Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri ||
            !(Endpoint.Scheme == Uri.UriSchemeHttps ||
              AllowInsecureLoopback && Endpoint.IsLoopback && Endpoint.Scheme == Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(Endpoint.UserInfo) || !string.IsNullOrEmpty(Endpoint.Fragment))
            throw new ArgumentException("An explicit HTTPS endpoint (or opted-in HTTP loopback endpoint) is required.", nameof(Endpoint));
        if (ApiKey?.Contains('\r') == true || ApiKey?.Contains('\n') == true)
            throw new ArgumentException("The API key must not contain line breaks.", nameof(ApiKey));
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 200)
            throw new ArgumentOutOfRangeException(nameof(Model));
        if (MaximumAudioBytes <= 0 || MaximumAudioBytes > 100 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumAudioBytes));
        if (MaximumResponseBytes <= 0 || MaximumResponseBytes > 10 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumResponseBytes));
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
    }
}
