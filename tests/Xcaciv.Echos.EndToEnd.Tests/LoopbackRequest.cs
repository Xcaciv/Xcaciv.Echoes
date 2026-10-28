namespace Xcaciv.Echos.EndToEnd.Tests;

internal sealed record LoopbackRequest(string Method, string Path, bool HasAuthorization, string Body)
{
    // The body may contain captured desktop content, so it is never printed.
    public override string ToString() => Method + " " + Path + " (" + Body.Length + " bytes)";
}
