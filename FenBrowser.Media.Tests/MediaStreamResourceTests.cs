using System.Collections.Concurrent;
using FenBrowser.Media.Audio;
using FenBrowser.Media.Element;
using FenBrowser.Media.Streams;
using FenBrowser.Media.WebAudio;

namespace FenBrowser.Media.Tests;

/// <summary>A MediaStream as a media element's resource (mediacapture-main 6).</summary>
public class MediaStreamResourceTests
{
    private sealed class RecordingClient : IMediaResourceClient
    {
        public ConcurrentQueue<string> Events { get; } = new();
        public MediaResourceMetadata? Metadata { get; private set; }
        public MediaReadyState ReadyState { get; private set; }

        public void Failed(MediaResourceFailure failure, string message) => Events.Enqueue("failed");
        public void MetadataAvailable(MediaResourceMetadata metadata) { Metadata = metadata; Events.Enqueue("metadata"); }
        public void ReadyStateChanged(MediaReadyState state) { ReadyState = state; Events.Enqueue("ready:" + (int)state); }
        public void DurationChanged(MediaTime duration) { }
        public void VideoSizeChanged(int width, int height) { }
        public void PositionChanged(MediaTime position, bool monotonic) { }
        public void ReachedEnd() => Events.Enqueue("ended");
        public void SeekCompleted(MediaTime position) => Events.Enqueue("seeked");
        public void BufferedChanged(MediaTimeRanges buffered) { }
        public void SeekableChanged(MediaTimeRanges seekable) => Events.Enqueue("seekable:" + seekable.Count);
        public void Progress() { }
        public void Suspended() { }
        public void Resumed() { }
        public void Stalled() { }
        public void FetchedEntirely() { }
    }

    private static AudioTrackPipe PipeWithConstant(float value, int sampleRate, int frames)
    {
        var pipe = new AudioTrackPipe(sampleRate, 1);
        var block = new AudioBus(1, 128);
        block.Reset(1);
        block.Channel(0).Fill(value);
        block.MarkNotSilent();
        for (int written = 0; written < frames; written += 128)
            pipe.Write(block);
        return pipe;
    }

    private static MediaStreamResource Create(LiveStreamSource source, RecordingClient client) =>
        new(source, client, action => action(), new NullAudioOutputFactory(realtime: false));

    [Fact]
    public void AnActiveStreamIsReadyAtOnceWithNoDurationAndNothingSeekable()
    {
        var source = new LiveStreamSource();
        source.Update([new LiveTrack("a", MediaTrackKind.Audio, PipeWithConstant(0.5f, 48000, 4096), Live: true, Enabled: true)]);
        var client = new RecordingClient();

        using var resource = Create(source, client);

        Assert.True(resource.IsProviderObject);
        Assert.True(client.Metadata!.Duration.IsInfinite);
        Assert.Single(client.Metadata.Tracks);
        Assert.Equal(MediaReadyState.HaveEnoughData, client.ReadyState);
        Assert.Contains("seekable:0", client.Events);
    }

    [Fact]
    public void AnEmptyStreamWaitsUntilItGainsALiveTrack()
    {
        var source = new LiveStreamSource();
        var client = new RecordingClient();
        using var resource = Create(source, client);
        Assert.Empty(client.Events);

        source.Update([new LiveTrack("a", MediaTrackKind.Audio, null, Live: true, Enabled: true)]);

        Assert.Equal(MediaReadyState.HaveEnoughData, client.ReadyState);
    }

    [Fact]
    public void GoingInactiveEndsPlaybackOnce()
    {
        var source = new LiveStreamSource();
        source.Update([new LiveTrack("a", MediaTrackKind.Audio, null, Live: true, Enabled: true)]);
        var client = new RecordingClient();
        using var resource = Create(source, client);

        source.Update([new LiveTrack("a", MediaTrackKind.Audio, null, Live: false, Enabled: true)]);
        source.Update([new LiveTrack("a", MediaTrackKind.Audio, null, Live: false, Enabled: true)]);

        Assert.Single(client.Events, e => e == "ended");
    }

    [Fact]
    public void RenderMixesLiveEnabledTracksAtTheDeviceRateWithVolume()
    {
        var source = new LiveStreamSource();
        source.Update(
        [
            // A 24 kHz track read at 48 kHz, and a disabled one that must stay silent.
            new LiveTrack("a", MediaTrackKind.Audio, PipeWithConstant(0.5f, 24000, 8192), Live: true, Enabled: true),
            new LiveTrack("b", MediaTrackKind.Audio, PipeWithConstant(0.25f, 48000, 8192), Live: true, Enabled: false),
        ]);
        var client = new RecordingClient();
        using var resource = Create(source, client);
        resource.UpdatePlayback(potentiallyPlaying: true, playbackRate: 1, preservesPitch: true, effectiveVolume: 0.5);

        var buffer = new float[512 * 2];
        int frames = resource.Render(buffer, 2);

        Assert.Equal(512, frames);
        // Mono to both channels, half volume: every sample 0.25.
        Assert.All(buffer, sample => Assert.Equal(0.25f, sample, 3));
    }

    [Fact]
    public void RenderIsSilentWhilePaused()
    {
        var source = new LiveStreamSource();
        source.Update([new LiveTrack("a", MediaTrackKind.Audio, PipeWithConstant(0.5f, 48000, 4096), Live: true, Enabled: true)]);
        using var resource = Create(source, new RecordingClient());

        var buffer = new float[256];
        resource.Render(buffer, 2);

        Assert.All(buffer, sample => Assert.Equal(0f, sample));
    }
}
