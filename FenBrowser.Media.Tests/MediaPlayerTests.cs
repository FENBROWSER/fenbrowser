using System.Collections.Concurrent;
using FenBrowser.Media.Audio;
using FenBrowser.Media.Clock;
using FenBrowser.Media.Element;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The audio pipeline end to end: WAV bytes → demuxer → PCM decoder → renderer → null
/// output, reporting to a fake element the way the browser's element hears it.
/// </summary>
public class MediaPlayerTests
{
    private sealed class RecordingClient : IMediaResourceClient
    {
        public ConcurrentQueue<string> Events { get; } = new();
        public MediaResourceMetadata? Metadata { get; private set; }
        public MediaReadyState ReadyState { get; private set; }
        public MediaTime Position { get; private set; }
        public MediaTime? SeekLanded { get; private set; }
        public string? Failure { get; private set; }
        public bool Ended { get; private set; }

        public void Failed(MediaResourceFailure failure, string message) { Failure = failure + ": " + message; Events.Enqueue("failed"); }
        public void MetadataAvailable(MediaResourceMetadata metadata) { Metadata = metadata; Events.Enqueue("metadata"); }
        public void ReadyStateChanged(MediaReadyState state) { ReadyState = state; Events.Enqueue("ready:" + (int)state); }
        public void DurationChanged(MediaTime duration) => Events.Enqueue("duration");
        public void VideoSizeChanged(int width, int height) => Events.Enqueue("size");
        public void PositionChanged(MediaTime position, bool monotonic) { Position = position; }
        public void ReachedEnd() { Ended = true; Events.Enqueue("ended"); }
        public void SeekCompleted(MediaTime position) { SeekLanded = position; Events.Enqueue("seeked"); }
        public void BufferedChanged(MediaTimeRanges buffered) => Events.Enqueue("buffered");
        public void SeekableChanged(MediaTimeRanges seekable) => Events.Enqueue("seekable");
        public void Progress() { }
        public void Suspended() { }
        public void Resumed() { }
        public void Stalled() { }
        public void FetchedEntirely() => Events.Enqueue("fetched");

        public async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("Condition not met. Events: " + string.Join(",", Events));
                await Task.Delay(10);
            }
        }
    }

    private static (DemuxerRegistry, DecoderRegistry) Registries()
    {
        var demuxers = new DemuxerRegistry();
        var decoders = new DecoderRegistry();
        MediaFormats.RegisterBuiltIn(demuxers, decoders);
        return (demuxers, decoders);
    }

    private static MediaPlayer Create(byte[] bytes, RecordingClient client, IAudioOutputFactory outputs, string? mime = null)
    {
        var (demuxers, decoders) = Registries();
        var services = new MediaPlayerServices(demuxers, decoders, outputs, TimeProvider.System);
        return new MediaPlayer(new MemoryByteSource(bytes), mime, client, action => action(), services, MediaPipelineContext.ForTests());
    }

    [Fact]
    public async Task Wav_LoadsReportsMetadataAndBuffersToEnoughData()
    {
        var client = new RecordingClient();
        var player = Create(MediaFixtures.Read("sine_pcm16.wav"), client, new NullAudioOutputFactory(realtime: false));
        player.Start();
        try
        {
            await client.WaitForAsync(() => client.ReadyState == MediaReadyState.HaveEnoughData);

            Assert.NotNull(client.Metadata);
            Assert.Equal(1.0, client.Metadata.Duration.TotalSeconds, 3);
            Assert.Single(client.Metadata.Tracks);
            Assert.Contains("metadata", client.Events);
            Assert.Contains("seekable", client.Events);
            Assert.Contains("buffered", client.Events);
            Assert.Contains("fetched", client.Events);
            Assert.Null(client.Failure);
            // Readiness climbed in order.
            var ready = client.Events.Where(e => e.StartsWith("ready:", StringComparison.Ordinal)).ToList();
            Assert.Equal(ready.OrderBy(r => r).ToList(), ready);
        }
        finally
        {
            player.Dispose();
        }
    }

    [Fact]
    public async Task Wav_PlaysToTheEndOnTheNullDevice()
    {
        var client = new RecordingClient();
        var player = Create(MediaFixtures.Read("sine_pcm16.wav"), client, NullAudioOutputFactory.Realtime);
        player.Start();
        try
        {
            await client.WaitForAsync(() => client.ReadyState >= MediaReadyState.HaveFutureData);
            player.UpdatePlayback(potentiallyPlaying: true, playbackRate: 1.0, preservesPitch: true, effectiveVolume: 1.0);

            await client.WaitForAsync(() => client.Position > MediaTime.FromSeconds(0.3), timeoutMs: 3000);
            Assert.False(client.Ended);

            await client.WaitForAsync(() => client.Ended, timeoutMs: 4000);
            Assert.Equal(1.0, client.Position.TotalSeconds, 2);
            Assert.Null(client.Failure);
        }
        finally
        {
            player.Dispose();
        }
    }

    [Fact]
    public async Task Seek_FlushesAndLandsOnTheTarget()
    {
        var client = new RecordingClient();
        var player = Create(MediaFixtures.Read("sine_pcm16.wav"), client, new NullAudioOutputFactory(realtime: false));
        player.Start();
        try
        {
            await client.WaitForAsync(() => client.ReadyState == MediaReadyState.HaveEnoughData);
            player.Seek(MediaTime.FromSeconds(0.75), approximateForSpeed: false);
            await client.WaitForAsync(() => client.SeekLanded is not null);

            Assert.Equal(MediaTime.FromSeconds(0.75), client.SeekLanded);
            Assert.Equal(MediaTime.FromSeconds(0.75), client.Position);
            Assert.Equal(MediaTime.FromSeconds(0.75), player.Clock!.CurrentTime);
            await client.WaitForAsync(() => client.ReadyState == MediaReadyState.HaveEnoughData);

            // Past the end clamps to the end.
            player.Seek(MediaTime.FromSeconds(5), approximateForSpeed: true);
            await client.WaitForAsync(() => client.SeekLanded == client.Metadata!.Duration);
        }
        finally
        {
            player.Dispose();
        }
    }

    [Theory]
    [InlineData("sine_mp3_noid3.mp3")]
    [InlineData("sine_opus.ogg")]
    [InlineData("sine_flac.flac")]
    public async Task Seek_IsAccurateAcrossCodecs(string file)
    {
        var client = new RecordingClient();
        var (demuxers, decoders) = Registries();
        Assert.True(Codecs.Ffmpeg.FfmpegDecoders.TryRegister(decoders, Diagnostics.NullMediaLogSink.Instance));
        var outputs = new CapturingOutputFactory();
        var services = new MediaPlayerServices(demuxers, decoders, outputs, TimeProvider.System);
        var player = new MediaPlayer(new MemoryByteSource(MediaFixtures.Read(file)), null, client, action => action(), services, MediaPipelineContext.ForTests());
        player.Start();
        try
        {
            await client.WaitForAsync(() => client.ReadyState == MediaReadyState.HaveEnoughData);
            player.Seek(MediaTime.FromSeconds(0.6), approximateForSpeed: false);
            await client.WaitForAsync(() => client.SeekLanded is not null);
            await client.WaitForAsync(() => client.ReadyState >= MediaReadyState.HaveFutureData);
            Assert.Equal(MediaTime.FromSeconds(0.6), client.SeekLanded);
            Assert.Equal(0.6, player.Clock!.CurrentTime.TotalSeconds, 3);

            // Play 100 ms on the device: the first audio started at the target, not at the
            // frame or page boundary before it, so the clock reads 0.7 s.
            player.UpdatePlayback(potentiallyPlaying: true, playbackRate: 1.0, preservesPitch: true, effectiveVolume: 1.0);
            var output = outputs.Last!;
            await client.WaitForAsync(() => output.IsRunning);
            output.Pump(output.Format.SampleRate / 10);
            Assert.Equal(0.7, player.Clock.CurrentTime.TotalSeconds, 2);
        }
        finally
        {
            player.Dispose();
        }
    }

    [Fact]
    public async Task NotMedia_FailsAsUnsupported()
    {
        var client = new RecordingClient();
        var player = Create("<html>not media</html>"u8.ToArray(), client, new NullAudioOutputFactory(realtime: false), "text/html");
        player.Start();
        await client.WaitForAsync(() => client.Failure is not null);
        Assert.StartsWith("Unsupported", client.Failure);
        player.Dispose();
    }

    [Fact]
    public async Task TruncatedHeader_FailsAsUnsupportedNotAsCrash()
    {
        var client = new RecordingClient();
        var bytes = MediaFixtures.Read("sine_pcm16.wav").AsSpan(0, 30).ToArray();
        var player = Create(bytes, client, new NullAudioOutputFactory(realtime: false));
        player.Start();
        await client.WaitForAsync(() => client.Failure is not null);
        Assert.StartsWith("Unsupported", client.Failure);
        player.Dispose();
    }

    [Fact]
    public void Renderer_ResamplesMapsChannelsAndFeedsTheClock()
    {
        var output = new NullAudioOutput(realtime: false);
        var format = new AudioStreamFormat(48000, 2);
        var clock = new AudioMasterClock(new FixedPosition(48000, 0));
        var renderer = new AudioRenderer(format, clock);

        // Mono 24 kHz source: 240 frames = 10 ms; at the 48 kHz stereo device that is 480 frames.
        var block = Buffers.AudioBlock.Allocate(MediaLimits.Default, 24000, 1, 240, MediaTime.FromSeconds(2));
        for (int i = 0; i < 240; i++)
            block.Samples[i] = 0.5f;
        renderer.Enqueue(block);
        Assert.Equal(MediaTime.FromSeconds(0.01), renderer.QueuedDuration);

        var buffer = new float[480 * 2];
        int written = renderer.Render(buffer, 2);
        Assert.Equal(480, written);
        Assert.All(buffer, s => Assert.Equal(0.5f, s, 3));
        Assert.Equal(MediaTime.Zero, renderer.QueuedDuration);

        // The clock maps the device position through the segment the render produced.
        clock = new AudioMasterClock(new FixedPosition(48000, 240));
        var renderer2 = new AudioRenderer(format, clock);
        var block2 = Buffers.AudioBlock.Allocate(MediaLimits.Default, 24000, 1, 240, MediaTime.FromSeconds(2));
        renderer2.Enqueue(block2);
        renderer2.Render(buffer, 2);
        Assert.Equal(MediaTime.FromSeconds(2.005), clock.CurrentTime);

        // Volume and mute scale the output without touching the clock.
        renderer2.Volume = 0.5;
        renderer2.Muted = true;
        var block3 = Buffers.AudioBlock.Allocate(MediaLimits.Default, 48000, 2, 10, MediaTime.FromSeconds(3));
        block3.Samples.Fill(1f);
        renderer2.Enqueue(block3);
        var small = new float[20];
        Assert.Equal(10, renderer2.Render(small, 2));
        Assert.All(small, s => Assert.Equal(0f, s));
        _ = output;
    }

    private sealed class CapturingOutputFactory : IAudioOutputFactory
    {
        public NullAudioOutput? Last { get; private set; }

        public IAudioOutput Create() => Last = new NullAudioOutput(realtime: false);
    }

    private sealed class FixedPosition(int sampleRate, long played) : Clock.IAudioPlaybackPosition
    {
        public int SampleRate => sampleRate;
        public long FramesPlayed => played;
    }
}
