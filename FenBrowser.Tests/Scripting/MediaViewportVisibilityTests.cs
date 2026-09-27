using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Network;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Media;
using FenBrowser.FenEngine.Scripting;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// The viewport half of the media background policy (MEDIA_ENGINE_DESIGN section 5): a
/// media element whose box is not in the viewport counts as not being shown, and a muted
/// video that started itself is paused while it is out of sight.
/// </summary>
[Collection("Media Engine State")]
public sealed class MediaViewportVisibilityTests
{
    [Fact]
    public async Task AMutedAutoplayingVideoScrolledOutOfSightIsPausedAndPlaysAgainOnTheWayBack()
    {
        var previousFetcher = MediaFetchResource.FetchDetailedAsync;
        var previousMode = MediaAutoplayPolicy.Default.Mode;
        MediaAutoplayPolicy.Default.Mode = AutoplayPolicyMode.Allowed;
        var bytes = File.ReadAllBytes(Path.Combine(FindTestAssets(), "media", "pattern_vp8_vorbis.webm"));
        MediaFetchResource.FetchDetailedAsync = (request, _) => Task.FromResult(new BinaryFetchResult
        {
            Body = bytes,
            StatusCode = 200,
            FinalUri = new Uri(request.Url),
            ContentType = "video/webm",
        });
        try
        {
            var engine = await CreateEngineAsync("<html><body><video id=v autoplay muted loop src=\"clip.webm\"></video></body></html>");

            // A 320x240 box at the top of a 1024x768 viewport: on screen.
            double boxTop = 0;
            engine.WindowWidth = 1024;
            engine.WindowHeight = 768;
            engine.LayoutBoxResolver = _ => new BoxModel { BorderBox = SKRect.Create(0, (float)boxTop, 320, 240) };

            engine.Evaluate("""
                globalThis.__events = [];
                var v = document.getElementById('v');
                ['play', 'pause', 'playing'].forEach(function (t) {
                    v.addEventListener(t, function () { globalThis.__events.push(t); });
                });
                """);
            Assert.Equal("playing", await WaitForAsync(engine, "globalThis.__events.indexOf('playing') >= 0 ? 'playing' : ''"));
            Assert.Equal("false", engine.Evaluate("String(v.paused)")?.ToString());

            // The page scrolls past it.
            boxTop = 5000;
            engine.ScheduleMediaViewportCheck();
            Assert.Equal("true", await WaitForAsync(engine, "v.paused ? 'true' : ''"));
            Assert.Contains("pause", engine.Evaluate("globalThis.__events.join(',')")?.ToString());

            // And back.
            boxTop = 100;
            engine.ScheduleMediaViewportCheck();
            Assert.Equal("false", await WaitForAsync(engine, "v.paused ? '' : 'false'"));
        }
        finally
        {
            MediaFetchResource.FetchDetailedAsync = previousFetcher;
            MediaAutoplayPolicy.Default.Mode = previousMode;
        }
    }

    /// <summary>
    /// An element with no box stops decoding but is never paused: during load a box can
    /// be a frame away, and a pause/play pair for it would be visible to the page.
    /// </summary>
    [Fact]
    public async Task AVideoWithNoBoxIsNotPaused()
    {
        var previousFetcher = MediaFetchResource.FetchDetailedAsync;
        var previousMode = MediaAutoplayPolicy.Default.Mode;
        MediaAutoplayPolicy.Default.Mode = AutoplayPolicyMode.Allowed;
        var bytes = File.ReadAllBytes(Path.Combine(FindTestAssets(), "media", "pattern_vp8_vorbis.webm"));
        MediaFetchResource.FetchDetailedAsync = (request, _) => Task.FromResult(new BinaryFetchResult
        {
            Body = bytes,
            StatusCode = 200,
            FinalUri = new Uri(request.Url),
            ContentType = "video/webm",
        });
        try
        {
            var engine = await CreateEngineAsync("<html><body><video id=v autoplay muted loop src=\"clip.webm\"></video></body></html>");
            engine.WindowWidth = 1024;
            engine.WindowHeight = 768;

            // No layout at all: the realm cannot tell, so the element counts as shown and
            // an autoplaying video is never paused for it.
            Assert.Equal("playing", await WaitForAsync(engine, "document.getElementById('v').paused ? '' : 'playing'"));
            engine.ScheduleMediaViewportCheck();
            await Task.Delay(100);
            Assert.Equal("false", engine.Evaluate("String(document.getElementById('v').paused)")?.ToString());

            // Layout that has no box for the element: not rendered, so it stops decoding
            // pictures, but nothing about its playback changes.
            engine.LayoutBoxResolver = _ => null;
            engine.ScheduleMediaViewportCheck();
            await Task.Delay(200);
            Assert.Equal("false", engine.Evaluate("String(document.getElementById('v').paused)")?.ToString());
        }
        finally
        {
            MediaFetchResource.FetchDetailedAsync = previousFetcher;
            MediaAutoplayPolicy.Default.Mode = previousMode;
        }
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string html)
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser(html, baseUri).Parse();
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

    private static async Task<string> WaitForAsync(FenJsBrowserScriptEngine engine, string expression)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 10000)
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

        throw new DirectoryNotFoundException("test_assets/media was not found above the test output directory.");
    }
}
