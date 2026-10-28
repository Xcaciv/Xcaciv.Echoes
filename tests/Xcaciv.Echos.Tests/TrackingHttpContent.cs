using System.Text;

namespace Xcaciv.Echos.Tests;

internal sealed class TrackingHttpContent : StringContent
{
    public bool IsDisposed { get; private set; }
    public TrackingHttpContent(string text) : base(text, Encoding.UTF8, "application/json") { }
    protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
}
