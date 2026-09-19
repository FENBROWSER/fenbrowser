using System.Collections.Concurrent;
using FenBrowser.Media.Audio;
using FenBrowser.Media.Codecs.Ffmpeg;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Element;
using FenBrowser.Media.Mse;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The player over a MediaSource: metadata arrives with the first initialization segment,
/// a starved read stalls instead of ending, an append wakes the pipeline, endOfStream
/// ends it, and an append after endOfStream reopens it.
/// </summary>
public class MsePlayerTests
{
    private sealed class RecordingClient : IMediaResourceClient
    {
        public ConcurrentQueue<string> Events { get; } = new();
        public MediaResourceMetadata? Metadata { get; private set; }
        public MediaReadyState ReadyState { get; private set; }
        public MediaTime Position { get; private set; }
        public MediaTime? Duration { get; private set; }
        public MediaTimeRanges Buffered { get; private set; } = MediaTimeRanges.Empty;
        public MediaTimeRanges Seekable { get; private set; } = MediaTimeRanges.Empty;
        public string? Failure { get; private set; }
        public int EndedCount { get; private set; }
        public bool Fetched { get; private set; }

        public void Failed(MediaResourceFailure failure, string message) { Failure = failure + ": " + message; Events.Enqueue("failed"); }
        public void MetadataAvailable(MediaResourceMetadata metadata) { Metadata = metadata; Events.Enqueue("metadata"); }
        public void ReadyStateChanged(MediaReadyState state) { ReadyState = state; Events.Enqueue("ready:" + (int)state); }
        public void DurationChanged(MediaTime duration) { Duration = duration; Events.Enqueue("duration"); }
        public void VideoSizeChanged(int width, int height) => Events.Enqueue($"size:{width}x{height}");
        public void PositionChanged(MediaTime position, bool monotonic) { Position = position; }
        public void ReachedEnd() { EndedCount++; Events.Enqueue("ended"); }
        public void SeekCompleted(MediaTime position) => Events.Enqueue("seeked");
        public void BufferedChanged(MediaTimeRanges buffered) { Buffered = buffered; Events.Enqueue("buffered"); }
        public void SeekableChanged(MediaTimeRanges seekable) { Seekable = seekable; Events.Enqueue("seekable"); }
        public void Progress() => Events.Enqueue("progress");
        public void Suspended() { }
        public void Resumed() { }
        public void Stalled() { }
        public void FetchedEntirely() { Fetched = true; Events.Enqueue("fetched"); }

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

    private static (MediaPlayer Player, MediaSourceModel Model) Create(RecordingClient client, IMediaLogSink? log = null)
    {
        var demuxers = new DemuxerRegistry();
        var decoders = new DecoderRegistry();
        MediaFormats.RegisterBuiltIn(demuxers, decoders);
        Assert.True(FfmpegDecoders.TryRegister(decoders, NullMediaLogSink.Instance), "libavcodec must be available on the development machine");
        FenBrowser.Media.Codecs.MediaFoundation.MediaFoundationDecoders.TryRegister(decoders, NullMediaLogSink.Instance);
        var services = new MediaPlayerServices(demuxers, decoders, NullAudioOutputFactory.Realtime, TimeProvider.System);
        var context = MediaPipelineContext.ForTests(log);
        var model = new MediaSourceModel(context);
        model.Attach();
        return (new MediaPlayer(model, client, action => action(), services, context), model);
    }

    [Fact]
    public async Task AppendsDriveThePlayer_StarvationStalls_EndOfStreamEnds_AndAnAppendReopens()
    {
        var client = new RecordingClient();
        var (player, model) = Create(client);
        player.Start();
        try
        {
            await Task.Delay(100);
            Assert.Null(client.Metadata); // nothing until the first initialization segment

            var buffer = model.AddSourceBuffer("video/webm", generateTimestamps: false);
            var bytes = MediaFixtures.Read("pattern_vp9.webm");
            Assert.Equal(AppendOutcome.Ok, buffer.Append(bytes));
            model.NotifyChanged();

            await client.WaitForAsync(() => client.Metadata is not null);
            Assert.Equal(64, client.Metadata!.VideoWidth);
            Assert.True(client.Metadata.Duration.TotalSeconds >= 1.0);
            await client.WaitForAsync(() => client.ReadyState >= MediaReadyState.HaveFutureData);
            Assert.Equal([(MediaTime.Zero, MediaTime.FromSeconds(1.0))], Enumerable.Range(0, client.Buffered.Count).Select(i => (client.Buffered.Start(i), client.Buffered.End(i))));
            Assert.False(client.Fetched, "an open MediaSource has not fetched everything");

            // Playing through the single second buffered: the pipeline starves at 1 s and
            // waits rather than ending.
            player.UpdatePlayback(potentiallyPlaying: true, playbackRate: 1.0, preservesPitch: true, effectiveVolume: 1.0);
            await client.WaitForAsync(() => client.Position >= MediaTime.FromSeconds(0.8), timeoutMs: 4000);
            await Task.Delay(400);
            Assert.Equal(0, client.EndedCount);

            // More data arrives at 1 s: playback goes on. Then endOfStream ends it.
            buffer.SetTimestampOffset(MediaTime.FromSeconds(1));
            Assert.Equal(AppendOutcome.Ok, buffer.Append(bytes));
            model.NotifyChanged();
            await client.WaitForAsync(() => client.Position >= MediaTime.FromSeconds(1.5), timeoutMs: 4000);
            model.EndOfStream(EndOfStreamError.None);
            await client.WaitForAsync(() => client.EndedCount == 1, timeoutMs: 4000);
            Assert.True(client.Fetched);
            Assert.Equal(2.0, client.Duration!.Value.TotalSeconds, 2);
            Assert.Equal(2.0, client.Seekable.End(client.Seekable.Count - 1).TotalSeconds, 2);

            // An append after endOfStream reopens the source (§3.5.4 step 3) and the
            // stream goes on to a second end.
            buffer.SetTimestampOffset(MediaTime.FromSeconds(2));
            model.Reopen();
            Assert.Equal(AppendOutcome.Ok, buffer.Append(bytes));
            model.NotifyChanged();
            player.UpdatePlayback(potentiallyPlaying: true, playbackRate: 1.0, preservesPitch: true, effectiveVolume: 1.0);
            await client.WaitForAsync(() => client.Position >= MediaTime.FromSeconds(2.5), timeoutMs: 4000);
            model.EndOfStream(EndOfStreamError.None);
            await client.WaitForAsync(() => client.EndedCount == 2, timeoutMs: 4000);
            Assert.Equal(3.0, client.Position.TotalSeconds, 1);
            Assert.Null(client.Failure);
        }
        finally
        {
            player.Dispose();
        }
    }

    /// <summary>mediasource-config-change-*-framesize: every picture size change fires resize (HTML §4.8.12.5).</summary>
    [Fact]
    public async Task PictureSizeChanges_ReportTheNewSize()
    {
        var root = Environment.GetEnvironmentVariable("FEN_WPT_ROOT") ?? @"D:\wpt";
        if (!Directory.Exists(Path.Combine(root, "media-source")))
            return;
        var a = File.ReadAllBytes(Path.Combine(root, "media-source", "webm", "test-v-128k-320x240-24fps-8kfr.webm"));
        var b = File.ReadAllBytes(Path.Combine(root, "media-source", "webm", "test-v-128k-640x480-30fps-10kfr.webm"));
        var client = new RecordingClient();
        var (player, model) = Create(client);
        player.Start();
        try
        {
            var buffer = model.AddSourceBuffer("video/webm;codecs=\"vp8\"", generateTimestamps: false);
            lock (model.Gate)
            {
                Assert.Equal(AppendOutcome.Ok, buffer.Append(a));
                buffer.SetTimestampOffset(MediaTime.FromSeconds(0.5));
                Assert.Equal(AppendOutcome.Ok, buffer.Append(b));
                buffer.SetTimestampOffset(MediaTime.FromSeconds(1));
                Assert.Equal(AppendOutcome.Ok, buffer.Append(a));
                buffer.SetTimestampOffset(MediaTime.FromSeconds(1.5));
                Assert.Equal(AppendOutcome.Ok, buffer.Append(b));
                buffer.Remove(MediaTime.FromSeconds(2), MediaTime.PositiveInfinity);
                model.SetDuration(MediaTime.FromSeconds(2));
                model.EndOfStream(EndOfStreamError.None);
            }

            model.NotifyChanged();
            await client.WaitForAsync(() => client.Metadata is not null);
            Assert.Equal(320, client.Metadata!.VideoWidth);
            player.UpdatePlayback(potentiallyPlaying: true, playbackRate: 1.0, preservesPitch: true, effectiveVolume: 1.0);
            await client.WaitForAsync(() => client.EndedCount == 1, timeoutMs: 6000);
            Assert.Equal(["size:640x480", "size:320x240", "size:640x480"], client.Events.Where(e => e.StartsWith("size:", StringComparison.Ordinal)));
            Assert.Null(client.Failure);
        }
        finally
        {
            player.Dispose();
        }
    }

    /// <summary>
    /// A paused element over the WPT test.mp4 (AAC + H.264 through the OS decoder, whose
    /// pipeline holds many frames) must still reach HAVE_ENOUGH_DATA: the look-ahead
    /// cannot stop at the audio limit while no picture has come out.
    /// </summary>
    [Fact]
    public async Task PausedElement_ReachesEnoughData_WithADeepVideoDecoder()
    {
        var root = Environment.GetEnvironmentVariable("FEN_WPT_ROOT") ?? @"D:\wpt";
        var file = Path.Combine(root, "media-source", "mp4", "test.mp4");
        if (!File.Exists(file) || !OperatingSystem.IsWindows())
            return;
        var client = new RecordingClient();
        var (player, model) = Create(client);
        player.Start();
        try
        {
            var buffer = model.AddSourceBuffer("video/mp4; codecs=\"mp4a.40.2,avc1.4d400d\"", generateTimestamps: false);
            lock (model.Gate)
                Assert.Equal(AppendOutcome.Ok, buffer.Append(File.ReadAllBytes(file)));
            model.NotifyChanged();
            await client.WaitForAsync(() => client.ReadyState >= MediaReadyState.HaveFutureData, timeoutMs: 6000);
            Assert.Null(client.Failure);
        }
        finally
        {
            player.Dispose();
        }
    }

    /// <summary>
    /// mediasource-seek-beyond-duration: a seek to the duration lands past the buffered
    /// data, so it waits (HTML §4.8.11.9 step 12) until endOfStream(), which lowers the
    /// duration to the buffered end, moves the position there and ends playback.
    /// </summary>
    [Fact]
    public async Task SeekPastTheBufferedData_WaitsForEndOfStream_ThenEnds()
    {
        var root = Environment.GetEnvironmentVariable("FEN_WPT_ROOT") ?? @"D:\wpt";
        var file = Path.Combine(root, "media-source", "mp4", "test.mp4");
        if (!File.Exists(file) || !OperatingSystem.IsWindows())
            return;
        var client = new RecordingClient();
        var (player, model) = Create(client);
        player.Start();
        try
        {
            var buffer = model.AddSourceBuffer("video/mp4; codecs=\"mp4a.40.2,avc1.4d400d\"", generateTimestamps: false);
            lock (model.Gate)
                Assert.Equal(AppendOutcome.Ok, buffer.Append(File.ReadAllBytes(file)));
            model.NotifyChanged();
            await client.WaitForAsync(() => client.ReadyState >= MediaReadyState.HaveFutureData, timeoutMs: 6000);
            player.UpdatePlayback(potentiallyPlaying: true, playbackRate: 1.0, preservesPitch: true, effectiveVolume: 1.0);
            await client.WaitForAsync(() => client.Position >= MediaTime.FromSeconds(0.3), timeoutMs: 4000);

            // The init segment says 6.549 s; the frames end at 6.548117 s (audio).
            Assert.Equal(6.549, client.Metadata!.Duration.TotalSeconds, 3);
            player.Seek(MediaTime.FromSeconds(6.549), approximateForSpeed: false);
            await Task.Delay(500);
            Assert.DoesNotContain("seeked", client.Events);
            Assert.Equal(0, client.EndedCount);

            model.EndOfStream(EndOfStreamError.None);
            // The element, told the duration shrank below its position, seeks to the new
            // duration at once (duration change step 6): the second seek replaces the
            // pending one and still ends playback.
            player.Seek(MediaTime.FromSeconds(6.548117), approximateForSpeed: false);
            await client.WaitForAsync(() => client.Events.Contains("seeked"), timeoutMs: 4000);
            await client.WaitForAsync(() => client.EndedCount == 1, timeoutMs: 4000);
            Assert.Equal(6.548117, client.Duration!.Value.TotalSeconds, 5);
            Assert.Equal(6.548117, client.Position.TotalSeconds, 3);
            Assert.Null(client.Failure);
        }
        finally
        {
            player.Dispose();
        }
    }

    /// <summary>mediasource-endofstream: a paused element told the stream ended has all the data, so canplaythrough follows.</summary>
    [Fact]
    public async Task EndOfStream_OnAPausedElement_MeansEnoughData()
    {
        var root = Environment.GetEnvironmentVariable("FEN_WPT_ROOT") ?? @"D:\wpt";
        var file = Path.Combine(root, "media-source", "mp4", "test.mp4");
        if (!File.Exists(file) || !OperatingSystem.IsWindows())
            return;
        var client = new RecordingClient();
        var (player, model) = Create(client);
        player.Start();
        try
        {
            var buffer = model.AddSourceBuffer("video/mp4; codecs=\"mp4a.40.2,avc1.4d400d\"", generateTimestamps: false);
            lock (model.Gate)
                Assert.Equal(AppendOutcome.Ok, buffer.Append(File.ReadAllBytes(file)));
            model.NotifyChanged();
            await client.WaitForAsync(() => client.ReadyState >= MediaReadyState.HaveFutureData, timeoutMs: 6000);
            model.EndOfStream(EndOfStreamError.None);
            await client.WaitForAsync(() => client.ReadyState == MediaReadyState.HaveEnoughData, timeoutMs: 4000);
            Assert.Null(client.Failure);
        }
        finally
        {
            player.Dispose();
        }
    }

    /// <summary>mediasource-buffered-seek: readiness for the new position is reported with the seek's completion, so the element reads more than HAVE_METADATA in its seeked handler.</summary>
    [Fact]
    public async Task SeekIntoBufferedData_ReportsReadinessWithTheCompletion()
    {
        var root = Environment.GetEnvironmentVariable("FEN_WPT_ROOT") ?? @"D:\wpt";
        var file = Path.Combine(root, "media-source", "mp4", "test.mp4");
        if (!File.Exists(file) || !OperatingSystem.IsWindows())
            return;
        var client = new RecordingClient();
        var (player, model) = Create(client);
        player.Start();
        try
        {
            var buffer = model.AddSourceBuffer("video/mp4; codecs=\"mp4a.40.2,avc1.4d400d\"", generateTimestamps: false);
            lock (model.Gate)
                Assert.Equal(AppendOutcome.Ok, buffer.Append(File.ReadAllBytes(file).AsSpan(0, 25447))); // init + media[0]
            model.NotifyChanged();
            await client.WaitForAsync(() => client.ReadyState >= MediaReadyState.HaveFutureData, timeoutMs: 6000);
            player.UpdatePlayback(potentiallyPlaying: true, playbackRate: 1.0, preservesPitch: true, effectiveVolume: 1.0);
            await client.WaitForAsync(() => client.Position >= MediaTime.FromSeconds(0.3), timeoutMs: 4000);

            client.Events.Clear();
            player.Seek(MediaTime.FromSeconds(0.0475), approximateForSpeed: false);
            await client.WaitForAsync(() => client.Events.Contains("seeked"), timeoutMs: 4000);
            var events = client.Events.ToArray();
            int seeked = Array.IndexOf(events, "seeked");
            Assert.True(seeked > 0 && events.Take(seeked).Any(e => e.StartsWith("ready:", StringComparison.Ordinal) && int.Parse(e.Substring(6)) >= 2), string.Join(",", events));
        }
        finally
        {
            player.Dispose();
        }
    }

    [Fact]
    public async Task DetachBeforeTheFirstSegment_FailsAsUnsupported()
    {
        var client = new RecordingClient();
        var (player, model) = Create(client);
        player.Start();
        try
        {
            await Task.Delay(50);
            model.Detach();
            await client.WaitForAsync(() => client.Failure is not null);
            Assert.StartsWith("Unsupported", client.Failure);
        }
        finally
        {
            player.Dispose();
        }
    }
}
