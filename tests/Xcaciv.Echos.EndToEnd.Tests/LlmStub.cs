namespace Xcaciv.Echos.EndToEnd.Tests;

internal static class LlmStub
{
    public const string SuggestionText = "E2E suggestion";

    public const string ResponseJson = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"" + SuggestionText + "\"}}]}";
}
