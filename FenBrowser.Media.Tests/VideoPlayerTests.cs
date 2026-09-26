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
    /// <summary>
    /// The background policy of design section 5: with nothing showing the pictures, a
    /// resource that also has audio stops decoding video and the audio still runs the
    /// element to its end.
    /// </summary>
    [Fact]
    public async Task VideoWithAudio_HiddenStopsDecodingPicturesAndPlaysTheAudioOut()
    {
        var client = new RecordingClient();
        var player = Create("pattern_vp8_vorbis.webm", client, NullAudioOutputFactory.Realtime);
        player.UpdateVideoVisibility(visible: false);
        player.Start();
        try
        {
            await client.WaitForAsync(() => client.ReadyState >= MediaReadyState.HaveFutureData);
            player.UpdatePlayback(potentiallyPlaying: true, playbackRate: 1.0, preservesPitch: true, effectiveVolume: 1.0);
            await client.WaitForAsync(() => client.Position > MediaTime.FromSeconds(0.3), timeoutMs: 4000);
            long presented = player.Presenter.Sequence;
            long decoded = player.GetVideoPlaybackQuality()!.Value.TotalVideoFrames;

            await client.WaitForAsync(() => client.Ended, timeoutMs: 5000);
            Assert.Equal(1.0, client.Position.TotalSeconds, 1);
            Assert.Equal(presented, player.Presenter.Sequence);
            Assert.Equal(decoded, player.GetVideoPlaybackQuality()!.Value.TotalVideoFrames);
            Assert.True(decoded < 10, $"{decoded} of the 10 pictures were decoded while hidden");
            Assert.Null(client.Failure);
        }
        finally
        {
            player.Dispose();
        }
    }

    /// <summary>Coming back into view resumes the pictures at the clock, not where they stopped.</summary>
    [Fact]
    public async Task VideoWithAudio_ShownAgainCatchesThePicturesUpToTheClock()
    {
        var client = new RecordingClient();
        var player = Create("pattern_vp8_vorbis.webm", client, NullAudioOutputFactory.Realtime);
        player.UpdateVideoVisibility(visible: false);
        player.Start();
        try
        {
            await client.WaitForAsync(() => client.ReadyState >= MediaReadyState.HaveFutureData);
            player.UpdatePlayback(potentiallyPlaying: true, playbackRate: 1.0, preservesPitch: true, effectiveVolume: 1.0);
            await client.WaitForAsync(() => client.Position > MediaTime.FromSeconds(0.3), timeoutMs: 4000);

            long presented = player.Presenter.Sequence;
            player.UpdateVideoVisibility(visible: true);
            await client.WaitForAsync(() => player.Presenter.Sequence > presented, timeoutMs: 4000);
            var picture = player.Presenter.Acquire()!;
            var shown = picture.Timestamp;
            picture.Release();
            Assert.True(shown >= MediaTime.FromSeconds(0.2), $"the picture shown was {shown}, so decoding restarted at the beginning");
            Assert.Null(client.Failure);
        }
        finally
        {
            player.Dispose();
        }
    }

    /// <summary>
    /// A resource with no audio keeps decoding while hidden: its pictures are what carry
    /// the clock to the end of playback.
    /// </summary>
    [Fact]
    public async Task VideoOnly_KeepsDecodingWhileHidden()
    {
        var client = new RecordingClient();
        var player = Create("pattern_vp9.webm", client, NullAudioOutputFactory.Realtime);
        player.UpdateVideoVisibility(visible: false);
        player.Start();
        try
        {
            await client.WaitForAsync(() => client.ReadyState == MediaReadyState.HaveEnoughData);
            player.UpdatePlayback(potentiallyPlaying: true, playbackRate: 1.0, preservesPitch: true, effectiveVolume: 1.0);
            await client.WaitForAsync(() => client.Ended, timeoutMs: 5000);
            Assert.Equal(10, player.GetVideoPlaybackQuality()!.Value.TotalVideoFrames);
            Assert.Null(client.Failure);
        }
        finally
        {
            player.Dispose();
        }
    }
}

