namespace Xcaciv.Echos.Core;

/// <summary>
/// Opt-in observation switches. State changes are made under a private lock and <see cref="Changed"/> is raised
/// after that lock is released. Start authorized operations with <see cref="TryStart"/>.
/// </summary>
public sealed class CaptureConsent
{
    private readonly object _gate = new();
    private bool _observation;
    private bool _screen;
    private bool _audio;

    /// <summary>Raised after state has been updated and the internal lock released.</summary>
    public event EventHandler? Changed;

    public bool IsObservationEnabled
    {
        get { lock (_gate) return _observation; }
        set { if (Update(ref _observation, value)) OnChanged(); }
    }

    public bool IsScreenEnabled
    {
        get { lock (_gate) return _screen; }
        set { if (Update(ref _screen, value)) OnChanged(); }
    }

    public bool IsAudioEnabled
    {
        get { lock (_gate) return _audio; }
        set { if (Update(ref _audio, value)) OnChanged(); }
    }

    /// <summary>Reads all flags consistently in one step.</summary>
    public (bool Observation, bool Screen, bool Audio) GetFlags()
    {
        lock (_gate) return (_observation, _screen, _audio);
    }

    /// <summary>
    /// Atomically verifies consent (observation, plus screen/audio when requested) and invokes
    /// <paramref name="start"/> while holding the private lock, so no revocation can slip between check and start.
    /// <paramref name="start"/> must return quickly and must not block on other threads.
    /// Returns false, without invoking <paramref name="start"/>, when not authorized.
    /// </summary>
    public bool TryStart(bool needScreen, bool needAudio, Action start)
    {
        ArgumentNullException.ThrowIfNull(start);
        lock (_gate)
        {
            if (!_observation || needScreen && !_screen || needAudio && !_audio)
                return false;
            start();
            return true;
        }
    }

    private bool Update(ref bool field, bool value)
    {
        lock (_gate)
        {
            if (field == value) return false;
            field = value;
            return true;
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
