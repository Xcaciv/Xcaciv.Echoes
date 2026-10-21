using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.Tests;

public sealed class CaptureConsentTests
{
    [Fact]
    public void DefaultsAreAllDisabled()
    {
        var consent = new CaptureConsent();
        Assert.False(consent.IsObservationEnabled);
        Assert.False(consent.IsScreenEnabled);
        Assert.False(consent.IsAudioEnabled);
    }

    [Fact]
    public void ChangedFiresOnlyWhenValueChanges()
    {
        var consent = new CaptureConsent();
        var changes = 0;
        consent.Changed += (_, _) => changes++;
        consent.IsObservationEnabled = true;
        consent.IsObservationEnabled = true;
        consent.IsScreenEnabled = true;
        consent.IsAudioEnabled = true;
        consent.IsObservationEnabled = false;
        Assert.Equal(4, changes);
    }
}
