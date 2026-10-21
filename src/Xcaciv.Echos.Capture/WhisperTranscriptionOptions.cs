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
}
