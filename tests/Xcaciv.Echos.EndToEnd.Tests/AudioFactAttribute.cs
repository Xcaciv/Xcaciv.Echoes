using NAudio.CoreAudioApi;

namespace Xcaciv.Echos.EndToEnd.Tests;

public sealed class AudioFactAttribute : FactAttribute
{
    public AudioFactAttribute()
    {
        Skip = DesktopRequirements.SkipReason();
        if (Skip is not null)
            return;
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                Skip = "No default audio render endpoint exists.";
        }
        catch (Exception error)
        {
            Skip = "Audio endpoints are unavailable (" + error.GetType().Name + ").";
        }
    }
}
