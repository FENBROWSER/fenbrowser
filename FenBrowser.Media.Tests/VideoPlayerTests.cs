using System.Collections.Concurrent;
using FenBrowser.Media.Audio;
using FenBrowser.Media.Codecs.Ffmpeg;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Element;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The pipeline end to end with pictures: WebM bytes → Matroska demuxer → libavcodec →
/// video renderer → presenter, on the monotonic clock without audio and on the audio
/// master clock with it, reporting to a fake element.
/// </summary>
public class VideoPlayerTests
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
        public void BufferedChanged(MediaTimeRanges buffered) { }
        public void SeekableChanged(MediaTimeRanges seekable) { }
        public void Progress() { }
        public void Suspended() { }
        public void Resumed() { }
        public void Stalled() { }
        public void FetchedEntirely() { }

        public async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("Condition not met. Failure: " + Failure + ". Events: " + string.Join(",", Events));
                await Task.Delay(10);
            }
        }
    }

    private static MediaPlayer Create(string fixture, RecordingClient client, IAudioOutputFactory outputs)
    {
        var demuxers = new DemuxerRegistry();
        var decoders = new DecoderRegistry();
        MediaFormats.RegisterBuiltIn(demuxers, decoders);
        Assert.True(FfmpegDecoders.TryRegister(decoders, NullMediaLogSink.Instance), "libavcodec must be available on the development machine");
        var services = new MediaPlayerServices(demuxers, decoders, outputs, TimeProvider.System);
        return new MediaPlayer(new MemoryByteSource(MediaFixtures.Read(fixture)), null, client, action => action(), services, MediaPipelineContext.ForTests());
    }

    [Fact]
    public async Task VideoOnly_ShowsTheFirstFrameWhilePausedThenPlaysToTheEnd()
    {
        var client = new RecordingClient();
        var player = Create("pattern_vp9.webm", client, NullAudioOutputFactory.Realtime);
        int notified = 0;
        player.Presenter.FrameAvailable += () => Interlocked.Increment(ref notified);
        player.Start();
        try
        {
            await client.WaitForAsync(() => client.ReadyState == MediaReadyState.HaveEnoughData);
            Assert.NotNull(client.Metadata);
            Assert.Equal(64, client.Metadata.VideoWidth);
            Assert.Equal(48, client.Metadata.VideoHeight);
            Assert.Equal(1.0, client.Metadata.Duration.TotalSeconds, 3);

            // Paused: the first frame stands in for the poster (§4.8.9).
            await client.WaitForAsync(() => player.Presenter.Sequence >= 1);
            var first = player.Presenter.Acquire()!;
            Assert.Equal(64, first.Width);
            Assert.Equal(48, first.Height);
            Assert.Equal(MediaTime.Zero, first.Timestamp);
            var bgra = new byte[64 * 4 * 48];
            first.WriteBgra(bgra, 64 * 4);
            Assert.Contains(bgra, b => b != 0);
            first.Release();
            Assert.Equal(1, Volatile.Read(ref notified));

            player.UpdatePlayback(potentiallyPlaying: true, playbackRate: 1.0, preservesPitch: true, effectiveVolume: 1.0);
            await client.WaitForAsync(() => client.Position > MediaTime.FromSeconds(0.3), timeoutMs: 3000);
            Assert.True(player.Presenter.Sequence >= 3, "frames advanced with the clock");

            await client.WaitForAsync(() => client.Ended, timeoutMs: 4000);
            Assert.Equal(1.0, client.Position.TotalSeconds, 2);
            var quality = player.GetVideoPlaybackQuality();
            Assert.NotNull(quality);
            Assert.Equal(10, quality.Value.TotalVideoFrames);
            Assert.InRange(quality.Value.DroppedVideoFrames, 0, 2);
            Assert.Equal(10 - quality.Value.DroppedVideoFrames, player.Presenter.Sequence);
            Assert.Null(client.Failure);
        }
        finally
        {
            player.Dispose();
        }
    }

    [Fact]
    public async Task VideoOnly_SeekShowsTheFrameThatContainsTheTarget()
    {
        var client = new RecordingClient();
        var player = Create("pattern_vp9.webm", client, NullAudioOutputFactory.Realtime);
        player.Start();
        try
        {
            await client.WaitForAsync(() => client.ReadyState == MediaReadyState.HaveEnoughData);
            await client.WaitForAsync(() => player.Presenter.Sequence >= 1);

            player.Seek(MediaTime.FromSeconds(0.55), approximateForSpeed: false);
            await client.WaitForAsync(() => client.SeekLanded is not null);
            Assert.Equal(MediaTime.FromSeconds(0.55), client.SeekLanded);
            Assert.Equal(0.55, player.Clock!.CurrentTime.TotalSeconds, 3);

            await client.WaitForAsync(() => player.Presenter.Sequence >= 2);
            var picture = player.Presenter.Acquire()!;
            Assert.Equal(MediaTime.FromSeconds(0.5), picture.Timestamp);
            picture.Release();
        }
        finally
        {
            player.Dispose();
        }
    }

    [Fact]
    public async Task VideoWithAudio_FollowsTheAudioClockToTheEnd()
    {
        var client = new RecordingClient();
        var player = Create("pattern_vp8_vorbis.webm", client, NullAudioOutputFactory.Realtime);
        player.Start();
        try
        {
            await client.WaitForAsync(() => client.ReadyState >= MediaReadyState.HaveFutureData);
            Assert.Equal(2, client.Metadata!.Tracks.Count);
            Assert.Equal(64, client.Metadata.VideoWidth);
            Assert.IsType<Clock.AudioMasterClock>(player.Clock);

            player.UpdatePlayback(potentiallyPlaying: true, playbackRate: 1.0, preservesPitch: true, effectiveVolume: 1.0);
            await client.WaitForAsync(() => client.Ended, timeoutMs: 5000);
            Assert.Equal(1.0, client.Position.TotalSeconds, 1);
            Assert.True(player.Presenter.Sequence >= 5, $"only {player.Presenter.Sequence} pictures were presented");
            Assert.Equal(10, player.GetVideoPlaybackQuality()!.Value.TotalVideoFrames);
            Assert.Null(client.Failure);
        }
        finally
        {
            player.Dispose();
        }
    }
}
