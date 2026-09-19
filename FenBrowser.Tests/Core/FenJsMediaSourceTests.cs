using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Media;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// Media Source Extensions through script: the MediaSource, SourceBuffer and
/// SourceBufferList objects, attaching through a blob URL, the append and remove event
/// sequences, endOfStream, and playback of what was appended.
/// </summary>
[Collection("Media Engine State")]
public sealed class FenJsMediaSourceTests
{
    [Fact]
    public async Task InterfacesValidateTheirArguments()
    {
        var engine = await CreateEngineAsync("<html><body></body></html>");

        var result = engine.Evaluate("""
            var out = [];
            out.push(typeof MediaSource, typeof SourceBuffer, typeof SourceBufferList);
            var ms = new MediaSource();
            out.push(ms instanceof EventTarget, ms.readyState, isNaN(ms.duration), ms.sourceBuffers.length, ms.activeSourceBuffers.length, ms.sourceBuffers instanceof SourceBufferList);
            out.push(MediaSource.isTypeSupported('video/webm; codecs="vp9"'), MediaSource.isTypeSupported('video/webm'), MediaSource.isTypeSupported('video/mp4; codecs="avc1.4d001e"'), MediaSource.isTypeSupported('audio/webm; codecs="vp8"'), MediaSource.isTypeSupported('text/html'));
            try { ms.addSourceBuffer('video/webm; codecs="vp9"'); out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { ms.addSourceBuffer(''); out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { ms.addSourceBuffer('video/bogus'); out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { ms.endOfStream(); out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { ms.endOfStream('bogus'); out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { ms.duration = 5; out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { ms.duration = -1; out.push('no-throw'); } catch (e) { out.push(e.name); }
            try { new SourceBuffer(); out.push('no-throw'); } catch (e) { out.push(e.name); }
            out.push(Object.prototype.toString.call(ms), MediaSource.canConstructInDedicatedWorker);
            out.join('|')
            """)?.ToString();

        Assert.Equal(
            "function|function|function|" +
            "true|closed|true|0|0|true|" +
            "true|false|true|false|false|" +
            "InvalidStateError|TypeError|NotSupportedError|InvalidStateError|TypeError|InvalidStateError|TypeError|TypeError|" +
            "[object MediaSource]|false",
            result);
    }

    /// <summary>
    /// MSE §11 ManagedMediaSource: a MediaSource that says when it wants data. streaming
    /// (with startstreaming) once attached and open with nothing buffered ahead; the 1 s
    /// fixture leaves it wanting more; endOfStream closes the request (endstreaming).
    /// </summary>
    [Fact]
    public async Task ManagedMediaSource_SaysWhenItWantsData()
    {
        var bytes = File.ReadAllBytes(Path.Combine(FindTestAssets(), "media", "pattern_vp9.webm"));
        var engine = await CreateEngineAsync("<html><body><video id=v></video></body></html>");
        engine.Evaluate($$"""
            globalThis.__events = [];
            var v = document.getElementById('v');
            var ms = new ManagedMediaSource();
            globalThis.__events.push('type:' + (ms instanceof MediaSource) + ':' + (ms instanceof ManagedMediaSource) + ':' + ms.streaming + ':' + Object.prototype.toString.call(ms) + ':' + ManagedMediaSource.canConstructInDedicatedWorker + ':' + (typeof ManagedMediaSource.isTypeSupported));
            var bytes = Uint8Array.from(atob('{{Convert.ToBase64String(bytes)}}'), function (c) { return c.charCodeAt(0); });
            ms.addEventListener('startstreaming', function () { globalThis.__events.push('startstreaming:' + ms.streaming); });
            ms.addEventListener('endstreaming', function () { globalThis.__events.push('endstreaming:' + ms.streaming); });
            ms.addEventListener('sourceopen', function () {
                var sb = ms.addSourceBuffer('video/webm; codecs="vp9"');
                sb.addEventListener('updateend', function () {
                    globalThis.__events.push('appended:' + ms.streaming);
                    ms.endOfStream();
                });
                sb.appendBuffer(bytes);
            });
            ms.addEventListener('sourceended', function () { globalThis.__events.push('sourceended:' + ms.streaming); });
            v.src = URL.createObjectURL(ms);
            """);

        var events = await WaitForAsync(engine, "globalThis.__events.some(function (e) { return e.indexOf('sourceended') === 0; }) ? globalThis.__events.join(',') : ''");
        var actual = events.Split(',').ToList();
        Assert.Equal("type:true:true:false:[object ManagedMediaSource]:false:function", actual[0]);
        Assert.Contains("startstreaming:true", actual);
        Assert.Contains("appended:true", actual);
        Assert.Contains("endstreaming:false", actual);
        Assert.True(actual.IndexOf("startstreaming:true") < actual.IndexOf("endstreaming:false"), events);
    }

    [Fact]
    public async Task AttachAppendEndOfStreamAndPlay()
    {
        var previousMode = MediaAutoplayPolicy.Default.Mode;
        MediaAutoplayPolicy.Default.Mode = AutoplayPolicyMode.Allowed;
        var bytes = File.ReadAllBytes(Path.Combine(FindTestAssets(), "media", "pattern_vp9.webm"));
        try
        {
            var engine = await CreateEngineAsync("<html><body><video id=v></video></body></html>");
            engine.Evaluate($$"""
                globalThis.__events = [];
                var v = document.getElementById('v');
                var ms = new MediaSource();
                var bytes = Uint8Array.from(atob('{{Convert.ToBase64String(bytes)}}'), function (c) { return c.charCodeAt(0); });
                ['loadedmetadata', 'durationchange', 'ended'].forEach(function (type) {
                    v.addEventListener(type, function () { globalThis.__events.push(type + (type === 'ended' ? ':' + v.currentTime.toFixed(1) + ':' + v.buffered.length + ':' + v.buffered.end(0).toFixed(1) + ':' + v.duration : '')); });
                });
                ms.addEventListener('sourceended', function () { globalThis.__events.push('sourceended:' + ms.readyState + ':' + ms.duration); });
                ms.addEventListener('sourceclose', function () { globalThis.__events.push('sourceclose:' + ms.readyState + ':' + ms.sourceBuffers.length); });
                ms.addEventListener('sourceopen', function () {
                    globalThis.__events.push('sourceopen:' + ms.readyState + ':' + (v.networkState) + ':' + v.readyState);
                    var sb = ms.addSourceBuffer('video/webm; codecs="vp9"');
                    globalThis.__events.push('added:' + ms.sourceBuffers.length + ':' + (ms.sourceBuffers[0] === sb) + ':' + sb.mode + ':' + sb.updating + ':' + sb.timestampOffset + ':' + sb.appendWindowEnd);
                    ms.sourceBuffers.addEventListener('addsourcebuffer', function () { globalThis.__events.push('addsourcebuffer'); });
                    ms.activeSourceBuffers.addEventListener('addsourcebuffer', function () { globalThis.__events.push('active:' + ms.activeSourceBuffers.length); });
                    ['updatestart', 'update', 'updateend', 'error', 'abort'].forEach(function (type) {
                        sb.addEventListener(type, function () { globalThis.__events.push(type + ':' + sb.updating); });
                    });
                    sb.addEventListener('updateend', function onEnd() {
                        sb.removeEventListener('updateend', onEnd);
                        globalThis.__events.push('buffered:' + sb.buffered.length + ':' + sb.buffered.start(0) + ':' + sb.buffered.end(0).toFixed(1));
                        globalThis.__events.push('tracks:' + sb.videoTracks.length + ':' + v.videoTracks.length + ':' + (v.videoTracks[0].sourceBuffer === sb));
                        try { sb.appendBuffer(bytes); sb.abort(); globalThis.__events.push('aborted:' + sb.updating); } catch (e) { globalThis.__events.push('abort-threw:' + e.name); }
                        ms.endOfStream();
                        globalThis.__events.push('eos:' + ms.readyState + ':' + ms.duration);
                        v.play();
                    });
                    globalThis.__events.push('append:' + (sb.appendBuffer(bytes), sb.updating));
                });
                v.src = URL.createObjectURL(ms);
                """);

            var events = await WaitForAsync(engine, "globalThis.__events.some(function (e) { return e.indexOf('ended') === 0; }) ? globalThis.__events.join(',') : ''");
            var actual = events.Split(',').ToList();
            Assert.Equal("sourceopen:open:2:0", actual[0]);
            Assert.Equal("added:1:true:segments:false:0:Infinity", actual[1]);
            Assert.Equal("append:true", actual[2]);
            Assert.Contains("addsourcebuffer", actual);
            Assert.Contains("updatestart:true", actual);
            Assert.Contains("update:false", actual);
            Assert.Contains("updateend:false", actual);
            Assert.Contains("active:1", actual);
            Assert.Contains("buffered:1:0:1.0", actual);
            Assert.Contains("tracks:1:1:true", actual);
            Assert.Contains("aborted:false", actual);
            Assert.Contains("abort:false", actual);
            Assert.Contains("eos:ended:1", actual);
            Assert.Contains("sourceended:ended:1", actual);
            Assert.Contains("loadedmetadata", actual);
            Assert.Contains("ended:1.0:1:1.0:1", actual);
            // The aborted append's queued updatestart lands before its abort and updateend.
            var abortedStart = actual.LastIndexOf("updatestart:false");
            Assert.True(abortedStart > 0 && actual.IndexOf("abort:false") > abortedStart, events);
            Assert.DoesNotContain("error:false", actual);

            // Loading something else detaches: the source closes and loses its buffers.
            engine.Evaluate("document.getElementById('v').src = 'about:blank'; void 0;");
            var closed = await WaitForAsync(engine, "globalThis.__events.some(function (e) { return e.indexOf('sourceclose') === 0; }) ? globalThis.__events.join(',') : ''");
            Assert.Contains("sourceclose:closed:0", closed.Split(','));
        }
        finally
        {
            MediaAutoplayPolicy.Default.Mode = previousMode;
        }
    }

    [Fact]
    public async Task AppendErrorEndsTheStreamWithADecodeError()
    {
        var engine = await CreateEngineAsync("<html><body><video id=v></video></body></html>");
        engine.Evaluate("""
            globalThis.__events = [];
            var v = document.getElementById('v');
            var ms = new MediaSource();
            v.addEventListener('error', function () { globalThis.__events.push('element-error:' + (v.error && v.error.code)); });
            ms.addEventListener('sourceopen', function () {
                var sb = ms.addSourceBuffer('video/mp4; codecs="avc1.4d001e"');
                ['updatestart', 'update', 'updateend', 'error', 'abort'].forEach(function (type) {
                    sb.addEventListener(type, function () { globalThis.__events.push(type); });
                });
                sb.addEventListener('updateend', function () { globalThis.__events.push('state:' + ms.readyState); });
                sb.appendBuffer(Uint8Array.from('this is not an mp4 at all, it is text', function (c) { return c.charCodeAt(0); }));
            });
            v.src = URL.createObjectURL(ms);
            """);

        var events = await WaitForAsync(engine, "globalThis.__events.some(function (e) { return e.indexOf('element-error') === 0; }) ? globalThis.__events.join(',') : ''");
        Assert.Equal("updatestart,error,updateend,state:ended,element-error:4", events);
    }

    private static string FindTestAssets()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "test_assets");
            if (Directory.Exists(Path.Combine(candidate, "media")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("test_assets/media not found above " + AppContext.BaseDirectory);
    }

    private static async Task<string> WaitForAsync(FenJsBrowserScriptEngine engine, string expression)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 8000)
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
            Sandbox = SandboxPolicy.AllowAll,
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
