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
