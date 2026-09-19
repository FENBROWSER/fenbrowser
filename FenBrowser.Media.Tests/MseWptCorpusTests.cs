using FenBrowser.Media.Mse;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The MSE byte streams the web-platform-tests feed a MediaSource (test.mp4: AAC+H.264
/// with styp/sidx before every fragment; test.webm: Vorbis+VP8 with unknown-size
/// Clusters), appended the way the tests do - the initialization segment, then each
/// media segment. Skipped when the local WPT checkout is not there.
/// </summary>
public class MseWptCorpusTests
{
    private static string? WptRoot()
    {
        var root = Environment.GetEnvironmentVariable("FEN_WPT_ROOT") ?? @"D:\wpt";
        return Directory.Exists(Path.Combine(root, "media-source")) ? root : null;
    }

    [Theory]
    [InlineData("mp4/test.mp4", "video/mp4; codecs=\"mp4a.40.2,avc1.4d400d\"", 1413, 6.549)]
    [InlineData("webm/test.webm", "video/webm; codecs=\"vp8, vorbis\"", 4116, 6.552)]
    public void WholeFile_BuffersAudioAndVideo(string file, string type, int initSize, double duration)
    {
        var root = WptRoot();
        if (root is null)
            return;
        var bytes = File.ReadAllBytes(Path.Combine(root, "media-source", file));
        var model = new MediaSourceModel(MediaPipelineContext.ForTests());
        model.Attach();
        var buffer = model.AddSourceBuffer(type, generateTimestamps: false);

        var outcome = buffer.Append(bytes.AsSpan(0, initSize));
        Assert.True(outcome == AppendOutcome.Ok, "init: " + buffer.LastParseError);
        Assert.Equal(2, buffer.TrackBuffers.Count);
        outcome = buffer.Append(bytes.AsSpan(initSize));
        Assert.True(outcome == AppendOutcome.Ok, "media: " + buffer.LastParseError);

        var audio = buffer.TrackBuffers.Single(t => t.Kind == MediaTrackKind.Audio);
        var video = buffer.TrackBuffers.Single(t => t.Kind == MediaTrackKind.Video);
        Assert.True(audio.Frames.Count > 100, $"audio frames: {audio.Frames.Count}");
        Assert.True(video.Frames.Count > 100, $"video frames: {video.Frames.Count}");
        var buffered = buffer.Buffered;
        Assert.Equal(1, buffered.Count);
        Assert.True(buffered.Start(0).TotalSeconds < 0.2, buffered.Start(0).ToString());
        Assert.Equal(duration, buffered.End(0).TotalSeconds, 0.15);
    }

    /// <summary>The per-track ranges mediasource-buffered.html expects, to the millisecond, so the frame duration rules match the other engines.</summary>
    [Theory]
    [InlineData("mp4/test-av-384k-44100Hz-1ch-320x240-30fps-10kfr.mp4", "video/mp4;codecs=\"avc1.4D4001,mp4a.40.2\"", "[0.067,2.067)", "[0.000,2.043)")]
    [InlineData("webm/test-av-384k-44100Hz-1ch-320x240-30fps-10kfr.webm", "video/webm;codecs=\"vp8,vorbis\"", "[0.003,2.004)", "[0.000,2.023)")]
    [InlineData("webm/test-v-128k-320x240-30fps-10kfr.webm", "video/webm;codecs=\"vp8\"", "[0.000,2.001)", null)]
    [InlineData("webm/test-a-128k-44100Hz-1ch.webm", "audio/webm;codecs=\"vorbis\"", null, "[0.000,2.023)")]
    public void TrackRanges_MatchTheOtherEngines(string file, string type, string? video, string? audio)
    {
        var root = WptRoot();
        if (root is null)
            return;
        var model = new MediaSourceModel(MediaPipelineContext.ForTests());
        model.Attach();
        var buffer = model.AddSourceBuffer(type, generateTimestamps: false);
        Assert.Equal(AppendOutcome.Ok, buffer.Append(File.ReadAllBytes(Path.Combine(root, "media-source", file))));

        static string Describe(TrackBuffer track) =>
            string.Join(" ", Enumerable.Range(0, track.Buffered.Count).Select(i => $"[{track.Buffered.Start(i).TotalSeconds:F3},{track.Buffered.End(i).TotalSeconds:F3})"));

        Assert.Equal(video, buffer.TrackBuffers.SingleOrDefault(t => t.Kind == MediaTrackKind.Video) is { } v ? Describe(v) : null);
        Assert.Equal(audio, buffer.TrackBuffers.SingleOrDefault(t => t.Kind == MediaTrackKind.Audio) is { } a ? Describe(a) : null);
    }
}

public class MseWptConfigChangeTests
{
    private static string? WptRoot()
    {
        var root = Environment.GetEnvironmentVariable("FEN_WPT_ROOT") ?? @"D:\wpt";
        return Directory.Exists(Path.Combine(root, "media-source")) ? root : null;
    }

    /// <summary>mediasource-config-change-webm-v-framesize: A at 0, B at 0.5, A at 1, B at 1.5, remove past 2, duration 2, endOfStream, play.</summary>
    [Fact]
    public async Task FrameSizeChanges_DecodeEveryFrame()
    {
        var root = WptRoot();
        if (root is null)
            return;
        var a = File.ReadAllBytes(Path.Combine(root, "media-source", "webm", "test-v-128k-320x240-24fps-8kfr.webm"));
        var b = File.ReadAllBytes(Path.Combine(root, "media-source", "webm", "test-v-128k-640x480-30fps-10kfr.webm"));
        var decoders = new DecoderRegistry();
        Assert.True(FenBrowser.Media.Codecs.Ffmpeg.FfmpegDecoders.TryRegister(decoders, Diagnostics.NullMediaLogSink.Instance));
        var context = MediaPipelineContext.ForTests();
        var model = new MediaSourceModel(context);
        model.Attach();
        var buffer = model.AddSourceBuffer("video/webm;codecs=\"vp8\"", generateTimestamps: false);
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

        await using var source = new MseDecodeSource(model, decoders, context);
        await source.OpenAsync(CancellationToken.None);
        int pictures = 0;
        var sizes = new List<int>();
        while (await source.ReadAsync(CancellationToken.None) is { } item)
        {
            using (item)
            {
                if (item.Video is { } picture && (sizes.Count == 0 || sizes[^1] != picture.Width))
                    sizes.Add(picture.Width);
                pictures++;
            }
        }

        Assert.Equal([320, 640, 320, 640], sizes);
        Assert.True(pictures > 50, $"pictures: {pictures}");
    }
}

public class MseWptOverlapUnderCursorTests
{
    private static string? WptRoot()
    {
        var root = Environment.GetEnvironmentVariable("FEN_WPT_ROOT") ?? @"D:\wpt";
        return Directory.Exists(Path.Combine(root, "media-source")) ? root : null;
    }

    /// <summary>
    /// The player is already decoding A when B is appended at 0.5 s over it (what the
    /// config-change tests do while paused): the frames under the cursor are replaced, so
    /// decoding restarts from B's random access point and continues without a gap or an
    /// undecodable frame.
    /// </summary>
    [Fact]
    public async Task OverlappingAppend_UnderTheCursor_RestartsFromTheNewGroup()
    {
        var root = WptRoot();
        if (root is null)
            return;
        var a = File.ReadAllBytes(Path.Combine(root, "media-source", "webm", "test-v-128k-320x240-24fps-8kfr.webm"));
        var b = File.ReadAllBytes(Path.Combine(root, "media-source", "webm", "test-v-128k-640x480-30fps-10kfr.webm"));
        var decoders = new DecoderRegistry();
        Assert.True(FenBrowser.Media.Codecs.Ffmpeg.FfmpegDecoders.TryRegister(decoders, Diagnostics.NullMediaLogSink.Instance));
        var context = MediaPipelineContext.ForTests();
        var model = new MediaSourceModel(context);
        model.Attach();
        var buffer = model.AddSourceBuffer("video/webm;codecs=\"vp8\"", generateTimestamps: false);
        Assert.Equal(AppendOutcome.Ok, buffer.Append(a));

        await using var source = new MseDecodeSource(model, decoders, context);
        await source.OpenAsync(CancellationToken.None);
        var stamps = new List<(double Time, int Width)>();
        while (stamps.Count < 18 && await source.ReadAsync(CancellationToken.None) is { } item)
        {
            using (item)
                stamps.Add((item.Timestamp.TotalSeconds, item.Video!.Width));
        }

        Assert.True(stamps[^1].Time > 0.6, $"decoded up to {stamps[^1].Time}");
        lock (model.Gate)
        {
            buffer.SetTimestampOffset(MediaTime.FromSeconds(0.5));
            Assert.Equal(AppendOutcome.Ok, buffer.Append(b));
            model.EndOfStream(EndOfStreamError.None);
        }

        while (await source.ReadAsync(CancellationToken.None) is { } item)
        {
            using (item)
                stamps.Add((item.Timestamp.TotalSeconds, item.Video!.Width));
        }

        for (int i = 1; i < stamps.Count; i++)
        {
            Assert.True(stamps[i].Time > stamps[i - 1].Time, $"picture {i} at {stamps[i].Time} after {stamps[i - 1].Time}");
            Assert.True(stamps[i].Time - stamps[i - 1].Time < 0.05, $"gap before picture {i}: {stamps[i - 1].Time} -> {stamps[i].Time}");
        }

        Assert.Equal(640, stamps[^1].Width);
        Assert.True(stamps[^1].Time > 2.4, $"ends at {stamps[^1].Time}");
    }
}

public class MseWptChangeTypeTests
{
    private static string? WptRoot()
    {
        var root = Environment.GetEnvironmentVariable("FEN_WPT_ROOT") ?? @"D:\wpt";
        return Directory.Exists(Path.Combine(root, "media-source")) ? root : null;
    }

    /// <summary>mediasource-changetype-play: VP8 in WebM, then changeType() to H.264 in MP4 appended after it; the video track gets a new decoder.</summary>
    [Fact]
    public async Task ChangeType_AcrossCodecs_SwitchesTheDecoder()
    {
        var root = WptRoot();
        if (root is null)
            return;
        var webm = File.ReadAllBytes(Path.Combine(root, "media-source", "webm", "test-v-128k-320x240-24fps-8kfr.webm"));
        var mp4 = File.ReadAllBytes(Path.Combine(root, "media-source", "mp4", "test-v-128k-320x240-24fps-8kfr.mp4"));
        var decoders = new DecoderRegistry();
        Assert.True(FenBrowser.Media.Codecs.Ffmpeg.FfmpegDecoders.TryRegister(decoders, Diagnostics.NullMediaLogSink.Instance));
        if (!FenBrowser.Media.Codecs.MediaFoundation.MediaFoundationDecoders.TryRegister(decoders, Diagnostics.NullMediaLogSink.Instance))
            return;
        var context = MediaPipelineContext.ForTests();
        var model = new MediaSourceModel(context);
        model.Attach();
        var buffer = model.AddSourceBuffer("video/webm;codecs=\"vp8\"", generateTimestamps: false);
        Assert.Equal(AppendOutcome.Ok, buffer.Append(webm));
        var end = buffer.Buffered.End(0);
        buffer.ChangeType("video/mp4;codecs=\"avc1.4D4001\"", model.CreateParser("video/mp4;codecs=\"avc1.4D4001\"")!, generateTimestamps: false);
        buffer.SetTimestampOffset(end);
        Assert.Equal(AppendOutcome.Ok, buffer.Append(mp4));
        model.EndOfStream(EndOfStreamError.None);

        await using var source = new MseDecodeSource(model, decoders, context);
        await source.OpenAsync(CancellationToken.None);
        int pictures = 0;
        MediaTime last = MediaTime.NegativeInfinity;
        while (await source.ReadAsync(CancellationToken.None) is { } item)
        {
            using (item)
            {
                Assert.True(item.Timestamp > last, $"{item.Timestamp} after {last}");
                last = item.Timestamp;
                pictures++;
            }
        }

        Assert.True(last.TotalSeconds > end.TotalSeconds + 1, $"ended at {last}");
        Assert.True(pictures > 80, $"pictures: {pictures}");
    }
}
