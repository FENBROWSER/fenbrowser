using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering.Interaction;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// CSSOM View 4 and 7 for the top-level Window. scrollX/scrollY/pageXOffset/
/// pageYOffset and scroll/scrollTo/scrollBy were only installed in frame realms -
/// window.scrollTo(0, 0) threw on every top-level page - and a viewport scroll the
/// host performed never reached script as a scroll event.
/// </summary>
public sealed class ViewportScrollTests
{
    private sealed class Viewport
    {
        public double X;
        public double Y;
        public int Writes;
    }

    private static async Task<(FenJsBrowserScriptEngine Engine, Viewport Viewport)> CreateAsync()
    {
        var viewport = new Viewport();
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll,
            FrameScrollReader = element => element == null ? (viewport.X, viewport.Y) : (0d, 0d),
            FrameScrollWriter = (element, x, y) =>
            {
                if (element == null)
                {
                    viewport.X = x;
                    viewport.Y = y;
                    viewport.Writes++;
                }
            }
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return (engine, viewport);
    }

    [Fact]
    public async Task ScrollMembers_ExistOnTheTopLevelWindow()
    {
        var (engine, _) = await CreateAsync();

        Assert.Equal(
            "function|function|function|number|number|number|number",
            engine.Evaluate("[typeof scrollTo, typeof scrollBy, typeof window.scroll, typeof scrollX, typeof scrollY, typeof pageXOffset, typeof pageYOffset].join('|')")?.ToString());
    }

    [Fact]
    public async Task ScrollToAndScrollBy_MoveTheViewport_AndReadsAreLive()
    {
        var (engine, viewport) = await CreateAsync();

        engine.Evaluate("window.scrollTo(0, 300);");
        Assert.Equal(300d, viewport.Y);
        Assert.Equal("300|300", engine.Evaluate("scrollY + '|' + pageYOffset")?.ToString());

        engine.Evaluate("window.scrollBy({ top: 50 });");
        Assert.Equal(350d, viewport.Y);

        engine.Evaluate("window.scroll({ top: -20, left: 0 });");
        Assert.Equal(0d, viewport.Y);

        viewport.Y = 120;
        Assert.Equal("120", engine.Evaluate("String(window.scrollY)")?.ToString());
    }

    [Fact]
    public async Task ScrollY_IsReplaceable()
    {
        var (engine, _) = await CreateAsync();

        Assert.Equal("7", engine.Evaluate("window.scrollY = 7; String(window.scrollY)")?.ToString());
    }

    [Fact]
    public async Task ViewportScroll_FiresScrollAtTheDocumentAndTheWindow()
    {
        var (engine, viewport) = await CreateAsync();
        engine.Evaluate(
            "window.__log = [];" +
            "document.addEventListener('scroll', function (e) { __log.push('document:' + e.bubbles); });" +
            "window.addEventListener('scroll', function () { __log.push('window:' + scrollY); });");

        viewport.Y = 40;
        engine.NotifyViewportScrollChanged();
        engine.NotifyViewportScrollChanged();

        var watch = Stopwatch.StartNew();
        string log = string.Empty;
        while (watch.ElapsedMilliseconds < 5000)
        {
            log = engine.Evaluate("__log.join(',')")?.ToString() ?? string.Empty;
            if (log.Contains("window", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(20);
        }

        // Two notifications before delivery coalesce into one event.
        Assert.Equal("document:true,window:40", log);
    }

    [Fact]
    public void ScrollManager_ReportsViewportMovesOnly()
    {
        var manager = new ScrollManager();
        manager.SetScrollBounds(null, 800, 4000, 800, 600);
        var moves = 0;
        manager.ViewportScrolled += () => moves++;

        manager.SetScrollPosition(null, 0, 100);
        manager.SetScrollPosition(null, 0, 100);
        manager.SetScrollPosition(new FenBrowser.Core.Dom.V2.Element("div"), 0, 50);

        Assert.Equal(1, moves);
    }
}
