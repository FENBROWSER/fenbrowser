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
}
