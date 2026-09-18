using FenBrowser.Media.Element;
using static FenBrowser.Media.Tests.Element.MediaElementTestKit;

namespace FenBrowser.Media.Tests.Element;

// HTML §4.8.11.7 ready states and §4.8.11.8 playing the media resource.
public class MediaElementPlaybackTests
{
    [Fact]
    public void Metadata_FiresDurationResizeLoadedMetadata()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "a.webm");
        element.Load();
        host.Run();
        host.TakeEvents();

        host.Resource!.LoadMetadata(durationSeconds: 12.5, width: 640, height: 360);
        host.Run();

        Assert.Equal(["durationchange", "resize", "loadedmetadata"], host.TakeEvents());
        Assert.Equal(MediaReadyState.HaveMetadata, element.ReadyState);
        Assert.Equal(12.5, element.Duration);
        Assert.Equal(640, element.VideoWidth);
        Assert.Equal(360, element.VideoHeight);
        Assert.True(host.Delaying); // still delayed until loadeddata
    }

    [Fact]
    public void AudioMetadata_HasNoResize()
    {
        var (host, element, _, _) = Create(h =>
        {
            h.SrcAttribute = "a.webm";
            h.IsVideo = false;
        });
        element.Load();
        host.Run();
        host.TakeEvents();

        host.Resource!.LoadMetadata(width: 640, height: 360);
        host.Run();

        Assert.Equal(["durationchange", "loadedmetadata"], host.TakeEvents());
        Assert.Equal(0, element.VideoWidth);
    }

    [Fact]
    public void UnboundedStream_HasInfiniteDuration()
    {
        var (_, element, _) = Loaded(MediaReadyState.HaveMetadata, duration: double.PositiveInfinity);
        Assert.True(double.IsPositiveInfinity(element.Duration));
        Assert.False(element.Ended);
    }

    [Fact]
    public void ReadyStates_JumpToEnoughData_FiresEachEventOnce()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveMetadata);

        host.Resource!.Client.ReadyStateChanged(MediaReadyState.HaveEnoughData);
        host.Run();

        Assert.Equal(["loadeddata", "canplay", "canplaythrough"], host.TakeEvents());
        Assert.False(host.Delaying);
    }

    [Fact]
    public void ReadyStates_StepwiseProgression()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveMetadata);
        var client = host.Resource!.Client;

        client.ReadyStateChanged(MediaReadyState.HaveCurrentData);
        host.Run();
        Assert.Equal(["loadeddata"], host.TakeEvents());

        client.ReadyStateChanged(MediaReadyState.HaveFutureData);
        host.Run();
        Assert.Equal(["canplay"], host.TakeEvents());

        client.ReadyStateChanged(MediaReadyState.HaveEnoughData);
        host.Run();
        Assert.Equal(["canplaythrough"], host.TakeEvents());

        // Falling back and rising again: loadeddata is not repeated, canplay is.
        client.ReadyStateChanged(MediaReadyState.HaveMetadata);
        client.ReadyStateChanged(MediaReadyState.HaveEnoughData);
        host.Run();
        Assert.Equal(["canplay", "canplaythrough"], host.TakeEvents());
        Assert.Equal(MediaReadyState.HaveEnoughData, element.ReadyState);
    }

    [Fact]
    public void Play_BeforeData_WaitsThenResolvesWhenPlayable()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveMetadata);

        var promise = AsPromise(element.Play());

        Assert.False(element.Paused);
        Assert.False(element.ShowPoster);
        Assert.True(host.Resource!.FullLoadRequested);
        host.Run();
        Assert.Equal(["play", "waiting"], host.TakeEvents());
        Assert.Equal("pending", promise.State);
        Assert.False(element.IsPotentiallyPlaying);

        host.Resource.Client.ReadyStateChanged(MediaReadyState.HaveFutureData);
        host.Run();

        Assert.Equal(["loadeddata", "canplay", "playing"], host.TakeEvents());
        Assert.Equal("resolved", promise.State);
        Assert.True(element.IsPotentiallyPlaying);
        Assert.True(host.Resource.LastPlayback.Playing);
    }

    [Fact]
    public void Play_WhenReady_FiresPlayAndPlaying()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);

        var promise = AsPromise(element.Play());
        host.Run();

        var log = host.TakeLog();
        Assert.Equal(["play", "playing", $"resolve#{promise.Id}"], log);
    }

    [Fact]
    public void Play_WhileAlreadyPlaying_ResolvesInATaskWithoutEvents()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        element.Play();
        host.Run();
        host.TakeLog();

        var second = AsPromise(element.Play());
        Assert.Equal("pending", second.State);
        host.Run();

        Assert.Equal([$"resolve#{second.Id}"], host.TakeLog());
    }

    [Fact]
    public void Pause_RejectsPendingPlayAndFiresTimeupdateThenPause()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveMetadata);
        var promise = AsPromise(element.Play());

        element.Pause();
        host.Run();

        Assert.True(element.Paused);
        Assert.Equal(["play", "waiting", "timeupdate", "pause", $"reject#{promise.Id}:AbortError"], host.TakeLog());
        Assert.False(host.Resource!.LastPlayback.Playing);
    }

    [Fact]
    public void Pause_WhenAlreadyPaused_FiresNothing()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        element.Pause();
        host.Run();
        Assert.Empty(host.TakeEvents());
    }

    [Fact]
    public void Pause_OnEmptyElement_RunsResourceSelection()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "a.webm");
        element.Pause();
        host.Run();

        Assert.Single(host.Resources);
        Assert.Equal(["loadstart"], host.TakeEvents());
    }

    [Fact]
    public void Play_NotAllowed_RejectsWithoutChangingState()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData, h => h.IsAllowedToPlay = false);

        var promise = AsPromise(element.Play());
        host.Run();

        Assert.Equal("NotAllowedError", promise.State);
        Assert.True(element.Paused);
        Assert.Empty(host.TakeEvents());
    }

    [Fact]
    public void Play_AfterSourceFailure_IsNotSupported()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "");
        element.Load();
        host.Run();

        var promise = AsPromise(element.Play());

        Assert.Equal("NotSupportedError", promise.State);
        Assert.True(element.Paused);
    }

    [Fact]
    public void Play_PendingWhenSourceFails_IsRejectedNotSupported()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "a.webm");
        element.Load();
        host.Run();
        var promise = AsPromise(element.Play());
        host.Run();

        host.Resource!.Client.Failed(MediaResourceFailure.Unsupported, "bad");
        host.Run();

        Assert.Equal("NotSupportedError", promise.State);
    }

    [Fact]
    public void Play_OnEmptyElement_StartsSelection()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "a.webm");

        var promise = AsPromise(element.Play());
        host.Run();

        Assert.Single(host.Resources);
        Assert.True(host.Resource!.FullLoadRequested);
        Assert.Equal(["play", "waiting", "loadstart"], host.TakeEvents());
        Assert.Equal("pending", promise.State);
    }

    [Fact]
    public void Autoplay_StartsWhenEnoughData()
    {
        var (host, element, _, _) = Create(h =>
        {
            h.SrcAttribute = "a.webm";
            h.HasAutoplayAttribute = true;
        });
        element.Load();
        host.Run();
        Assert.True(host.Resource!.FullLoadRequested);
        host.Resource.LoadMetadata();
        host.Resource.Client.ReadyStateChanged(MediaReadyState.HaveEnoughData);
        host.Run();

        Assert.False(element.Paused);
        Assert.False(element.ShowPoster);
        Assert.Equal(
            ["loadstart", "durationchange", "resize", "loadedmetadata", "loadeddata", "canplay", "canplaythrough", "play", "playing"],
            host.TakeEvents());
        Assert.True(element.IsPotentiallyPlaying);
    }

    [Theory]
    [InlineData(false, true)]   // policy forbids playback
    [InlineData(true, false)]   // document forbids autoplay (sandbox or permissions policy)
    public void Autoplay_BlockedStaysPaused(bool allowedToPlay, bool documentAllows)
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData, h =>
        {
            h.HasAutoplayAttribute = true;
            h.IsAllowedToPlay = allowedToPlay;
            h.DocumentAllowsAutoplay = documentAllows;
        });

        Assert.True(element.Paused);
    }

    [Fact]
    public void Autoplay_DoesNotRunAfterAPauseCall()
    {
        var (host, element, _, _) = Create(h =>
        {
            h.SrcAttribute = "a.webm";
            h.HasAutoplayAttribute = true;
        });
        element.Load();
        host.Run();
        element.Pause(); // clears the can-autoplay flag
        host.Resource!.LoadMetadata();
        host.Resource.Client.ReadyStateChanged(MediaReadyState.HaveEnoughData);
        host.Run();

        Assert.True(element.Paused);
    }

    [Fact]
    public void Underflow_WhilePlaying_FiresTimeupdateAndWaiting()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        element.Play();
        host.Run();
        host.TakeEvents();

        host.Resource!.Client.ReadyStateChanged(MediaReadyState.HaveCurrentData);
        host.Run();

        Assert.Equal(["timeupdate", "waiting"], host.TakeEvents());
        Assert.False(element.IsPotentiallyPlaying);
        Assert.False(host.Resource.LastPlayback.Playing);

        host.Resource.Client.ReadyStateChanged(MediaReadyState.HaveEnoughData);
        host.Run();
        Assert.Equal(["canplay", "playing", "canplaythrough"], host.TakeEvents());
    }

    [Fact]
    public void Underflow_WhilePaused_IsSilent()
    {
        var (host, _, _) = Loaded(MediaReadyState.HaveEnoughData);
        host.Resource!.Client.ReadyStateChanged(MediaReadyState.HaveCurrentData);
        host.Run();
        Assert.Empty(host.TakeEvents());
    }

    [Fact]
    public void ReachingTheEnd_PausesAndFiresEnded()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        element.Play();
        var pending = AsPromise(element.Play());
        host.RunStableStates();

        host.Resource!.Client.PositionChanged(MediaTime.FromSeconds(10), monotonic: true);
        host.Resource.Client.ReachedEnd();
        host.Run();

        Assert.True(element.Ended);
        Assert.True(element.Paused);
        Assert.Equal(10, element.CurrentTime);
        var events = host.TakeEvents();
        Assert.Equal(["timeupdate", "pause", "ended"], events.Skip(events.Count - 3));
        Assert.NotEqual("pending", pending.State);
        Assert.False(host.Resource.LastPlayback.Playing);
    }

    [Fact]
    public void ReachingTheEnd_WithLoop_SeeksToTheStart()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData, h => h.HasLoopAttribute = true);
        element.Play();
        host.Run();
        host.TakeEvents();

        host.Resource!.Client.ReachedEnd();
        host.Run();

        Assert.False(element.Ended);
        Assert.False(element.Paused);
        Assert.True(element.Seeking);
        Assert.Equal(MediaTime.Zero, host.Resource.Seeks.Single().Target);
        Assert.Equal(["seeking"], host.TakeEvents());

        host.Resource.Client.SeekCompleted(MediaTime.Zero);
        host.Run();
        Assert.False(element.Seeking);
        Assert.Equal(["timeupdate", "seeked"], host.TakeEvents());
    }

    /// <summary>WPT playing-the-media-resource/loop-from-ended.tentative (whatwg/html#4487).</summary>
    [Fact]
    public void Play_AfterEndedWithLoopSetSince_RestartsFromTheBeginning()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        host.Resource!.Client.PositionChanged(MediaTime.FromSeconds(10), monotonic: false);
        host.Resource.Client.ReachedEnd();
        host.Run();
        Assert.True(element.Ended);
        Assert.True(element.Paused);
        host.TakeEvents();

        host.HasLoopAttribute = true;
        Assert.False(element.Ended);
        Assert.True(element.Paused);

        element.Play();
        host.Run();

        Assert.False(element.Paused);
        Assert.Equal(MediaTime.Zero, host.Resource.Seeks.Single().Target);
        Assert.Contains("seeking", host.TakeEvents());
    }

    [Fact]
    public void Play_AfterEnded_RestartsFromTheBeginning()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        host.Resource!.Client.PositionChanged(MediaTime.FromSeconds(10), monotonic: false);
        host.Resource.Client.ReachedEnd();
        host.Run();
        Assert.True(element.Ended);
        host.TakeEvents();

        element.Play();
        host.Run();

        Assert.False(element.Ended);
        Assert.Equal(MediaTime.Zero, host.Resource.Seeks.Single().Target);
        Assert.Equal(["seeking", "play", "playing"], host.TakeEvents());
    }

    [Fact]
    public void RemovedFromDocument_PausesUnlessReinserted()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        element.Play();
        host.Run();
        host.TakeEvents();

        element.OnRemovedFromDocument(() => true);
        host.Run();
        Assert.False(element.Paused);

        element.OnRemovedFromDocument(() => false);
        host.Run();
        Assert.True(element.Paused);
        Assert.Equal(["timeupdate", "pause"], host.TakeEvents());
    }

    [Fact]
    public void Timeupdate_DuringPlayback_IsThrottledTo250ms()
    {
        var (host, element, time) = Loaded(MediaReadyState.HaveEnoughData);
        element.Play();
        host.Run();
        host.TakeEvents();
        var client = host.Resource!.Client;

        for (int i = 1; i <= 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            client.PositionChanged(MediaTime.FromMicroseconds(i * 50_000), monotonic: true);
            host.Run();
        }

        // Updates every 50 ms for 1 s: timeupdate at 50, 300, 550 and 800 ms.
        Assert.Equal(4, host.TakeEvents().Count(e => e == "timeupdate"));
        Assert.Equal(1.0, element.CurrentTime);

        // A seek-style jump is not throttled by this rule and fires nothing by itself.
        client.PositionChanged(MediaTime.FromSeconds(3), monotonic: false);
        host.Run();
        Assert.Empty(host.TakeEvents());
    }

    [Fact]
    public void Played_AccumulatesNormalPlayback()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        element.Play();
        host.Resource!.Client.PositionChanged(MediaTime.FromSeconds(2), monotonic: true);
        host.Run();

        // A discontinuous jump closes the range.
        host.Resource.Client.PositionChanged(MediaTime.FromSeconds(5), monotonic: false);
        host.Resource.Client.PositionChanged(MediaTime.FromSeconds(6), monotonic: true);
        host.Run();

        var played = element.Played;
        Assert.Equal(2, played.Count);
        Assert.Equal((MediaTime.Zero, MediaTime.FromSeconds(2)), (played.Start(0), played.End(0)));
        Assert.Equal((MediaTime.FromSeconds(5), MediaTime.FromSeconds(6)), (played.Start(1), played.End(1)));
    }

    [Fact]
    public void DurationChange_ShorterThanPosition_Seeks()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        host.Resource!.Client.PositionChanged(MediaTime.FromSeconds(8), monotonic: false);
        host.Run();
        host.TakeEvents();

        host.Resource.Client.SeekableChanged(MediaTimeRanges.Single(MediaTime.Zero, MediaTime.FromSeconds(5)));
        host.Resource.Client.DurationChanged(MediaTime.FromSeconds(5));
        host.Run();

        Assert.Equal(5, element.Duration);
        Assert.Equal(["durationchange", "seeking"], host.TakeEvents());
        Assert.Equal(MediaTime.FromSeconds(5), host.Resource.Seeks.Single().Target);
    }

    [Fact]
    public void VideoSizeChange_FiresResize()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        int before = host.RenderInvalidations;

        host.Resource!.Client.VideoSizeChanged(1280, 720);
        host.Resource.Client.VideoSizeChanged(1280, 720);
        host.Run();

        Assert.Equal(["resize"], host.TakeEvents());
        Assert.Equal((1280, 720), (element.VideoWidth, element.VideoHeight));
        Assert.True(host.RenderInvalidations > before);
    }
}
