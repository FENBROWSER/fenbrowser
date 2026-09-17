using FenBrowser.Media.Element;
using static FenBrowser.Media.Tests.Element.MediaElementTestKit;

namespace FenBrowser.Media.Tests.Element;

// Randomized sequences of script calls, tree changes and pipeline reports. After every step
// the element must satisfy the invariants HTML §4.8.11 implies; the fake host additionally
// fails the test if a promise is ever settled twice.
public class MediaElementRandomizedTests
{
    private const int Steps = 4_000;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RandomOperations_KeepTheInvariants(int seed)
    {
        var random = new Random(seed);
        var (host, element, _, _) = Create(h => h.SrcAttribute = "start.webm");
        var promises = new List<FakePromise>();
        var trace = new List<string>();
        bool sawPlaying = false, sawSeeking = false, sawEnded = false, sawError = false;

        for (int step = 0; step < Steps; step++)
        {
            string op = Apply(random, host, element, promises);
            trace.Add(op);
            if (trace.Count > 12)
                trace.RemoveAt(0);

            if (random.Next(3) == 0)
                host.Run();
            else
                host.RunStableStates();

            try
            {
                CheckInvariants(host, element, promises);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"seed {seed}, step {step}: {ex.Message}\nlast operations: {string.Join(" > ", trace)}", ex);
            }

            sawPlaying |= element.IsPotentiallyPlaying;
            sawSeeking |= element.Seeking;
            sawEnded |= element.Ended;
            sawError |= element.Error is not null;
        }

        // The walk must reach the interesting states, or the invariants were barely exercised.
        Assert.True(sawPlaying && sawSeeking && sawEnded && sawError,
            $"coverage: playing={sawPlaying} seeking={sawSeeking} ended={sawEnded} error={sawError}");
        Assert.Contains(promises, p => p.State == "resolved");
        Assert.Contains(promises, p => p.State is not ("resolved" or "pending"));

        // After a final pause and a drain, nothing may be left pending.
        element.Pause();
        host.Run();
        Assert.All(promises, p => Assert.NotEqual("pending", p.State));
    }

    private static string Apply(Random random, FakeMediaElementHost host, HtmlMediaElementController element, List<FakePromise> promises)
    {
        var client = host.Resource?.Client;
        switch (random.Next(22))
        {
            case 0:
                element.Load();
                return "load";
            case 1:
                host.SrcAttribute = random.Next(4) switch { 0 => null, 1 => "", 2 => "::bad", _ => "v.webm" };
                element.OnSrcAttributeSet();
                return $"src={host.SrcAttribute ?? "(none)"}";
            case 2:
            case 3:
                promises.Add(AsPromise(element.Play()));
                return "play";
            case 4:
                element.Pause();
                return "pause";
            case 5:
                element.SetCurrentTime(random.NextDouble() * 14 - 2);
                return "currentTime";
            case 6:
                element.TrySetVolume(random.NextDouble() * 1.4 - 0.2);
                element.SetMuted(random.Next(2) == 0);
                return "volume/muted";
            case 7:
                element.TrySetPlaybackRate(random.Next(3) switch { 0 => 0, 1 => 2, _ => -1 });
                return "rate";
            case 8:
            {
                var source = host.AddSource("s" + host.Children.Count, random.Next(3) == 0 ? "" : "x.webm");
                element.OnChildInserted(source);
                return "append source";
            }

            case 9 when host.Children.Count > 0:
            {
                int index = random.Next(host.Children.Count);
                var node = host.Children[index];
                var previous = index > 0 ? host.Children[index - 1] : null;
                host.Children.RemoveAt(index);
                element.OnChildRemoved(node, previous);
                return "remove child";
            }

            case 10:
                host.IsAllowedToPlay = random.Next(8) != 0;
                host.HasAutoplayAttribute = random.Next(2) == 0;
                host.HasLoopAttribute = random.Next(4) == 0;
                return "policy";
            case 11 when client is not null:
                host.Resource!.LoadMetadata(random.Next(4) == 0 ? double.PositiveInfinity : 10);
                return "metadata";
            case 12 when client is not null:
                client.ReadyStateChanged((MediaReadyState)random.Next(5));
                return "readyState";
            case 13 when client is not null:
                client.Failed((MediaResourceFailure)random.Next(4), "random failure");
                return "failed";
            case 14 when client is not null:
                client.PositionChanged(MediaTime.FromSeconds(random.NextDouble() * 10), random.Next(2) == 0);
                return "position";
            case 15 when client is not null:
                client.ReachedEnd();
                return "end";
            case 16 when client is not null && host.Resource!.Seeks.Count > 0:
                client.SeekCompleted(host.Resource.Seeks[^1].Target);
                return "seek done";
            case 17 when client is not null:
                client.DurationChanged(MediaTime.FromSeconds(random.Next(1, 20)));
                return "duration";
            case 18 when client is not null:
                switch (random.Next(5))
                {
                    case 0: client.Progress(); break;
                    case 1: client.Suspended(); break;
                    case 2: client.Resumed(); break;
                    case 3: client.Stalled(); break;
                    default: client.FetchedEntirely(); break;
                }

                return "network";
            case 19 when client is not null:
                client.SeekableChanged(random.Next(3) == 0
                    ? MediaTimeRanges.Empty
                    : MediaTimeRanges.Single(MediaTime.Zero, MediaTime.FromSeconds(random.Next(1, 20))));
                return "seekable";
            case 20:
                element.OnRemovedFromDocument(() => random.Next(2) == 0);
                return "removed";
            default:
                host.Run();
                return "run";
        }
    }

    private static void CheckInvariants(FakeMediaElementHost host, HtmlMediaElementController element, List<FakePromise> promises)
    {
        // An empty element has no data.
        if (element.NetworkState == MediaNetworkState.Empty && element.ReadyState != MediaReadyState.HaveNothing)
            throw new InvalidOperationException($"networkState EMPTY with readyState {element.ReadyState}");

        // Without data there is no duration, no video size, no seeking, and nothing has ended.
        if (element.ReadyState == MediaReadyState.HaveNothing)
        {
            if (!double.IsNaN(element.Duration))
                throw new InvalidOperationException("duration is set while HAVE_NOTHING");
            if (element.Seeking)
                throw new InvalidOperationException("seeking while HAVE_NOTHING");
            if (element.Ended)
                throw new InvalidOperationException("ended while HAVE_NOTHING");
        }

        // Potentially playing implies not paused, not ended, and at least HAVE_FUTURE_DATA.
        if (element.IsPotentiallyPlaying && (element.Paused || element.Ended || element.ReadyState < MediaReadyState.HaveFutureData))
            throw new InvalidOperationException("potentially playing in an impossible state");

        // Attribute ranges.
        if (!(element.Volume >= 0 && element.Volume <= 1))
            throw new InvalidOperationException($"volume {element.Volume}");
        if (!HtmlMediaElementController.IsSupportedPlaybackRate(element.PlaybackRate))
            throw new InvalidOperationException($"playbackRate {element.PlaybackRate}");
        if (double.IsNaN(element.CurrentTime))
            throw new InvalidOperationException("currentTime is NaN");
        if (element.EffectiveVolume != (element.Muted ? 0 : element.Volume))
            throw new InvalidOperationException("effective volume disagrees with muted/volume");

        // A playing resource has only ever been told it plays while the element says so.
        if (host.Resource is { Disposed: false } resource && resource.LastPlayback.Playing && !element.IsPotentiallyPlaying)
            throw new InvalidOperationException("resource plays while the element is not potentially playing");

        // Only the newest resource may be alive.
        if (host.Resources.Take(Math.Max(0, host.Resources.Count - 1)).Any(r => !r.Disposed))
            throw new InvalidOperationException("an abandoned resource was not disposed");

        // A paused element resolves no promise after it paused: every promise is settled at most once (checked by the host).
        _ = promises;
    }
}
