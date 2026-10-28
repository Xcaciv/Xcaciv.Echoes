using Xcaciv.Echos.Capture;
using Xcaciv.Echos.Llm;

namespace Xcaciv.Echos.App;

public sealed class AppConfiguration
{
    public LlmOptions Llm { get; }
    public WhisperTranscriptionOptions Transcription { get; }
    public bool HasLlmConfiguration { get; }
    public bool HasTranscriptionConfiguration { get; }

    public AppConfiguration()
    {
        Llm = new LlmOptions
        {
            Endpoint = ReadEndpoint("ECHOS_LLM_ENDPOINT"),
            Model = Environment.GetEnvironmentVariable("ECHOS_LLM_MODEL") ?? string.Empty,
            ApiKey = Environment.GetEnvironmentVariable("ECHOS_LLM_API_KEY")
        };
        Transcription = new WhisperTranscriptionOptions
        {
            Endpoint = ReadEndpoint("ECHOS_TRANSCRIPTION_ENDPOINT"),
            Model = Environment.GetEnvironmentVariable("ECHOS_TRANSCRIPTION_MODEL") ?? string.Empty,
            ApiKey = Environment.GetEnvironmentVariable("ECHOS_TRANSCRIPTION_API_KEY"),
            AllowInsecureLoopback = true
        };

        try
        {
            Llm.Validate();
            HasLlmConfiguration = true;
        }
        catch (ArgumentException)
        {
            HasLlmConfiguration = false;
        }
        try
        {
            Transcription.Validate();
            HasTranscriptionConfiguration = true;
        }
        catch (ArgumentException)
        {
            HasTranscriptionConfiguration = false;
        }
    }

    public string Disclosure =>
        "When observation is on, foreground UI text, enabled screen OCR, and enabled audio transcripts are sent to "
        + Describe(Llm.Endpoint, HasLlmConfiguration)
        + ". If system audio is enabled, raw speaker/loopback audio is also sent to "
        + Describe(Transcription.Endpoint, HasTranscriptionConfiguration)
        + ". Nothing is sent while observation is off. Remote providers may retain submitted data.";

    private static string Describe(Uri? endpoint, bool configured) =>
        configured && endpoint is not null
            ? endpoint.GetLeftPart(UriPartial.Path)
            : "(not configured)";

    private static Uri? ReadEndpoint(string name)
    {
        if (!Uri.TryCreate(Environment.GetEnvironmentVariable(name), UriKind.Absolute, out var endpoint))
            return null;
        if (endpoint.Scheme != Uri.UriSchemeHttps &&
            !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
            return null;
        return string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Fragment)
            ? endpoint
            : null;
    }
}
