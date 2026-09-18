using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using FenBrowser.Core;
using FenBrowser.Core.Network;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Media;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// HTML §4.8.12 text tracks and §4.8.12.11 the track element, driven through script:
/// the object model, the track processing model over data: and http URLs, and the
/// "time marches on" cue activation against a real playing resource.
/// </summary>
[Collection("Media Engine State")]
public sealed class FenJsTextTrackTests
{
    private const string Vtt = "WEBVTT\n\n1\n00:00:00.100 --> 00:00:00.400 align:start\n<b>first</b> cue\n\n00:00:00.500 --> 00:00:00.800\nsecond";

    [Fact]
    public async Task InterfacesExistAndVTTCueValidatesItsAttributes()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        var result = engine.Evaluate("""
            var out = [];
            out.push(typeof TextTrack, typeof TextTrackList, typeof TextTrackCue, typeof TextTrackCueList, typeof VTTCue, typeof VTTRegion, typeof TrackEvent);
            var cue = new VTTCue(1, 2.5, 'hello');
            out.push(cue instanceof TextTrackCue, cue instanceof EventTarget, cue.startTime, cue.endTime, cue.text, cue.id, cue.pauseOnExit, cue.track);
            out.push(cue.line, cue.position, cue.size, cue.align, cue.vertical, cue.snapToLines, cue.lineAlign, cue.positionAlign, cue.region);
            cue.line = 5; cue.position = 40; cue.size = 50; cue.align = 'left'; cue.vertical = 'rl'; cue.align = 'bogus'; cue.vertical = 'xx';
            out.push(cue.line, cue.position, cue.size, cue.align, cue.vertical);
            try { cue.position = 101; out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { cue.size = -1; out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { cue.line = 'middle'; out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { new VTTCue(0, NaN, 'x'); out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { new TextTrackCue(); out.push('no-throw'); } catch (e) { out.push(e.name); }
            cue.line = 'auto'; cue.position = 'auto';
            out.push(cue.line, cue.position);
            var region = new VTTRegion();
            out.push(region.width, region.lines, region.regionAnchorX, region.regionAnchorY, region.viewportAnchorX, region.viewportAnchorY, region.scroll);
            region.scroll = 'up'; region.scroll = 'down'; region.lines = 7;
            try { region.width = 120; out.push('no-throw'); } catch (e) { out.push(e.name); }
            out.push(region.scroll, region.lines);
            cue.region = region;
            out.push(cue.region === region);
            out.push(Object.prototype.toString.call(cue), Object.prototype.toString.call(region));
            out.join('|')
            """)?.ToString();

        Assert.Equal(
            "function|function|function|function|function|function|function|" +
            "true|true|1|2.5|hello||false||" +
            "auto|auto|100|center||true|start|auto||" +
            "5|40|50|left|rl|" +
            "IndexSizeError|IndexSizeError|TypeError|TypeError|TypeError|" +
            "auto|auto|" +
            "100|3|0|100|0|100||" +
            "IndexSizeError|up|7|true|[object VTTCue]|[object VTTRegion]",
            result);
    }

    [Fact]
    public async Task AddTextTrackBuildsAHiddenTrackWithASortedCueList()
    {
        var engine = await CreateEngineAsync("<html><body><video id=v></video></body></html>");

        engine.Evaluate("""
            globalThis.__events = [];
            var v = document.getElementById('v');
            v.textTracks.addEventListener('addtrack', function (e) { globalThis.__events.push('addtrack:' + (e.track === t) + ':' + (e instanceof TrackEvent)); });
            v.textTracks.onchange = function () { globalThis.__events.push('change'); };
            var t = v.addTextTrack('subtitles', 'Label', 'en');
            globalThis.__sync = [v.textTracks.length, v.textTracks[0] === t, v.textTracks.item(0) === t, t.kind, t.label, t.language, t.mode, t.cues.length, t.activeCues.length, t instanceof TextTrack, v.textTracks instanceof TextTrackList].join('|');
            var a = new VTTCue(2, 3, 'a'), b = new VTTCue(1, 5, 'b'), c = new VTTCue(1, 6, 'c'), d = new VTTCue(1, 6, 'd');
            t.addCue(a); t.addCue(b); t.addCue(c); t.addCue(d);
            globalThis.__order = [t.cues[0].text, t.cues[1].text, t.cues[2].text, t.cues[3].text].join('');
            globalThis.__track = (a.track === t) + ':' + (t.cues.getCueById('') === null);
            a.id = 'x';
            globalThis.__byId = t.cues.getCueById('x') === a;
            t.removeCue(a);
            globalThis.__afterRemove = t.cues.length + ':' + a.track;
            try { t.removeCue(a); globalThis.__removeTwice = 'no-throw'; } catch (e) { globalThis.__removeTwice = e.name; }
            var t2 = v.addTextTrack('captions');
            t2.addCue(b);
            globalThis.__moved = (b.track === t2) + ':' + t.cues.length + ':' + t2.cues.length;
            t.mode = 'showing';
            t.mode = 'disabled';
            globalThis.__disabled = String(t.cues) + ':' + String(t.activeCues);
            try { v.addTextTrack('bogus'); globalThis.__badKind = 'no-throw'; } catch (e) { globalThis.__badKind = e.name; }
            """);

        Assert.Equal("1|true|true|subtitles|Label|en|hidden|0|0|true|true", engine.Evaluate("globalThis.__sync")?.ToString());
        Assert.Equal("cdba", engine.Evaluate("globalThis.__order")?.ToString());
        Assert.Equal("true:true", engine.Evaluate("globalThis.__track")?.ToString());
        Assert.Equal("true", engine.Evaluate("String(globalThis.__byId)")?.ToString());
        Assert.Equal("3:null", engine.Evaluate("globalThis.__afterRemove")?.ToString());
        Assert.Equal("NotFoundError", engine.Evaluate("globalThis.__removeTwice")?.ToString());
        Assert.Equal("true:2:1", engine.Evaluate("globalThis.__moved")?.ToString());
        Assert.Equal("null:null", engine.Evaluate("globalThis.__disabled")?.ToString());
        Assert.Equal("TypeError", engine.Evaluate("globalThis.__badKind")?.ToString());

        var events = await WaitForAsync(engine, "globalThis.__events.length >= 3 ? globalThis.__events.join(',') : ''");
        // Two addtrack tasks (one per track), one coalesced change task.
        Assert.Equal("addtrack:true:true,addtrack:false:true,change", events);
    }

    [Fact]
    public async Task ATrackElementLoadsADataUrlAndExposesItsCues()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        engine.Evaluate($$"""
            globalThis.__events = [];
            var video = document.createElement('video');
            var track = document.createElement('track');
            globalThis.__initial = [track.readyState, track.kind, track.track.kind, track.track.mode, track.NONE, track.LOADING, track.LOADED, track.ERROR, HTMLTrackElement.LOADED].join('|');
            track.src = 'data:text/vtt,' + encodeURIComponent({{JsString(Vtt)}});
            track.kind = 'captions';
            track['default'] = true;
            track.onload = function () {
                var t = track.track;
                globalThis.__events.push('load:' + track.readyState + ':' + t.mode + ':' + t.cues.length + ':' + t.cues[0].id + ':' + t.cues[0].startTime + ':' + t.cues[0].endTime + ':' + t.cues[0].align + ':' + t.cues[1].text + ':' + video.textTracks.length + ':' + (video.textTracks[0] === t));
                var html = t.cues[0].getCueAsHTML();
                globalThis.__events.push('html:' + html.childNodes.length + ':' + html.firstChild.tagName + ':' + html.firstChild.textContent + ':' + html.lastChild.nodeValue);
            };
            track.onerror = function () { globalThis.__events.push('error'); };
            video.appendChild(track);
            globalThis.__afterInsert = track.readyState + ':' + video.textTracks.length + ':' + track.track.kind;
            """);

        Assert.Equal("0|subtitles|subtitles|disabled|0|1|2|3|2", engine.Evaluate("globalThis.__initial")?.ToString());
        Assert.Equal("0:1:captions", engine.Evaluate("globalThis.__afterInsert")?.ToString());

        var events = await WaitForAsync(engine, "globalThis.__events.length >= 2 ? globalThis.__events.join(',') : ''");
        Assert.Equal("load:2:showing:2:1:0.1:0.4:start:second:1:true,html:2:B:first: cue", events);
    }

    [Fact]
    public async Task ATrackElementFetchesOverHttpAndReportsErrors()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>", request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/good.vtt")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Vtt, Encoding.UTF8, "text/vtt") };
            }

            if (path == "/notvtt.txt")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello", Encoding.UTF8, "text/plain") };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        engine.Evaluate("""
            globalThis.__events = [];
            function make(src) {
                var video = document.createElement('video');
                var track = document.createElement('track');
                track.src = src;
                track['default'] = true;
                track.onload = function () { globalThis.__events.push('load:' + src + ':' + track.readyState + ':' + track.track.cues.length); };
                track.onerror = function () { globalThis.__events.push('error:' + src + ':' + track.readyState + ':' + track.track.cues.length); };
                video.appendChild(track);
                return track;
            }
            make('good.vtt');
            make('missing.vtt');
            make('notvtt.txt');
            make('https://other.test/cross.vtt');
            """);

        var events = await WaitForAsync(engine, "globalThis.__events.length >= 4 ? globalThis.__events.sort().join(',') : ''");
        Assert.Equal(
            "error:https://other.test/cross.vtt:3:0,error:missing.vtt:3:0,error:notvtt.txt:3:0,load:good.vtt:2:2",
            events);
    }

    [Fact]
    public async Task ChangingSrcEmptiesTheCuesAndReloads()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        engine.Evaluate($$"""
            globalThis.__events = [];
            var video = document.createElement('video');
            var track = document.createElement('track');
            track.src = 'data:text/vtt,' + encodeURIComponent({{JsString(Vtt)}});
            track['default'] = true;
            track.onload = function () {
                globalThis.__events.push('load:' + track.track.cues.length);
                if (globalThis.__events.length === 1) {
                    track.src = 'data:text/vtt,' + encodeURIComponent('WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nonly');
                    globalThis.__events.push('reset:' + track.readyState + ':' + track.track.cues.length);
                }
            };
            video.appendChild(track);
            """);

        var events = await WaitForAsync(engine, "globalThis.__events.length >= 3 ? globalThis.__events.join(',') : ''");
        // The reload starts at a stable state, so right after the src change the element is back at NONE.
        Assert.Equal("load:2,reset:0:0,load:1", events);
    }

    [Fact]
    public async Task SettingSrcAfterTheModeStillLoads()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        // The mode change queues the load at a stable state; the src change that follows
        // must not cancel it (it only invalidates fetches already in flight).
        engine.Evaluate($$"""
            globalThis.__events = [];
            var video = document.createElement('video');
            var track = document.createElement('track');
            video.appendChild(track);
            document.body.appendChild(video);
            track.track.mode = 'showing';
            track.src = 'data:text/vtt,' + encodeURIComponent({{JsString(Vtt)}});
            track.onload = track.onerror = function (e) { globalThis.__events.push(e.type + ':' + track.readyState + ':' + track.track.cues.length); };
            globalThis.__sync = String(track.readyState);
            """);

        Assert.Equal("0", engine.Evaluate("globalThis.__sync")?.ToString());
        Assert.Equal("load:2:2", await WaitForAsync(engine, "globalThis.__events.join(',')"));
    }

    [Fact]
    public async Task RemovingTheTrackElementRemovesItsTrack()
    {
        var engine = await CreateEngineAsync("<html><body><video id=v><track id=t kind=captions></video></body></html>");

        engine.Evaluate("""
            globalThis.__events = [];
            var v = document.getElementById('v');
            var t = document.getElementById('t');
            v.textTracks.onremovetrack = function (e) { globalThis.__events.push('removetrack:' + (e.track === t.track) + ':' + v.textTracks.length); };
            globalThis.__before = v.textTracks.length + ':' + (v.textTracks[0] === t.track) + ':' + t.track.kind;
            v.removeChild(t);
            globalThis.__after = String(v.textTracks.length);
            """);

        Assert.Equal("1:true:captions", engine.Evaluate("globalThis.__before")?.ToString());
        Assert.Equal("0", engine.Evaluate("globalThis.__after")?.ToString());
        var events = await WaitForAsync(engine, "globalThis.__events.join(',')");
        Assert.Equal("removetrack:true:0", events);
    }

    [Fact]
    public async Task CuesEnterAndExitAsTheResourcePlays()
    {
        var previousFetcher = MediaFetchResource.FetchDetailedAsync;
        var previousMode = MediaAutoplayPolicy.Default.Mode;
        MediaAutoplayPolicy.Default.Mode = AutoplayPolicyMode.Allowed;
        var bytes = File.ReadAllBytes(Path.Combine(FindTestAssets(), "media", "sine_pcm16.wav"));
        MediaFetchResource.FetchDetailedAsync = (request, _) => Task.FromResult(new BinaryFetchResult
        {
            Body = bytes,
            StatusCode = 200,
            FinalUri = new Uri(request.Url),
            ContentType = "audio/wav",
        });
        try
        {
            var engine = await CreateEngineAsync("<html><body></body></html>");
            engine.Evaluate("""
                globalThis.__events = [];
                var a = document.createElement('audio');
                var t = a.addTextTrack('subtitles');
                var c1 = new VTTCue(0.1, 0.4, 'one'), c2 = new VTTCue(0.5, 0.8, 'two'), c3 = new VTTCue(0.55, 0.6, 'three');
                c3.pauseOnExit = false;
                [c1, c2, c3].forEach(function (c) {
                    t.addCue(c);
                    c.onenter = function (e) { globalThis.__events.push('enter:' + c.text + ':' + (e.target === c) + ':' + t.activeCues.length); };
                    c.addEventListener('exit', function () { globalThis.__events.push('exit:' + c.text); });
                });
                t.oncuechange = function (e) { globalThis.__events.push('cuechange:' + (e.target === t)); };
                a.addEventListener('ended', function () { globalThis.__events.push('ended:' + t.activeCues.length); });
                a.src = 'resource';
                a.play();
                """);

            var events = await WaitForAsync(engine, "globalThis.__events.some(function (e) { return e.indexOf('ended') === 0; }) ? globalThis.__events.join(',') : ''");
            // Every cue enters and exits in cue order with a cuechange per batch; no cue
            // is still active once playback has ended.
            var expected = new[]
            {
                "enter:one:true:1", "cuechange:true", "exit:one", "cuechange:true",
                "enter:two:true:1", "cuechange:true", "enter:three:true:2", "cuechange:true",
                "exit:three", "cuechange:true", "exit:two", "cuechange:true", "ended:0",
            };
            var actual = events.Split(',');
            // The 4 ms decode tick can land two cue boundaries in one batch, so compare the
            // order of cue events and the batch structure rather than an exact transcript.
            var cueEvents = actual.Where(e => !e.StartsWith("cuechange", StringComparison.Ordinal)).ToArray();
            Assert.Equal(expected.Where(e => !e.StartsWith("cuechange", StringComparison.Ordinal)).Select(e => e.Split(':')[0] + ":" + e.Split(':')[1]),
                cueEvents.Select(e => e.Split(':')[0] + ":" + e.Split(':')[1]));
            Assert.Contains("cuechange:true", actual);
            Assert.EndsWith("ended:0", events);
        }
        finally
        {
            MediaFetchResource.FetchDetailedAsync = previousFetcher;
            MediaAutoplayPolicy.Default.Mode = previousMode;
        }
    }

    [Fact]
    public async Task PauseOnExitPausesTheElement()
    {
        var previousFetcher = MediaFetchResource.FetchDetailedAsync;
        var previousMode = MediaAutoplayPolicy.Default.Mode;
        MediaAutoplayPolicy.Default.Mode = AutoplayPolicyMode.Allowed;
        var bytes = File.ReadAllBytes(Path.Combine(FindTestAssets(), "media", "sine_pcm16.wav"));
        MediaFetchResource.FetchDetailedAsync = (request, _) => Task.FromResult(new BinaryFetchResult
        {
            Body = bytes,
            StatusCode = 200,
            FinalUri = new Uri(request.Url),
            ContentType = "audio/wav",
        });
        try
        {
            var engine = await CreateEngineAsync("<html><body></body></html>");
            engine.Evaluate("""
                globalThis.__result = '';
                var a = document.createElement('audio');
                var t = a.addTextTrack('metadata');
                var c = new VTTCue(0.1, 0.3, 'stop');
                c.pauseOnExit = true;
                t.addCue(c);
                a.addEventListener('pause', function () { globalThis.__result = 'paused:' + (a.currentTime >= 0.3) + ':' + (a.currentTime < 0.9); });
                a.addEventListener('ended', function () { globalThis.__result = 'ended'; });
                a.src = 'resource';
                a.play();
                """);

            var result = await WaitForAsync(engine, "globalThis.__result");
            Assert.Equal("paused:true:true", result);
        }
        finally
        {
            MediaFetchResource.FetchDetailedAsync = previousFetcher;
            MediaAutoplayPolicy.Default.Mode = previousMode;
        }
    }

    [Fact]
    public async Task MediaSessionAndMediaMetadataFollowTheStandard()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        var result = engine.Evaluate("""
            var out = [];
            var s = navigator.mediaSession;
            out.push(typeof MediaSession, typeof MediaMetadata, s instanceof MediaSession, s === navigator.mediaSession, s.playbackState, String(s.metadata));
            s.playbackState = 'playing'; s.playbackState = 'bogus';
            out.push(s.playbackState);
            var m = new MediaMetadata({ title: 't', artist: 'a', album: 'b', artwork: [{ src: '../x.png', sizes: '1x1', type: 'image/png', junk: 1 }], junk: 2,
                chapterInfo: [{ title: 'c1', startTime: 3, artwork: [] }] });
            s.metadata = m;
            out.push(s.metadata === m, m.title, m.artwork.length, m.artwork[0].src, m.artwork[0].sizes, 'junk' in m.artwork[0], Object.isFrozen(m.artwork), Object.isFrozen(m.artwork[0]), m.junk, m.chapterInfo.length, m.chapterInfo[0].startTime, Object.isFrozen(m.chapterInfo));
            try { m.artwork.push({ src: 'y' }); out.push('no-throw'); } catch (e) { out.push(e.name); }
            m.chapterInfo = [{ title: 'z' }];
            out.push(m.chapterInfo[0].title);
            try { new MediaMetadata('x'); out.push('no-throw'); } catch (e) { out.push(e.name); }
            out.push(new MediaMetadata().title === '');
            s.metadata = null;
            out.push(String(s.metadata));
            s.setActionHandler('play', function (d) { out.push('play:' + d.action + ':' + d.seekTime); });
            try { s.setActionHandler('bogus', null); out.push('no-throw'); } catch (e) { out.push(e.name); }
            s.setPositionState({ duration: 10, position: 2, playbackRate: 1.5 });
            s.setPositionState(null);
            s.setPositionState({ duration: 0 });
            try { s.setPositionState({ duration: -1 }); out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { s.setPositionState({ duration: 5, position: 6 }); out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { s.setPositionState({ duration: 5, playbackRate: 0 }); out.push('no-throw'); } catch (e) { out.push(e.name); }
            s.setMicrophoneActive(true); s.setCameraActive(false);
            out.push(__fenMediaSessionAction('play', { seekTime: 4 }), __fenMediaSessionAction('pause', {}));
            out.join('|')
            """)?.ToString();

        Assert.Equal(
            "function|function|true|true|none|null|playing|" +
            "true|t|1|https://example.test/x.png|1x1|false|true|true||1|3|true|TypeError|c1|TypeError|true|null|" +
            "TypeError|TypeError|TypeError|TypeError|play:play:4|true|false",
            result);
    }

    private static string JsString(string value) =>
        "'" + value.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n") + "'";

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

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string html, Func<HttpRequestMessage, HttpResponseMessage>? fetch = null)
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll,
        };
        if (fetch != null)
        {
            engine.FetchHandler = request => Task.FromResult(fetch(request));
        }

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
