using System.Text.Json;

namespace Xcaciv.Echos.EndToEnd.Tests;

internal static class PromptSections
{
    public static readonly string OcrHeader = "Screen OCR (untrusted):" + Environment.NewLine;
    public static readonly string AudioHeader = "Audio transcript (untrusted):" + Environment.NewLine;

    /// <summary>Parses a chat-completions request body and returns the user prompt. Messages never include the content.</summary>
    public static string ReadPrompt(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        var root = document.RootElement;
        Assert.True(root.GetProperty("model").GetString() == "e2e-model", "The model name is wrong.");
        var message = root.GetProperty("messages")[0];
        Assert.True(message.GetProperty("role").GetString() == "user", "The message role is wrong.");
        return message.GetProperty("content").GetString() ?? string.Empty;
    }

    public static string OcrSection(string prompt)
    {
        var start = prompt.IndexOf(OcrHeader, StringComparison.Ordinal);
        var end = prompt.IndexOf(AudioHeader, StringComparison.Ordinal);
        Assert.True(start >= 0 && end >= start + OcrHeader.Length, "The prompt sections are missing.");
        return prompt.Substring(start + OcrHeader.Length, end - start - OcrHeader.Length);
    }
}
