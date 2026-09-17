using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Element;
using static FenBrowser.Media.Tests.Element.MediaElementTestKit;

namespace FenBrowser.Media.Tests.Element;

// HTML §4.8.11.5: the load algorithm, the resource selection algorithm and the
// failure paths of the resource fetch algorithm.
public class MediaElementLoadTests
{
    [Fact]
    public void InitialState_MatchesTheSpec()
    {
        var (_, element, _, _) = Create();

        Assert.Equal(MediaNetworkState.Empty, element.NetworkState);
        Assert.Equal(MediaReadyState.HaveNothing, element.ReadyState);
        Assert.True(element.Paused);
        Assert.False(element.Seeking);
        Assert.False(element.Ended);
        Assert.True(element.ShowPoster);
        Assert.Null(element.Error);
        Assert.Equal("", element.CurrentSrc);
        Assert.Equal(0, element.CurrentTime);
        Assert.True(double.IsNaN(element.Duration));
        Assert.Equal(1.0, element.Volume);
        Assert.Equal(1.0, element.PlaybackRate);
        Assert.Equal(1.0, element.DefaultPlaybackRate);
        Assert.True(element.PreservesPitch);
        Assert.False(element.Muted);
        Assert.Equal(0, element.Buffered.Count);
        Assert.Equal(0, element.Seekable.Count);
        Assert.Equal(0, element.Played.Count);
    }

    [Fact]
    public void Load_WithNothingToLoad_EndsEmptyWithoutEvents()
    {
        var (host, element, _, _) = Create();

        element.Load();
        Assert.Equal(MediaNetworkState.NoSource, element.NetworkState);
        Assert.True(host.Delaying);

        host.Run();

        Assert.Equal(MediaNetworkState.Empty, element.NetworkState);
        Assert.False(host.Delaying);
        Assert.Empty(host.TakeEvents());
    }

    [Fact]
    public void SrcAttribute_SelectsAndFetches()
    {
        var (host, element, log, _) = Create(h => h.SrcAttribute = "movie.webm");

        element.OnSrcAttributeSet();

        // Synchronous part: NO_SOURCE and delaying the load event, fetch waits for a stable state.
        Assert.Equal(MediaNetworkState.NoSource, element.NetworkState);
        Assert.Empty(host.Resources);

        host.RunStableStates();
        Assert.Equal(MediaNetworkState.Loading, element.NetworkState);
        Assert.Equal("https://example.test/movie.webm", element.CurrentSrc);
        var resource = Assert.Single(host.Resources);
        Assert.Equal("https://example.test/movie.webm", resource.Request.Url);
        Assert.True(resource.Request.IsVideo);
        Assert.False(resource.FullLoadRequested);

        host.Run();
        Assert.Equal(["loadstart"], host.TakeEvents());
        Assert.True(host.Delaying);
        Assert.Contains(log.Events, e => e.Kind == MediaEventKind.SourceSelected);
    }

    [Fact]
    public void FetchRequest_CarriesCrossOriginAndPreload()
    {
        var (host, element, _, _) = Create(h =>
        {
            h.SrcAttribute = "a.webm";
            h.IsVideo = false;
            h.CrossOriginAttribute = "use-credentials";
            h.PreloadAttribute = "none";
        });

        element.Load();
        host.Run();

        var request = host.Resource!.Request;
        Assert.False(request.IsVideo);
        Assert.Equal("use-credentials", request.CrossOrigin);
        Assert.Equal("none", request.Preload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("::bad")]
    public void BadSrc_RunsTheDedicatedFailureSteps(string src)
    {
        var (host, element, log, _) = Create(h => h.SrcAttribute = src);

        element.OnSrcAttributeSet();
        host.Run();

        Assert.Equal(["loadstart", "error"], host.TakeEvents());
        Assert.Equal(MediaErrorCode.SrcNotSupported, element.Error!.Code);
        Assert.Equal(MediaNetworkState.NoSource, element.NetworkState);
        Assert.True(element.ShowPoster);
        Assert.False(host.Delaying);
        Assert.Empty(host.Resources);
        Assert.Equal("", element.CurrentSrc);
        Assert.Contains(log.Events, e => e.Kind == MediaEventKind.Error && e.Level == MediaLogLevel.Warn);
    }

    [Fact]
    public void NoPipeline_IsTreatedAsUnsupported()
    {
        var (host, element, _, _) = Create(h =>
        {
            h.SrcAttribute = "movie.webm";
            h.ReturnNoResource = true;
        });

        element.Load();
        host.Run();

        Assert.Equal(["loadstart", "error"], host.TakeEvents());
        Assert.Equal(MediaErrorCode.SrcNotSupported, element.Error!.Code);
        Assert.Equal("https://example.test/movie.webm", element.CurrentSrc);
    }

    [Theory]
    [InlineData(MediaResourceFailure.Unsupported)]
    [InlineData(MediaResourceFailure.Network)]
    [InlineData(MediaResourceFailure.Decode)]
    public void FailureBeforeMetadata_WithAttribute_IsSrcNotSupported(MediaResourceFailure failure)
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "movie.webm");
        element.Load();
        host.Run();
        host.TakeEvents();

        host.Resource!.Client.Failed(failure, "nope");
        host.Run();

        Assert.Equal(["error"], host.TakeEvents());
        Assert.Equal(MediaErrorCode.SrcNotSupported, element.Error!.Code);
        Assert.True(host.Resource.Disposed);
        Assert.False(host.Delaying);
    }

    [Fact]
    public void Children_SkipUnusableCandidatesInOrder()
    {
        var (host, element, _, _) = Create();
        host.Children.Add(new FakeNode("text", isSource: false));
        var noSrc = host.AddSource("noSrc", src: null);
        var emptySrc = host.AddSource("emptySrc", src: "");
        var badUrl = host.AddSource("badUrl", src: "::bad");
        var wrongMedia = host.AddSource("wrongMedia", src: "a.webm", media: "(max-width: 1px)");
        var wrongType = host.AddSource("wrongType", src: "b.mov", type: "video/quicktime");
        host.AddSource("good", src: "c.webm", type: "video/webm; codecs=vp8");
        host.MediaMatches = q => q != "(max-width: 1px)";

        element.Load();
        host.Run();

        Assert.Equal(
            ["loadstart", $"error@{noSrc}", $"error@{emptySrc}", $"error@{badUrl}", $"error@{wrongMedia}", $"error@{wrongType}"],
            host.TakeEvents());
        Assert.Equal("https://example.test/c.webm", element.CurrentSrc);
        Assert.Single(host.Resources);
        Assert.Equal(MediaNetworkState.Loading, element.NetworkState);
        Assert.Null(element.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("application/octet-stream")]
    [InlineData("video/webm")]
    public void Children_TypesThatDoNotRuleOut(string type)
    {
        var (host, element, _, _) = Create();
        host.AddSource("s", src: "x.webm", type: type);

        element.Load();
        host.Run();

        Assert.Single(host.Resources);
    }

    [Fact]
    public void Children_FailedFetchMovesToTheNextSource()
    {
        var (host, element, _, _) = Create();
        var first = host.AddSource("first", src: "a.webm");
        host.AddSource("second", src: "b.webm");

        element.Load();
        host.Run();
        host.TakeEvents();

        host.Resources[0].Client.Failed(MediaResourceFailure.Unsupported, "not media");
        host.Run();

        Assert.Equal([$"error@{first}"], host.TakeEvents());
        Assert.Equal(2, host.Resources.Count);
        Assert.True(host.Resources[0].Disposed);
        Assert.Equal("https://example.test/b.webm", element.CurrentSrc);
        Assert.Null(element.Error);
    }

    [Fact]
    public void Children_AllFail_WaitsThenResumesWhenASourceIsAppended()
    {
        var (host, element, _, _) = Create();
        var only = host.AddSource("only", src: "");

        element.Load();
        host.Run();

        Assert.Equal(["loadstart", $"error@{only}"], host.TakeEvents());
        Assert.Equal(MediaNetworkState.NoSource, element.NetworkState);
        Assert.False(host.Delaying);
        Assert.Null(element.Error); // waiting is not an error

        var late = host.AddSource("late", src: "late.webm");
        element.OnChildInserted(late);
        host.Run();

        Assert.Equal(MediaNetworkState.Loading, element.NetworkState);
        Assert.True(host.Delaying);
        Assert.Equal("https://example.test/late.webm", element.CurrentSrc);
        Assert.Single(host.Resources);
    }

    [Fact]
    public void Children_NonSourceInsertionWhileWaiting_KeepsWaiting()
    {
        var (host, element, _, _) = Create();
        host.AddSource("only", src: "");
        element.Load();
        host.Run();
        host.TakeEvents();

        var div = new FakeNode("div", isSource: false);
        host.Children.Add(div);
        element.OnChildInserted(div);
        host.Run();

        Assert.Equal(MediaNetworkState.NoSource, element.NetworkState);
        Assert.Empty(host.Resources);

        var source = host.AddSource("next", src: "n.webm");
        element.OnChildInserted(source);
        host.Run();
        Assert.Single(host.Resources);
    }

    [Fact]
    public void Children_PointerSurvivesRemovalOfThePassedNode()
    {
        var (host, element, _, _) = Create();
        var a = host.AddSource("a", src: "");
        var b = host.AddSource("b", src: "");
        element.Load();
        host.Run();
        host.TakeEvents();

        // Remove the node just before the pointer; the pointer stays after "a".
        host.Children.Remove(b);
        element.OnChildRemoved(b, previousSibling: a);

        var c = host.AddSource("c", src: "c.webm");
        element.OnChildInserted(c);
        host.Run();

        Assert.Equal("https://example.test/c.webm", element.CurrentSrc);
    }

    [Fact]
    public void Children_InsertionBeforeThePointerIsNotRevisited()
    {
        var (host, element, _, _) = Create();
        var a = host.AddSource("a", src: "");
        element.Load();
        host.Run();
        host.TakeEvents();

        var early = new FakeNode("early", isSource: true, new() { ["src"] = "early.webm" });
        host.Children.Insert(0, early);
        element.OnChildInserted(early);
        host.Run();

        Assert.Empty(host.Resources);
        Assert.Equal(MediaNetworkState.NoSource, element.NetworkState);
        _ = a;
    }

    [Fact]
    public void SourceInsertedIntoEmptyElement_StartsSelection()
    {
        var (host, element, _, _) = Create();
        var source = host.AddSource("s", src: "a.webm");

        element.OnChildInserted(source);
        host.Run();

        Assert.Single(host.Resources);
        Assert.Equal(["loadstart"], host.TakeEvents());
    }

    [Fact]
    public void SourceInsertedWhenSrcIsSet_DoesNothing()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "a.webm");
        var source = host.AddSource("s", src: "b.webm");

        element.OnChildInserted(source);
        host.Run();

        Assert.Empty(host.Resources);
        Assert.Equal(MediaNetworkState.Empty, element.NetworkState);
    }

    [Fact]
    public void Reload_FiresAbortAndEmptied_AndIgnoresTheOldResource()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        var old = host.Resource!;
        Assert.Equal(MediaReadyState.HaveEnoughData, element.ReadyState);

        host.SrcAttribute = "other.webm";
        element.OnSrcAttributeSet();

        Assert.True(old.Disposed);
        Assert.Equal(MediaReadyState.HaveNothing, element.ReadyState);
        Assert.True(double.IsNaN(element.Duration));
        Assert.Equal(0, element.VideoWidth);

        // Late reports from the abandoned resource change nothing.
        old.Client.ReadyStateChanged(MediaReadyState.HaveEnoughData);
        old.Client.Failed(MediaResourceFailure.Decode, "late");
        host.Run();

        Assert.Equal(["abort", "emptied", "loadstart"], host.TakeEvents());
        Assert.Null(element.Error);
        Assert.Equal(MediaReadyState.HaveNothing, element.ReadyState);
        Assert.Equal("https://example.test/other.webm", element.CurrentSrc);
    }

    [Fact]
    public void Reload_DropsQueuedEventsOfTheOldLoad()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "a.webm");
        element.Load();
        host.RunStableStates(); // loadstart is queued, not yet fired

        element.Load();
        host.Run();

        // The first loadstart was discarded with the rest of the pending tasks.
        Assert.Equal(["abort", "emptied", "loadstart"], host.TakeEvents());
    }

    [Fact]
    public void Reload_WhilePlaying_RejectsPendingPlayWithAbortError()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "a.webm");
        element.Load();
        host.Run();
        var promise = AsPromise(element.Play());
        Assert.False(element.Paused);

        element.Load();

        Assert.Equal("AbortError", promise.State);
        Assert.True(element.Paused);
    }

    [Fact]
    public void Reload_SettlesPromisesOwnedByQueuedTasksImmediately()
    {
        // play() on a ready element hands its promise to a queued "playing" task.
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);
        var resolvedByTask = AsPromise(element.Play());
        Assert.Equal("pending", resolvedByTask.State);

        element.Load();

        // Load algorithm step 4: the task's promise is resolved now, not dropped.
        Assert.Equal("resolved", resolvedByTask.State);
        host.Run();
        Assert.DoesNotContain("playing", host.TakeEvents());
    }

    [Fact]
    public void Reload_RejectsPromisesOwnedByAQueuedPauseTask()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveMetadata);
        var rejectedByTask = AsPromise(element.Play());
        element.Pause(); // queues timeupdate + pause + reject

        element.Load();

        Assert.Equal("AbortError", rejectedByTask.State);
        host.Run();
        Assert.DoesNotContain("pause", host.TakeEvents());
    }

    [Fact]
    public void Reload_ResetsPlaybackRateAndError()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "");
        element.Load();
        host.Run();
        Assert.NotNull(element.Error);
        element.DefaultPlaybackRate = 0.5;
        Assert.True(element.TrySetPlaybackRate(2));

        host.SrcAttribute = "a.webm";
        element.OnSrcAttributeSet();

        Assert.Null(element.Error);
        Assert.Equal(0.5, element.PlaybackRate);
    }

    [Fact]
    public void NetworkErrorAfterMetadata_IsMediaErrNetwork()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveMetadata);

        host.Resource!.Client.Failed(MediaResourceFailure.Network, "connection reset");
        host.Run();

        Assert.Equal(["error"], host.TakeEvents());
        Assert.Equal(MediaErrorCode.Network, element.Error!.Code);
        Assert.Equal(MediaNetworkState.Idle, element.NetworkState);
        Assert.False(host.Delaying);
        Assert.True(host.Resource.Disposed);
        Assert.Equal(MediaReadyState.HaveMetadata, element.ReadyState);
    }

    [Fact]
    public void DecodeErrorAfterMetadata_IsMediaErrDecode()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveEnoughData);

        host.Resource!.Client.Failed(MediaResourceFailure.Decode, "corrupt frame");
        host.Run();

        Assert.Equal(["error"], host.TakeEvents());
        Assert.Equal(MediaErrorCode.Decode, element.Error!.Code);
        Assert.Equal("corrupt frame", element.Error.Message);
    }

    [Fact]
    public void UserAbortBeforeMetadata_EmptiesTheElement()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "a.webm");
        element.Load();
        host.Run();
        host.TakeEvents();

        host.Resource!.Client.Failed(MediaResourceFailure.AbortedByUser, "stopped");
        host.Run();

        Assert.Equal(["abort", "emptied"], host.TakeEvents());
        Assert.Equal(MediaErrorCode.Aborted, element.Error!.Code);
        Assert.Equal(MediaNetworkState.Empty, element.NetworkState);
        Assert.False(host.Delaying);
    }

    [Fact]
    public void UserAbortAfterMetadata_GoesIdle()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveMetadata);

        host.Resource!.Client.Failed(MediaResourceFailure.AbortedByUser, "stopped");
        host.Run();

        Assert.Equal(["abort"], host.TakeEvents());
        Assert.Equal(MediaNetworkState.Idle, element.NetworkState);
    }

    [Fact]
    public void NetworkActivityEvents()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "a.webm");
        element.Load();
        host.Run();
        host.TakeEvents();
        var client = host.Resource!.Client;

        client.Stalled();
        host.Run();
        Assert.True(element.IsCurrentlyStalled);

        client.Progress();
        host.Run();
        Assert.False(element.IsCurrentlyStalled);

        client.Suspended();
        host.Run();
        Assert.Equal(MediaNetworkState.Idle, element.NetworkState);

        client.Resumed();
        host.Run();
        Assert.Equal(MediaNetworkState.Loading, element.NetworkState);

        client.FetchedEntirely();
        host.Run();
        Assert.Equal(MediaNetworkState.Idle, element.NetworkState);

        Assert.Equal(["stalled", "progress", "suspend", "progress", "suspend"], host.TakeEvents());
    }

    [Fact]
    public void BufferedAndSeekable_AreReported()
    {
        var (host, element, _) = Loaded(MediaReadyState.HaveMetadata);
        var buffered = MediaTimeRanges.Single(MediaTime.Zero, MediaTime.FromSeconds(3));

        host.Resource!.Client.BufferedChanged(buffered);
        host.Run();

        Assert.Same(buffered, element.Buffered);
        Assert.Equal(1, element.Seekable.Count);
        Assert.Equal(MediaTime.FromSeconds(10), element.Seekable.End(0));
    }

    [Fact]
    public void CanPlayType_UsesTheRegistries()
    {
        var (_, element, _, _) = Create();
        Assert.Equal("probably", element.CanPlayType("video/webm; codecs=vp8"));
        Assert.Equal("maybe", element.CanPlayType("video/webm"));
        Assert.Equal("", element.CanPlayType("video/mp4"));
    }

    [Fact]
    public void MetadataBeforeReadyState_IsRequired()
    {
        var (host, element, _, _) = Create(h => h.SrcAttribute = "a.webm");
        element.Load();
        host.Run();

        host.Resource!.Client.ReadyStateChanged(MediaReadyState.HaveEnoughData);
        host.Run();

        Assert.Equal(MediaReadyState.HaveNothing, element.ReadyState);
    }
}
