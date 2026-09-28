using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Media.Capture;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// mediacapture-main 9-10: navigator.mediaDevices.getUserMedia() and enumerateDevices()
/// over <see cref="CaptureDevices"/>, driven with the fake microphone and camera that
/// FEN_MEDIA_FAKE_DEVICES selects, and §6's rules for a media element whose provider object
/// is a MediaStream.
/// </summary>
[Collection("Media Engine State")]
public sealed class FenJsUserMediaTests : IDisposable
{
    private readonly string? _previousFakeUi = Environment.GetEnvironmentVariable("FEN_MEDIA_FAKE_UI");

    public FenJsUserMediaTests()
    {
        CaptureDevices.Provider = new FakeCaptureDeviceProvider();
        Environment.SetEnvironmentVariable("FEN_MEDIA_FAKE_UI", "1");
    }

    public void Dispose()
    {
        CaptureDevices.Provider = NoCaptureDeviceProvider.Instance;
        Environment.SetEnvironmentVariable("FEN_MEDIA_FAKE_UI", _previousFakeUi);
    }

    [Fact]
    public async Task GetUserMediaReturnsALiveTrackPerRequestedKind()
    {
        var engine = await CreateEngineAsync();

        engine.Evaluate("""
            globalThis.__result = '';
            navigator.mediaDevices.getUserMedia({ audio: true, video: true }).then(function (s) {
                var a = s.getAudioTracks()[0], v = s.getVideoTracks()[0];
                globalThis.__stream = s;
                globalThis.__result = [s.getTracks().length, a.kind, a.label, a.readyState,
                    v.kind, v.label, v.getSettings().width, v.getSettings().height, s.active].join('|');
            }, function (e) { globalThis.__result = 'rejected:' + e.name; });
            """);

        Assert.Equal("2|audio|Fake Microphone|live|video|Fake Camera|640|480|true",
            await WaitForAsync(engine, "globalThis.__result"));

        // Stopping every track releases the devices and ends the stream.
        Assert.Equal("false|ended", engine.Evaluate("""
            __stream.getTracks().forEach(function (t) { t.stop(); });
            [__stream.active, __stream.getVideoTracks()[0].readyState].join('|')
            """)?.ToString());
    }

    [Fact]
    public async Task GetUserMediaRejectsBadRequestsWithTheSpecifiedErrors()
    {
        var engine = await CreateEngineAsync();

        engine.Evaluate("""
            globalThis.__results = [];
            function record(label, p) {
                p.then(function () { __results.push(label + ':resolved'); },
                       function (e) { __results.push(label + ':' + e.name + (e.constraint ? ':' + e.constraint : '')); });
            }
            record('none', navigator.mediaDevices.getUserMedia({}));
            record('width', navigator.mediaDevices.getUserMedia({ video: { width: { exact: 1920 } } }));
            record('device', navigator.mediaDevices.getUserMedia({ audio: { deviceId: { exact: 'nope' } } }));
            """);

        var results = await WaitForAsync(engine, "__results.length === 3 ? __results.sort().join(',') : ''");
        // 10.1 step 3: nothing requested is a TypeError; 5.2: an unmet exact constraint is
        // OverconstrainedError naming it.
        Assert.Equal("device:OverconstrainedError:deviceId,none:TypeError,width:OverconstrainedError:width", results);
    }

    [Fact]
    public async Task WithoutPermissionGetUserMediaIsNotAllowed()
    {
        Environment.SetEnvironmentVariable("FEN_MEDIA_FAKE_UI", null);
        var engine = await CreateEngineAsync();

        engine.Evaluate("""
            globalThis.__result = '';
            navigator.mediaDevices.getUserMedia({ audio: true })
                .then(function () { __result = 'resolved'; }, function (e) { __result = e.name; });
            """);

        Assert.Equal("NotAllowedError", await WaitForAsync(engine, "globalThis.__result"));
    }

    [Fact]
    public async Task WithoutDevicesGetUserMediaIsNotFound()
    {
        CaptureDevices.Provider = NoCaptureDeviceProvider.Instance;
        var engine = await CreateEngineAsync();

        engine.Evaluate("""
            globalThis.__result = '';
            navigator.mediaDevices.getUserMedia({ video: true })
                .then(function () { __result = 'resolved'; }, function (e) { __result = e.name; });
            """);

        Assert.Equal("NotFoundError", await WaitForAsync(engine, "globalThis.__result"));
    }

    [Fact]
    public async Task EnumerateDevicesHidesIdentifiersUntilCaptureStarts()
    {
        var engine = await CreateEngineAsync();

        engine.Evaluate("""
            globalThis.__before = ''; globalThis.__after = '';
            function inputs(list) {
                return list.filter(function (d) { return d.kind === 'audioinput' || d.kind === 'videoinput'; })
                           .map(function (d) { return d.kind + '=' + (d.label || '-') + '/' + (d.deviceId ? 'id' : '-'); })
                           .join(',');
            }
            navigator.mediaDevices.enumerateDevices().then(function (l) {
                __before = inputs(l);
                return navigator.mediaDevices.getUserMedia({ video: true });
            }).then(function () {
                return navigator.mediaDevices.enumerateDevices();
            }).then(function (l) { __after = inputs(l); });
            """);

        var after = await WaitForAsync(engine, "globalThis.__after");
        // 9.2.1: before capture each kind is listed once with no label or id; afterwards the
        // camera, the kind that was captured, is named.
        Assert.Equal("audioinput=-/-,videoinput=-/-", engine.Evaluate("globalThis.__before")?.ToString());
        Assert.Equal("audioinput=-/-,videoinput=Fake Camera/id", after);
    }

    [Fact]
    public async Task AMediaStreamProviderFixesRateAndIgnoresSeekingAndPreload()
    {
        var engine = await CreateEngineAsync();

        engine.Evaluate("""
            globalThis.__result = '';
            navigator.mediaDevices.getUserMedia({ video: true }).then(function (s) {
                var v = document.createElement('video');
                v.playbackRate = 2;
                v.srcObject = s;
                v.playbackRate = 3;
                v.defaultPlaybackRate = 3;
                v.preload = 'auto';
                v.currentTime = 5;
                // mediacapture-main 6: rates read 1, preload reads "none", currentTime is not seekable.
                __result = [v.playbackRate, v.defaultPlaybackRate, v.preload, v.currentTime].join('|');
            }, function (e) { __result = 'rejected:' + e.name; });
            """);

        Assert.Equal("1|1|none|0", await WaitForAsync(engine, "globalThis.__result"));
    }

    private static async Task<string> WaitForAsync(FenJsBrowserScriptEngine engine, string expression)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            var value = engine.Evaluate(expression)?.ToString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            await Task.Delay(20);
        }

        return engine.Evaluate(expression)?.ToString() ?? string.Empty;
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }
}
