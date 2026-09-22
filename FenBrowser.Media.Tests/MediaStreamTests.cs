using FenBrowser.Media.Streams;
using FenBrowser.Media.Types;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The stream behind <c>captureStream()</c>: which tracks it has as the element it follows
/// gains and loses them, and when it stops being active.
/// </summary>
public class MediaStreamTests
{
    private static MediaTrackInfo Audio() =>
        new(1, CodecConfig.Audio(MediaCodec.Opus, 48000, 2), MediaTime.FromSeconds(1));

    private static MediaTrackInfo Video() =>
        new(2, CodecConfig.Video(MediaCodec.Vp9, 320, 240), MediaTime.FromSeconds(1));

    [Fact]
    public void AnEmptyStreamIsNotActiveAndHasNoTracks()
    {
        var stream = new MediaStreamModel();
        Assert.Empty(stream.Tracks);
        Assert.False(stream.Active);
        Assert.NotEmpty(stream.Id);
    }

    [Fact]
    public void FollowingAnElementWithBothKindsAddsOneTrackEach()
    {
        var stream = new MediaStreamModel();
        var change = stream.Follow([Audio(), Video()], muted: false);

        Assert.Equal(2, change.Added.Count);
        Assert.Empty(change.Removed);
        Assert.True(stream.Active);
        Assert.Contains(stream.Tracks, t => t.Kind == MediaStreamTrackKind.Audio);
        Assert.Contains(stream.Tracks, t => t.Kind == MediaStreamTrackKind.Video);
        Assert.All(stream.Tracks, t => Assert.True(t.Live));
        // Every track has its own identifier, which is what a page keys them by.
        Assert.Equal(2, stream.Tracks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void FollowingTheSameElementAgainChangesNothing()
    {
        // A page holds the track objects it was handed; a second look at an unchanged
        // element must not replace them.
        var stream = new MediaStreamModel();
        stream.Follow([Audio(), Video()], muted: false);
        var before = stream.Tracks.ToArray();

        var change = stream.Follow([Audio(), Video()], muted: false);

        Assert.False(change.AnythingChanged);
        Assert.Equal(before, stream.Tracks);
    }

    [Fact]
    public void ATrackTheElementNoLongerHasIsRemovedAndEnded()
    {
        var stream = new MediaStreamModel();
        stream.Follow([Audio(), Video()], muted: false);
        var video = stream.Tracks.Single(t => t.Kind == MediaStreamTrackKind.Video);

        var change = stream.Follow([Audio()], muted: false);

        Assert.Same(video, Assert.Single(change.Removed));
        Assert.False(video.Live);
        Assert.DoesNotContain(video, stream.Tracks);
        Assert.True(stream.Active); // the audio track is still live
    }

    [Fact]
    public void AnElementThatLosesEverythingLeavesAnInactiveStream()
    {
        var stream = new MediaStreamModel();
        stream.Follow([Audio(), Video()], muted: false);

        var change = stream.Follow([], muted: false);

        Assert.Equal(2, change.Removed.Count);
        Assert.Empty(stream.Tracks);
        Assert.False(stream.Active);
    }

    [Fact]
    public void RemovingEveryTrackAtOnceReportsThemInTheOrderTheyWereAdded()
    {
        var stream = new MediaStreamModel();
        stream.Follow([Audio(), Video()], muted: false);
        var expected = stream.Tracks.ToArray();

        var removed = stream.RemoveAllTracks();

        Assert.Equal(expected, removed);
        Assert.All(removed, t => Assert.False(t.Live));
        Assert.False(stream.Active);
        Assert.Empty(stream.RemoveAllTracks());
    }

    [Fact]
    public void ATrackOnlyEndsOnce()
    {
        var stream = new MediaStreamModel();
        var track = stream.AddTrack(MediaStreamTrackKind.Audio);

        Assert.True(track.End());
        Assert.False(track.End());
        Assert.False(stream.Active);

        // Taking an ended track out again is not a change the page hears about.
        Assert.True(stream.RemoveTrack(track));
        Assert.False(stream.RemoveTrack(track));
    }

    [Fact]
    public void TheTracksAreMutedWhileTheElementIsNotDelivering()
    {
        var stream = new MediaStreamModel();
        stream.Follow([Audio(), Video()], muted: true);
        Assert.All(stream.Tracks, t => Assert.True(t.Muted));

        stream.Follow([Audio(), Video()], muted: false);
        Assert.All(stream.Tracks, t => Assert.False(t.Muted));
        // Muting is not ending: the tracks are still live either way.
        Assert.All(stream.Tracks, t => Assert.True(t.Live));
    }

    [Fact]
    public void DisablingATrackLeavesItLive()
    {
        var stream = new MediaStreamModel();
        var track = stream.AddTrack(MediaStreamTrackKind.Video);
        track.Enabled = false;

        Assert.False(track.Enabled);
        Assert.True(track.Live);
        Assert.True(stream.Active);
    }
}
