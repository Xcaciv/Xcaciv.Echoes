namespace Xcaciv.Echos.Core;

/// <summary>Opt-in observation switches. Lock this instance when starting an authorized operation.</summary>
public sealed class CaptureConsent
{
    private bool _observation;
    private bool _screen;
    private bool _audio;

    public event EventHandler? Changed;

    public bool IsObservationEnabled
    {
        get { lock (this) return _observation; }
        set { lock (this) { if (_observation == value) return; _observation = value; Changed?.Invoke(this, EventArgs.Empty); } }
    }

    public bool IsScreenEnabled
    {
        get { lock (this) return _screen; }
        set { lock (this) { if (_screen == value) return; _screen = value; Changed?.Invoke(this, EventArgs.Empty); } }
    }

    public bool IsAudioEnabled
    {
        get { lock (this) return _audio; }
        set { lock (this) { if (_audio == value) return; _audio = value; Changed?.Invoke(this, EventArgs.Empty); } }
    }
}
