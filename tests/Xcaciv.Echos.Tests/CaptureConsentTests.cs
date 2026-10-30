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

    [Fact]
    public void ChangedIsRaisedWithoutTheConsentLockHeld()
    {
        var consent = new CaptureConsent();
        var otherThreadFinished = false;
        consent.Changed += (_, _) =>
        {
            // Deliberate bounded block inside the handler: it proves Changed is raised without the lock held.
#pragma warning disable xUnit1031
            if (consent.IsObservationEnabled && !consent.IsScreenEnabled)
                otherThreadFinished = Task.Run(() => consent.IsScreenEnabled = true).Wait(TimeSpan.FromSeconds(5));
#pragma warning restore xUnit1031
        };
        consent.IsObservationEnabled = true;
        Assert.True(otherThreadFinished);
        Assert.True(consent.IsScreenEnabled);
    }

    [Fact]
    public void TryStartRunsOnlyWhenAuthorized()
    {
        var consent = new CaptureConsent();
        var starts = 0;
        Assert.False(consent.TryStart(false, false, () => starts++));
        consent.IsObservationEnabled = true;
        Assert.True(consent.TryStart(false, false, () => starts++));
        Assert.False(consent.TryStart(true, false, () => starts++));
        Assert.False(consent.TryStart(false, true, () => starts++));
        consent.IsScreenEnabled = true;
        consent.IsAudioEnabled = true;
        Assert.True(consent.TryStart(true, true, () => starts++));
        Assert.Equal(2, starts);
        Assert.Equal((true, true, true), consent.GetFlags());
    }
}
