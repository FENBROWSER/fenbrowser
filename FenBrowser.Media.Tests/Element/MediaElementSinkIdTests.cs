using FenBrowser.Media.Audio;
using FenBrowser.Media.Element;

namespace FenBrowser.Media.Tests.Element;

/// <summary>
/// Audio Output Devices API on the element: which endpoint its audio goes to, and what a
/// page gets back when it names one that is not there.
/// </summary>
public class MediaElementSinkIdTests
{
    /// <summary>A backend with two endpoints and no real devices behind them.</summary>
    private sealed class FakeOutputs : IAudioOutputFactory
    {
        public IAudioOutput Create() => new NullAudioOutput();

        public IReadOnlyList<AudioOutputDevice> Devices { get; } =
        [
            new("speakers", "Speakers", true),
            new("headphones", "Headphones", false),
        ];

        public IAudioOutput? Create(string deviceId) =>
            deviceId.Length == 0 || Devices.Any(d => d.DeviceId == deviceId) ? Create() : null;
    }

    [Fact]
    public void AnElementFollowsTheDefaultOutputUntilAPageChoosesOne()
    {
        var (_, element, _, _) = MediaElementTestKit.Create(audioOutputs: new FakeOutputs());
        Assert.Equal(string.Empty, element.SinkId);
    }

    [Fact]
    public void ChoosingAnEndpointThePlatformOffersMovesTheElementsAudio()
    {
        var outputs = new FakeOutputs();
        var (host, element, _, _) = MediaElementTestKit.Create(h => h.SrcAttribute = "clip.webm", outputs);
        element.OnSrcAttributeSet();
        host.Run();

        Assert.Equal(SinkIdOutcome.Applied, element.TrySetSinkId("headphones"));
        Assert.Equal("headphones", element.SinkId);
        Assert.Equal(["headphones"], host.Resource!.Sinks);

        // Asking for the endpoint it is already on is not a change, and the resource is not
        // told to move again.
        Assert.Equal(SinkIdOutcome.Unchanged, element.TrySetSinkId("headphones"));
        Assert.Equal(["headphones"], host.Resource.Sinks);

        // The empty identifier goes back to the system default.
        Assert.Equal(SinkIdOutcome.Applied, element.TrySetSinkId(string.Empty));
        Assert.Equal(string.Empty, element.SinkId);
        Assert.Equal(["headphones", ""], host.Resource.Sinks);
    }

    [Fact]
    public void AnEndpointThePlatformDoesNotHaveLeavesTheElementWhereItWas()
    {
        var (_, element, _, _) = MediaElementTestKit.Create(audioOutputs: new FakeOutputs());
        Assert.Equal(SinkIdOutcome.Applied, element.TrySetSinkId("speakers"));
        Assert.Equal(SinkIdOutcome.NotFound, element.TrySetSinkId("nonexistent_device_id"));
        Assert.Equal("speakers", element.SinkId);
    }

    [Fact]
    public void TheDefaultOutputIsAlwaysAvailableEvenWithoutABackend()
    {
        // No audio backend at all (a headless realm): the default sink still resolves,
        // because it is whatever the system plays through, and nothing else does.
        var (_, element, _, _) = MediaElementTestKit.Create();
        Assert.Equal(SinkIdOutcome.Unchanged, element.TrySetSinkId(string.Empty));
        Assert.Equal(SinkIdOutcome.NotFound, element.TrySetSinkId("speakers"));
    }

    [Fact]
    public void AnEndpointChosenBeforeThereWasAResourceStillReachesIt()
    {
        var (host, element, _, _) = MediaElementTestKit.Create(h => h.SrcAttribute = "clip.webm", new FakeOutputs());
        Assert.Equal(SinkIdOutcome.Applied, element.TrySetSinkId("headphones"));

        element.OnSrcAttributeSet();
        host.Run();
        Assert.Equal(["headphones"], host.Resource!.Sinks);
    }
}
