using FenBrowser.Media.Audio;
using FenBrowser.Media.Audio.Windows;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The audio output directory behind <c>setSinkId</c>: what the platform offers, and what
/// happens when a page names something the platform does not offer.
/// </summary>
public class AudioOutputDeviceTests
{
    [Fact]
    public void ThePlatformBackendNamesItsOutputEndpoints()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var devices = PlatformAudioOutputFactory.Instance.Devices;
        if (devices.Count == 0)
            return; // a machine with no audio hardware at all; nothing to assert about

        Assert.All(devices, device =>
        {
            Assert.NotEmpty(device.DeviceId);
            Assert.NotEmpty(device.Label);
        });

        // The system default comes first, and an endpoint is listed once.
        Assert.True(devices[0].IsDefault);
        Assert.Single(devices, d => d.IsDefault);
        Assert.Equal(devices.Count, devices.Select(d => d.DeviceId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task AnEndpointTheSystemOffersCanBeOpenedByItsIdentifier()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var factory = PlatformAudioOutputFactory.Instance;
        var devices = factory.Devices;
        if (devices.Count == 0)
            return;

        var output = factory.Create(devices[0].DeviceId);
        Assert.NotNull(output);
        await output.DisposeAsync();
    }

    [Fact]
    public void AnIdentifierNoEndpointHasIsNotOpenedAtAll()
    {
        // This is what makes setSinkId reject with NotFoundError rather than fall back to
        // the default output and pretend the page got what it asked for.
        Assert.Null(PlatformAudioOutputFactory.Instance.Create("nonexistent_device_id"));
    }

    [Fact]
    public void TheEmptyIdentifierIsTheDefaultOutput()
    {
        var output = PlatformAudioOutputFactory.Instance.Create(string.Empty);
        Assert.NotNull(output);
    }

    [Fact]
    public void TheNullSinkHasNoSeparateDevices()
    {
        IAudioOutputFactory factory = new NullAudioOutputFactory(realtime: false);
        Assert.Empty(factory.Devices);
        Assert.NotNull(factory.Create(string.Empty));
        Assert.Null(factory.Create("anything"));
    }
}
