using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Network;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Media;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// HTML §4.8.11: the media element IDL surface answered by
/// <see cref="FenBrowser.Media.Element.HtmlMediaElementController"/> through the script
/// runtime. No demuxer is registered, so every source is unsupported and the observable
/// behaviour is the state machine's: states, events, promises and reflected attributes.
/// </summary>
[Collection("Media Engine State")]
public sealed class FenJsMediaElementTests
{
    [Fact]
    public async Task FreshVideoReportsTheInitialState()
    {
        var engine = await CreateEngineAsync("<html><body><video id=v></video></body></html>");

        var state = engine.Evaluate("""
            var v = document.getElementById('v');
            [v.networkState, v.readyState, v.paused, v.seeking, v.currentTime, String(v.duration),
             v.volume, v.muted, v.playbackRate, v.defaultPlaybackRate, String(v.error), v.currentSrc,
             v.buffered.length, v.videoWidth, v.videoHeight, v.NETWORK_EMPTY, v.HAVE_ENOUGH_DATA].join('|')
            """)?.ToString();

        Assert.Equal("0|0|true|false|0|NaN|1|false|1|1|null||0|0|0|0|4", state);
    }

    [Fact]
    public async Task SettingSrcRunsTheLoadAlgorithmSynchronously()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        // §4.8.11.5 resource selection: networkState becomes NETWORK_NO_SOURCE before
        // the algorithm awaits a stable state, so a script observes it right away.
        var state = engine.Evaluate("""
            var v = document.createElement('video');
            v.src = 'movie.webm';
            [v.networkState, v.src, v.currentSrc].join('|')
            """)?.ToString();

        Assert.Equal("3|https://example.test/movie.webm|", state);
    }

    [Fact]
    public async Task UnsupportedSourceFiresErrorWithSrcNotSupported()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        engine.Evaluate("""
            globalThis.__events = [];
            var v = document.createElement('video');
            v.addEventListener('error', function (e) {
                globalThis.__events.push('error:' + (e.target === v) + ':' + v.error.code + ':' + v.networkState + ':' + v.currentSrc);
            });
            v.addEventListener('loadstart', function () { globalThis.__events.push('loadstart'); });
            v.src = 'movie.webm';
            document.body.appendChild(v);
            """);

        var events = await WaitForAsync(engine, "globalThis.__events.length > 1 ? globalThis.__events.join(',') : ''");
        Assert.Equal("loadstart,error:true:4:3:https://example.test/movie.webm", events);
        Assert.Equal("4", engine.Evaluate("String(v.error.MEDIA_ERR_SRC_NOT_SUPPORTED)")?.ToString());
        Assert.Equal("true", engine.Evaluate("String(v.error === v.error)")?.ToString());
    }

    [Fact]
    public async Task ParserCreatedVideoWithSourceChildrenReportsFailureOnTheSource()
    {
        var engine = await CreateEngineAsync("""
            <html><body>
            <video id=v>
              <source id=s1 src="a.webm" type="video/webm" onerror="globalThis.__events.push('source-error:' + this.id)">
              <source id=s2 src="b.mp4" onerror="globalThis.__events.push('source-error:' + this.id)">
            </video>
            <script>
              globalThis.__events = [];
              document.getElementById('v').addEventListener('error', function () { globalThis.__events.push('media-error'); });
            </script>
            </body></html>
            """);

        var events = await WaitForAsync(engine, "globalThis.__events.length > 1 ? globalThis.__events.join(',') : ''");
        // Every source fails, then the element waits for another one: networkState stays
        // NETWORK_NO_SOURCE and no error is fired at the element itself (§4.8.11.5 step 9).
        Assert.Equal("source-error:s1,source-error:s2", events);
        Assert.Equal("3", engine.Evaluate("String(document.getElementById('v').networkState)")?.ToString());
    }

    [Fact]
    public async Task PlayReturnsAPromiseRejectedWithNotSupportedErrorAfterFailure()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        engine.Evaluate("""
            globalThis.__result = '';
            var v = document.createElement('audio');
            v.muted = true;  // allowed to play, so the failure is what rejects
            v.src = 'song.ogg';
            v.addEventListener('error', function () {
                var p = v.play();
                globalThis.__isPromise = p instanceof Promise;
                p.then(function () { globalThis.__result = 'resolved'; },
                       function (e) { globalThis.__result = e.name + ':' + (e instanceof DOMException); });
            });
            """);

        var result = await WaitForAsync(engine, "globalThis.__result");
        Assert.Equal("NotSupportedError:true", result);
        Assert.Equal("true", engine.Evaluate("String(globalThis.__isPromise)")?.ToString());
    }

    [Fact]
    public async Task PlayWithoutActivationIsRejectedWithNotAllowedErrorWhenAudible()
    {
        var previous = MediaAutoplayPolicy.Default.Mode;
        MediaAutoplayPolicy.Default.Mode = AutoplayPolicyMode.AllowedMuted;
        try
        {
            var engine = await CreateEngineAsync("<html><body></body></html>");
            engine.Evaluate("""
                globalThis.__result = '';
                var v = document.createElement('video');
                v.play().catch(function (e) { globalThis.__result = e.name; });
                var m = document.createElement('video');
                m.muted = true;
                globalThis.__mutedPaused = m.paused;
                m.play();
                globalThis.__mutedPlayRequested = !m.paused;
                """);

            var result = await WaitForAsync(engine, "globalThis.__result");
            Assert.Equal("NotAllowedError", result);
            Assert.Equal("true", engine.Evaluate("String(globalThis.__mutedPaused)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(globalThis.__mutedPlayRequested)")?.ToString());
        }
        finally
        {
            MediaAutoplayPolicy.Default.Mode = previous;
        }
    }

    [Fact]
    public async Task SettersValidateLikeTheSpec()
    {
        var engine = await CreateEngineAsync("<html><body><video id=v></video></body></html>");

        var results = engine.Evaluate("""
            var v = document.getElementById('v');
            var out = [];
            try { v.volume = 2; out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { v.playbackRate = -1; out.push('no-throw'); } catch (e) { out.push(e.name); }
            v.volume = 0.5; out.push(v.volume);
            v.playbackRate = 2; out.push(v.playbackRate);
            v.muted = true; out.push(v.muted);
            v.loop = true; out.push(v.hasAttribute('loop'));
            v.autoplay = false; out.push(v.autoplay);
            v.preload = 'bogus'; out.push(v.preload);
            v.crossOrigin = 'use-credentials'; out.push(v.crossOrigin);
            v.crossOrigin = 'nonsense'; out.push(v.crossOrigin);
            v.crossOrigin = null; out.push(String(v.crossOrigin));
            v.width = 320; out.push(v.width + ':' + v.getAttribute('width'));
            out.push(v.canPlayType('video/webm; codecs="vp9"') + ':' + v.canPlayType('text/html') + ':' + v.canPlayType('audio/wav'));
            out.join('|')
            """)?.ToString();

        Assert.Equal("IndexSizeError|NotSupportedError|0.5|2|true|true|false|auto|use-credentials|anonymous|null|320:320|probably::maybe", results);
    }

    [Fact]
    public async Task MediaMembersLiveOnTheInterfacePrototypes()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        var results = engine.Evaluate("""
            [
              'play' in HTMLMediaElement.prototype,
              'canPlayType' in HTMLMediaElement.prototype,
              'currentTime' in HTMLMediaElement.prototype,
              'videoWidth' in HTMLVideoElement.prototype,
              typeof Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'paused').get,
              document.createElement('audio') instanceof HTMLMediaElement,
              document.createElement('video') instanceof HTMLVideoElement,
              new Audio() instanceof HTMLAudioElement,
              new Audio('song.mp3').preload + ':' + new Audio('song.mp3').networkState + ':' + new Audio('song.mp3').src
            ].join('|')
            """)?.ToString();

        Assert.Equal("true|true|true|true|function|true|true|true|auto:3:https://example.test/song.mp3", results);
    }

    [Fact]
    public async Task SourcesAndSrcInAnotherNamespaceDoNotStartSelection()
    {
        var engine = await CreateEngineAsync("<html><body><video id=v></video></body></html>");

        var state = engine.Evaluate("""
            var v = document.getElementById('v');
            v.appendChild(document.createElementNS('bogus', 'source'));
            var afterForeignSource = v.networkState;
            v.setAttributeNS('bogus', 'src', 'movie.webm');
            var afterForeignSrc = v.networkState + ':' + v.getAttributeNS('bogus', 'src') + ':' + String(v.getAttributeNS(null, 'src'));
            v.removeAttributeNS('bogus', 'src');
            v.setAttributeNS(null, 'src', 'movie.webm');
            [afterForeignSource, afterForeignSrc, v.hasAttributeNS('bogus', 'src'), v.networkState].join('|')
            """)?.ToString();

        Assert.Equal("0|0:movie.webm:null|false|3", state);
    }

    [Fact]
    public async Task ErrorsAndRangesAreInstancesOfTheirInterfaces()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        engine.Evaluate("""
            globalThis.__result = '';
            var v = document.createElement('video');
            v.onerror = function () {
                globalThis.__result = [
                    v.error instanceof MediaError,
                    v.error.code === MediaError.MEDIA_ERR_SRC_NOT_SUPPORTED,
                    v.error.MEDIA_ERR_DECODE,
                    Object.prototype.toString.call(v.error),
                    v.buffered instanceof TimeRanges,
                    HTMLMediaElement.NETWORK_NO_SOURCE,
                    HTMLMediaElement.prototype.HAVE_ENOUGH_DATA,
                    (function () { try { new MediaError(); return 'constructed'; } catch (e) { return e.name; } })()
                ].join('|');
            };
            v.src = 'movie.webm';
            """);

        var result = await WaitForAsync(engine, "globalThis.__result");
        Assert.Equal("true|true|3|[object MediaError]|true|3|4|TypeError", result);
    }

    [Fact]
    public async Task SrcReflectsAsAUrl()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        var result = engine.Evaluate("""
            var v = document.createElement('video');
            var out = [v.src];
            v.src = ' ';   out.push(v.src, v.currentSrc === '' ? 'pending' : 'set');
            v.src = 'movie.webm'; out.push(v.src);
            v.src = 'http://[bad'; out.push(v.src);
            v.currentTime = Number.MAX_VALUE; out.push(v.currentTime === Number.MAX_VALUE);
            out.join('|')
            """)?.ToString();

        Assert.Equal("|https://example.test/|pending|https://example.test/movie.webm|http://[bad|true", result);
    }

    [Fact]
    public async Task NavigatorReportsTheAutoplayPolicy()
    {
        var previous = MediaAutoplayPolicy.Default.Mode;
        MediaAutoplayPolicy.Default.Mode = AutoplayPolicyMode.AllowedMuted;
        try
        {
            var engine = await CreateEngineAsync("<html><body></body></html>");
            var result = engine.Evaluate("""
                var v = document.createElement('video');
                var m = document.createElement('video'); m.muted = true;
                [
                  navigator.getAutoplayPolicy('mediaelement'),
                  navigator.getAutoplayPolicy('audiocontext'),
                  navigator.getAutoplayPolicy(v),
                  navigator.getAutoplayPolicy(m),
                  (function () { try { navigator.getAutoplayPolicy('bogus'); return 'no-throw'; } catch (e) { return e.name; } })()
                ].join('|')
                """)?.ToString();

            Assert.Equal("allowed-muted|allowed|allowed-muted|allowed|TypeError", result);
        }
        finally
        {
            MediaAutoplayPolicy.Default.Mode = previous;
        }
    }

    [Fact]
    public async Task MovingASourceIntoAVideoStartsSelection()
    {
        var engine = await CreateEngineAsync("<html><body><div id=holder><source src=a.webm></div><video id=v></video></body></html>");

        var result = engine.Evaluate("""
            var v = document.getElementById('v');
            var holder = document.getElementById('holder');
            var source = holder.firstChild;
            var out = [];
            // Not a video child: nothing starts.
            holder.moveBefore(source, undefined);
            out.push(v.networkState);
            // Into the video: the source is a candidate (§4.8.11.5), so selection starts.
            v.moveBefore(source, null);
            out.push(v.networkState, source.parentNode === v, holder.childNodes.length);
            // Validity: a foreign tree, an ancestor, a missing reference child.
            out.push((function () { try { v.moveBefore(document.createElement('source'), null); return 'no-throw'; } catch (e) { return e.name; } })());
            out.push((function () { try { v.moveBefore(document.body, null); return 'no-throw'; } catch (e) { return e.name; } })());
            out.push((function () { try { v.moveBefore(source, holder); return 'no-throw'; } catch (e) { return e.name; } })());
            out.push(typeof Node.prototype.moveBefore);
            out.join('|')
            """)?.ToString();

        Assert.Equal("0|3|true|0|HierarchyRequestError|HierarchyRequestError|NotFoundError|function", result);
    }

    [Fact]
    public async Task SelectionPointerWaitsOnTheNetworkWhileScriptsAddSources()
    {
        // The WPT "pointer updates" shape: the parser inserted three sources before the
        // script ran and the second one's fetch is still in flight, so the pointer sits
        // after it. Sources the script inserts before the pointer are never tried.
        var previous = MediaFetchResource.FetchDetailedAsync;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetched = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaFetchResource.FetchDetailedAsync = async (request, _) =>
        {
            fetched.TrySetResult(request.Url);
            await release.Task;
            return new BinaryFetchResult { FailureReason = BinaryFetchFailureReason.HttpError, StatusCode = 404, FinalUri = new Uri(request.Url) };
        };
        try
        {
            var engine = await CreateEngineAsync("""
                <html><body>
                <script>var a = 0, b = 0, c = 0, x1 = 0, x2 = 0, x3 = 0, x4 = 0;</script>
                <video><source onerror=a++><source onerror=b++ src='delayed.webm'><source onerror=c++></video>
                <script>
                  var video = document.querySelector('video');
                  globalThis.__stateBeforeInsert = video.networkState + ':' + a;
                  var source1 = document.createElement('source'); source1.onerror = function () { x1++; };
                  var source2 = document.createElement('source'); source2.onerror = function () { x2++; };
                  var source3 = document.createElement('source'); source3.onerror = function () { x3++; };
                  var source4 = document.createElement('source'); source4.onerror = function () { x4++; };
                  video.insertBefore(source1, video.querySelector('[onerror="a++"]'));
                  video.insertBefore(source2, video.querySelector('[onerror="b++"]'));
                  video.insertBefore(source3, video.querySelector('[onerror="c++"]'));
                  video.appendChild(source4);
                </script>
                </body></html>
                """);

            Assert.Equal("https://example.test/delayed.webm", await fetched.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            // The fetch of the second source is in flight (NETWORK_LOADING); the first
            // source's error is a queued task and has not fired inside the script yet.
            Assert.Equal("2:0", engine.Evaluate("globalThis.__stateBeforeInsert")?.ToString());

            release.SetResult(true);
            var counts = await WaitForAsync(engine, "x4 === 1 ? [a, b, c, x1, x2, x3, x4].join(',') : ''");
            Assert.Equal("1,1,1,0,0,1,1", counts);
        }
        finally
        {
            MediaFetchResource.FetchDetailedAsync = previous;
        }
    }

    [Fact]
    public async Task TheLoadEventWaitsForAMediaFetch()
    {
        var previous = MediaFetchResource.FetchDetailedAsync;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaFetchResource.FetchDetailedAsync = async (request, _) =>
        {
            await release.Task;
            return new BinaryFetchResult { FailureReason = BinaryFetchFailureReason.HttpError, StatusCode = 404, FinalUri = new Uri(request.Url) };
        };
        try
        {
            var engine = await CreateEngineAsync("""
                <html><body>
                <video src="slow.webm" onerror="globalThis.__order.push('error')"></video>
                <script>
                  globalThis.__order = [];
                  document.addEventListener('DOMContentLoaded', function () { globalThis.__order.push('DOMContentLoaded:' + document.readyState); });
                  window.addEventListener('load', function () { globalThis.__order.push('load:' + document.readyState); });
                </script>
                </body></html>
                """);

            // §4.8.11.5: the fetch delays the load event, so after startup only
            // DOMContentLoaded has fired and the document is still interactive.
            Assert.Equal("DOMContentLoaded:interactive", engine.Evaluate("globalThis.__order.join(',')")?.ToString());
            Assert.Equal("interactive", engine.Evaluate("document.readyState")?.ToString());

            release.SetResult(true);
            var order = await WaitForAsync(engine, "globalThis.__order.length === 3 ? globalThis.__order.join(',') : ''");
            Assert.Equal("DOMContentLoaded:interactive,error,load:complete", order);
        }
        finally
        {
            MediaFetchResource.FetchDetailedAsync = previous;
        }
    }

    [Theory]
    [InlineData("sine_pcm16.wav", "audio/wav", "1.000")]
    [InlineData("sine_mp3_id3.mp3", "audio/mpeg", "1.045")]
    [InlineData("sine_opus.ogg", "audio/ogg", "1.000")]
    public async Task AnAudioResourcePlaysThroughTheElement(string file, string contentType, string duration)
    {
        var previousFetcher = MediaFetchResource.FetchDetailedAsync;
        var previousMode = MediaAutoplayPolicy.Default.Mode;
        MediaAutoplayPolicy.Default.Mode = AutoplayPolicyMode.Allowed;
        var bytes = File.ReadAllBytes(Path.Combine(FindTestAssets(), "media", file));
        MediaFetchResource.FetchDetailedAsync = (request, _) => Task.FromResult(new BinaryFetchResult
        {
            Body = bytes,
            StatusCode = 200,
            FinalUri = new Uri(request.Url),
            ContentType = contentType,
        });
        try
        {
            var engine = await CreateEngineAsync("<html><body></body></html>");
            engine.Evaluate("""
                globalThis.__events = [];
                var a = document.createElement('audio');
                ['loadstart', 'loadedmetadata', 'loadeddata', 'canplay', 'canplaythrough', 'play', 'playing', 'ended', 'error', 'seeking', 'seeked', 'durationchange'].forEach(function (t) {
                    a.addEventListener(t, function () { globalThis.__events.push(t + (t === 'ended' ? ':' + a.currentTime.toFixed(2) + ':' + a.paused : '')); });
                });
                var updates = 0;
                a.addEventListener('timeupdate', function () { updates++; });
                a.src = 'resource';
                a.play().then(function () { globalThis.__events.push('play-resolved:' + a.readyState + ':' + a.duration.toFixed(3)); },
                              function (e) { globalThis.__events.push('play-rejected:' + e.name); });
                """);

            var events = await WaitForAsync(engine, "globalThis.__events.some(function (e) { return e.indexOf('ended:') === 0 || e.indexOf('error') === 0 || e.indexOf('play-rejected') === 0; }) ? globalThis.__events.join(',') : ''");
            // play() queued its event before the load algorithm's stable state queued
            // loadstart; then the readiness cascade (§4.8.11.7): canplay, playing and the
            // resolved promise at HAVE_FUTURE_DATA, canplaythrough at HAVE_ENOUGH_DATA.
            var ended = "ended:" + duration.Substring(0, 4) + ":true";
            Assert.Equal(
                $"play,loadstart,durationchange,loadedmetadata,loadeddata,canplay,playing,play-resolved:4:{duration},canplaythrough,{ended}",
                events);
            Assert.True(int.Parse(engine.Evaluate("String(updates)")?.ToString() ?? "0") >= 2, "timeupdate should have fired during playback");
            Assert.Equal("false|1|4|0", engine.Evaluate("[a.seeking, a.buffered.length, a.readyState, a.buffered.start(0)].join('|')")?.ToString());
        }
        finally
        {
            MediaFetchResource.FetchDetailedAsync = previousFetcher;
            MediaAutoplayPolicy.Default.Mode = previousMode;
        }
    }

    [Theory]
    [InlineData("pattern_vp9.webm", "video/webm", 0)]
    [InlineData("pattern_vp8_vorbis.webm", "video/webm", 1)]
    [InlineData("pattern_av1.webm", "video/webm", 0)]
    public async Task AVideoResourcePlaysThroughTheElement(string file, string contentType, int audioTracks)
    {
        var previousFetcher = MediaFetchResource.FetchDetailedAsync;
        var previousMode = MediaAutoplayPolicy.Default.Mode;
        MediaAutoplayPolicy.Default.Mode = AutoplayPolicyMode.Allowed;
        var bytes = File.ReadAllBytes(Path.Combine(FindTestAssets(), "media", file));
        MediaFetchResource.FetchDetailedAsync = (request, _) => Task.FromResult(new BinaryFetchResult
        {
            Body = bytes,
            StatusCode = 200,
            FinalUri = new Uri(request.Url),
            ContentType = contentType,
        });
        try
        {
            var baseUri = new Uri("https://example.test/");
            var document = new HtmlParser("<html><body><video id='v'></video></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
            await engine.SetDomAsync(document.DocumentElement, baseUri);
            engine.Evaluate("""
                globalThis.__events = [];
                var v = document.getElementById('v');
                ['loadedmetadata', 'resize', 'loadeddata', 'canplay', 'playing', 'ended', 'error'].forEach(function (t) {
                    v.addEventListener(t, function () { globalThis.__events.push(t + (t === 'resize' ? ':' + v.videoWidth + 'x' + v.videoHeight : '')); });
                });
                v.src = 'resource';
                v.play().then(function () { globalThis.__events.push('play-resolved'); }, function (e) { globalThis.__events.push('play-rejected:' + e.name); });
                """);

            var events = await WaitForAsync(engine, "globalThis.__events.some(function (e) { return e === 'ended' || e === 'error' || e.indexOf('play-rejected') === 0; }) ? globalThis.__events.join(',') : ''");
            // §4.8.11.5 "once enough of the media data has been fetched": the video size and
            // its resize come before readyState reaches HAVE_METADATA.
            Assert.Equal("resize:64x48,loadedmetadata,loadeddata,canplay,playing,play-resolved,ended", events);
            Assert.Equal("64|48|1.00", engine.Evaluate("[v.videoWidth, v.videoHeight, v.duration.toFixed(2)].join('|')")?.ToString());

            // Media Playback Quality: every decoded picture is counted, few are dropped.
            var quality = engine.Evaluate("(function () { var q = v.getVideoPlaybackQuality(); return [q.totalVideoFrames, q.droppedVideoFrames <= 3, q.corruptedVideoFrames, typeof q.creationTime].join('|'); })()")?.ToString();
            Assert.Equal("10|true|0|number", quality);
            Assert.Equal("true", engine.Evaluate("String(HTMLVideoElement.prototype.hasOwnProperty('getVideoPlaybackQuality'))")?.ToString());

            // The element's presentation carries a picture for paint, no poster.
            var element = Assert.IsType<FenBrowser.Core.Dom.V2.Element>(document.GetElementById("v"));
            var state = FenBrowser.FenEngine.Media.MediaPresentation.Get(element);
            Assert.False(state.ShowPoster);
            Assert.Equal(64, state.VideoWidth);
            Assert.NotNull(state.Presenter);
            Assert.True(state.Presenter.Sequence >= 5, $"only {state.Presenter.Sequence} pictures reached the presenter");
            var picture = state.Presenter.Acquire();
            Assert.NotNull(picture);
            Assert.Equal(64, picture.Width);
            Assert.Equal(48, picture.Height);
            picture.Release();
            _ = audioTracks;
        }
        finally
        {
            MediaFetchResource.FetchDetailedAsync = previousFetcher;
            MediaAutoplayPolicy.Default.Mode = previousMode;
        }
    }

    private static string FindTestAssets()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "test_assets");
            if (Directory.Exists(Path.Combine(candidate, "media")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("test_assets/media not found above " + AppContext.BaseDirectory);
    }

    private static async Task<string> WaitForAsync(FenJsBrowserScriptEngine engine, string expression)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 5000)
        {
            var value = engine.Evaluate(expression)?.ToString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            await Task.Delay(25);
        }

        return engine.Evaluate(expression)?.ToString() ?? string.Empty;
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string html)
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
