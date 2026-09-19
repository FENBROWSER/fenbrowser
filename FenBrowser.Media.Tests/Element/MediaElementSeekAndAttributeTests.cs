using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Element;
using static FenBrowser.Media.Tests.Element.MediaElementTestKit;

namespace FenBrowser.Media.Tests.Element;

// HTML §4.8.11.6 currentTime, §4.8.11.9 seeking, §4.8.11.8 rates, §4.8.11.13 volume and muted.
public class MediaElementSeekAndAttributeTests
{
    [Fact]
    public void CurrentTime_BeforeMetadata_IsTheDefaultStartPosition()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "a.webm");
        element.Load();
        host.Run();

        element.SetCurrentTime(4);

        Assert.Equal(4, element.CurrentTime);
        Assert.False(element.Seeking);
        Assert.Empty(host.Resource!.Seeks);

        host.Resource.LoadMetadata();
        host.Run();

        Assert.Equal(MediaTime.FromSeconds(4), host.Resource.Seeks.Single().Target);
        Assert.True(element.Seeking);
        Assert.Equal(["loadstart", "durationchange", "resize", "loadedmetadata", "seeking"], host.TakeEvents());

        host.Resource.Client.SeekCompleted(MediaTime.FromSeconds(4));
        host.Run();
        Assert.Equal(4, element.CurrentTime);
        Assert.Equal(["timeupdate", "seeked"], host.TakeEvents());
    }

    [Fact]
    public void CurrentTime_BeforeMetadata_SeeksEvenWhenSeekableArrivesLater()
    {
        // A resource that reports its metadata before its seekable ranges: the default
        // playback start position still becomes a seek (the finite duration bounds it).
        var (host, element, _, _) = Create(h => h.SrcAttribute = "a.webm");
        element.Load();
        host.Run();
        element.SetCurrentTime(4);

        host.Resource!.Client.MetadataAvailable(new MediaResourceMetadata(MediaTime.FromSeconds(10), 320, 240, []));
        host.Run();

        Assert.Equal(MediaTime.FromSeconds(4), host.Resource.Seeks.Single().Target);
        Assert.True(element.Seeking);
        Assert.Equal(4, element.CurrentTime);
    }

    [Fact]
    public void CurrentTime_BeforeMetadata_ReadsBackExactly()
    {
        // The default playback start position is the double script set; it only becomes
        // a (saturating) media time once there is a resource to seek in.
        var (_, element, _, _) = Create();

        element.SetCurrentTime(double.MaxValue);

        Assert.Equal(double.MaxValue, element.CurrentTime);
        Assert.False(element.Seeking);
    }

    [Fact]
    public void CurrentTime_Setter_SeeksAndCompletes()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);

        element.SetCurrentTime(6.5);

        Assert.True(element.Seeking);
        Assert.False(element.ShowPoster);
        Assert.Equal(6.5, element.CurrentTime);
        Assert.Equal((MediaTime.FromSeconds(6.5), false), host.Resource!.Seeks.Single());
        host.Run();
        Assert.Equal(["seeking"], host.TakeEvents());

        host.Resource.Client.SeekCompleted(MediaTime.FromSeconds(6.5));
        host.Run();

        Assert.False(element.Seeking);
        Assert.Equal(["timeupdate", "seeked"], host.TakeEvents());
    }

    [Fact]
    public void FastSeek_SetsTheApproximateFlag()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        element.FastSeek(3);
        Assert.True(host.Resource!.Seeks.Single().Approximate);
    }

    [Fact]
    public void FastSeek_BeforeMetadata_DoesNothing()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "a.webm");
        element.Load();
        host.Run();

        element.FastSeek(3);

        Assert.Empty(host.Resource!.Seeks);
        Assert.Equal(0, element.CurrentTime);
    }

    [Theory]
    [InlineData(99, 10)]   // past the end clamps to the duration
    [InlineData(-5, 0)]    // before the start clamps to the earliest position
    public void Seek_ClampsToTheResource(double target, double expected)
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        element.SetCurrentTime(target);
        Assert.Equal(MediaTime.FromSeconds(expected), host.Resource!.Seeks.Single().Target);
    }

    /// <summary>WPT seeking/seek-to-negative-time and seek-to-max-value: currentTime reads the clamped target right after the setter.</summary>
    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(double.MaxValue, 10.0)]
    [InlineData(4.25, 4.25)]
    public void CurrentTime_Setter_ReadsBackTheClampedTargetAtOnce(double requested, double expected)
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);

        element.SetCurrentTime(requested);

        Assert.True(element.Seeking);
        Assert.Equal(expected, element.CurrentTime);
        Assert.Equal(MediaTime.FromSeconds(expected), host.Resource!.Seeks.Single().Target);
    }

    /// <summary>WPT offsets/currentTime-move-within-document: a position report from the clock being left does not undo the seek.</summary>
    [Fact]
    public void PositionReportsDuringAPendingSeekAreIgnored()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);

        element.SetCurrentTime(10);
        host.Resource!.Client.PositionChanged(MediaTime.FromSeconds(0.4), monotonic: true);
        host.Run();

        Assert.Equal(10, element.CurrentTime);
        Assert.True(element.Seeking);

        host.Resource.Client.SeekCompleted(MediaTime.FromSeconds(10));
        host.Run();
        host.Resource.Client.PositionChanged(MediaTime.FromSeconds(10.2), monotonic: true);
        host.Run();
        Assert.Equal(10.2, element.CurrentTime);
    }

    [Fact]
    public void Seek_SnapsIntoSeekableRanges()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        host.Resource!.Client.SeekableChanged(MediaTimeRanges.From([
            (MediaTime.FromSeconds(0), MediaTime.FromSeconds(2)),
            (MediaTime.FromSeconds(6), MediaTime.FromSeconds(8)),
        ]));
        host.Run();

        element.SetCurrentTime(3);
        element.SetCurrentTime(5);

        Assert.Equal(MediaTime.FromSeconds(2), host.Resource.Seeks[0].Target);
        Assert.Equal(MediaTime.FromSeconds(6), host.Resource.Seeks[1].Target);
    }

    [Fact]
    public void Seek_WithNothingSeekable_EndsImmediately()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        host.Resource!.Client.SeekableChanged(MediaTimeRanges.Empty);
        host.Run();

        element.SetCurrentTime(3);
        host.Run();

        Assert.False(element.Seeking);
        Assert.Empty(host.Resource.Seeks);
        Assert.Empty(host.TakeEvents());
    }

    [Fact]
    public void Seek_SupersededSeekCompletesOnlyOnce()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);

        element.SetCurrentTime(2);
        element.SetCurrentTime(7);
        host.Resource!.Client.SeekCompleted(MediaTime.FromSeconds(7));
        host.Run();

        Assert.Equal(["seeking", "seeking", "timeupdate", "seeked"], host.TakeEvents());
        Assert.False(element.Seeking);
        Assert.Equal(7, element.CurrentTime);

        // A stale completion after that changes nothing.
        host.Resource.Client.SeekCompleted(MediaTime.FromSeconds(2));
        host.Run();
        Assert.Empty(host.TakeEvents());
        Assert.Equal(7, element.CurrentTime);
    }

    [Fact]
    public void Seek_AnswerForAnEphemeralSeek_IsIgnored()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);

        // 1, then 3, then 1 again: the resource may have started the seek to 3 before the
        // last request reached it; its answer for 3 is not the seek script is waiting on.
        element.SetCurrentTime(1);
        element.SetCurrentTime(3);
        element.SetCurrentTime(1);
        host.Resource!.Client.SeekCompleted(MediaTime.FromSeconds(3));
        host.Resource.Client.PositionChanged(MediaTime.FromSeconds(3), monotonic: false);
        host.Run();
        Assert.Equal(["seeking", "seeking", "seeking"], host.TakeEvents());
        Assert.True(element.Seeking);
        Assert.Equal(1, element.CurrentTime);

        host.Resource.Client.SeekCompleted(MediaTime.FromSeconds(1));
        host.Run();
        Assert.Equal(["timeupdate", "seeked"], host.TakeEvents());
        Assert.False(element.Seeking);
    }

    /// <summary>MSE §2.4.2: attaching a MediaSource stops delaying the load event before any data; a fetch keeps delaying it until loadeddata.</summary>
    [Fact]
    public void MediaSourceAttachment_StopsDelayingTheLoadEvent_AtOnce()
    {
        var (host, element, _, _) = Create(h => { h.SrcAttribute = "blob:https://a/1"; h.NextResourceIsProviderObject = true; });
        element.Load();
        host.Run();
        Assert.False(host.Delaying);

        var (fetchHost, fetchElement, _, _) = Create(h => h.SrcAttribute = "a.webm");
        fetchElement.Load();
        fetchHost.Run();
        Assert.True(fetchHost.Delaying);
    }

    [Fact]
    public void Seek_IsLogged()
    {
        var (host, element, log, _) = Create(h => h.SrcAttribute = "a.webm");
        element.Load();
        host.Run();
        host.Resource!.LoadMetadata();
        host.Run();

        element.SetCurrentTime(1);
        host.Resource.Client.SeekCompleted(MediaTime.FromSeconds(1));
        host.Run();

        Assert.Contains(log.Events, e => e.Kind == MediaEventKind.SeekStart && e.Player == element.Player);
        Assert.Contains(log.Events, e => e.Kind == MediaEventKind.SeekEnd);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Volume_InRange_IsAccepted(double volume)
    {
        var (host, element, _, _) = Create();
        Assert.True(element.TrySetVolume(volume));
        Assert.Equal(volume, element.Volume);
        host.Run();
        Assert.Equal(volume == 1.0 ? [] : ["volumechange"], host.TakeEvents());
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Volume_OutOfRange_IsRejected(double volume)
    {
        var (host, element, _, _) = Create();
        Assert.False(element.TrySetVolume(volume));
        Assert.Equal(1.0, element.Volume);
        host.Run();
        Assert.Empty(host.TakeEvents());
    }

    [Fact]
    public void Volume_ReachesTheResource()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        element.TrySetVolume(0.25);
        Assert.Equal(0.25, host.Resource!.LastPlayback.Volume);
    }

    [Fact]
    public void Muted_DefaultsToTheAttribute_UntilSet()
    {
        var (host, element, _, _) = Create(h => h.HasMutedAttribute = true);
        Assert.True(element.Muted);
        Assert.Equal(0.0, element.EffectiveVolume);

        host.HasMutedAttribute = false;
        Assert.False(element.Muted);

        element.SetMuted(true);
        host.HasMutedAttribute = false;
        Assert.True(element.Muted);
        element.SetMuted(true); // no change, no event
        element.SetMuted(false);
        Assert.False(element.Muted);
        host.Run();
        Assert.Equal(["volumechange", "volumechange"], host.TakeEvents());
    }

    [Fact]
    public void Unmuting_WhenNotAllowedToPlay_Pauses()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData, h => h.HasMutedAttribute = true);
        element.Play();
        host.Run();
        host.TakeEvents();

        // The policy only allowed muted playback.
        host.IsAllowedToPlay = false;
        element.SetMuted(false);
        host.Run();

        Assert.True(element.Paused);
        Assert.Equal(["timeupdate", "pause", "volumechange"], host.TakeEvents());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.0625)]
    [InlineData(2.0)]
    [InlineData(16.0)]
    public void PlaybackRate_Supported(double rate)
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        Assert.True(element.TrySetPlaybackRate(rate));
        Assert.Equal(rate, element.PlaybackRate);
        Assert.Equal(rate, host.Resource!.LastPlayback.Rate);
        host.Run();
        Assert.Equal(["ratechange"], host.TakeEvents());
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(0.05)]
    [InlineData(16.5)]
    public void PlaybackRate_Unsupported(double rate)
    {
        var (host, element, _, _) = Create();
        Assert.False(element.TrySetPlaybackRate(rate));
        Assert.Equal(1.0, element.PlaybackRate);
        host.Run();
        Assert.Empty(host.TakeEvents());
    }

    [Fact]
    public void PlaybackRate_SameValue_FiresNothing()
    {
        var (host, element, _, _) = Create();
        Assert.True(element.TrySetPlaybackRate(1.0));
        host.Run();
        Assert.Empty(host.TakeEvents());
    }

    [Fact]
    public void DefaultPlaybackRate_AcceptsAnyValueAndFiresRatechange()
    {
        var (host, element, _, _) = Create();
        element.DefaultPlaybackRate = -2;
        element.DefaultPlaybackRate = -2;
        Assert.Equal(-2, element.DefaultPlaybackRate);
        host.Run();
        Assert.Equal(["ratechange"], host.TakeEvents());
    }

    [Fact]
    public void PreservesPitch_ReachesTheResource()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        element.PreservesPitch = false;
        Assert.False(host.Resource!.LastPlayback.PreservesPitch);
        Assert.False(element.PreservesPitch);
    }

    [Fact]
    public void Controller_LogsItsLifecycle()
    {
        var (host, element, log, _) = Create(h => h.SrcAttribute = "");
        element.Load();
        host.Run();

        var kinds = log.Events.Where(e => e.Player == element.Player).Select(e => e.Kind).ToList();
        Assert.Equal(MediaEventKind.PlayerCreated, kinds[0]);
        Assert.Contains(MediaEventKind.NetworkStateChanged, kinds);
        Assert.Contains(MediaEventKind.SourceSelected, kinds);
        Assert.Contains(MediaEventKind.Error, kinds);
    }
}
