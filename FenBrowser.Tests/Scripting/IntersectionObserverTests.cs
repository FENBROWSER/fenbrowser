using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Scripting;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// W3C Intersection Observer. The observer used to be a stub that reported every
/// target as fully visible with invented 100x100 rects and had no
/// IntersectionObserverEntry interface, so YouTube's polyfill (which feature-detects
/// 'intersectionRatio' in IntersectionObserverEntry.prototype) replaced it with a
/// polling version that never fired, and no search-result thumbnail loaded. Entries
/// now come from layout in the engine's intersection step (§3.2.8).
/// </summary>
public sealed class IntersectionObserverTests
{
    private const string Html =
        "<html><body><div id='visible'></div><div id='below'></div><div id='unlaid'></div></body></html>";

    private sealed class Page
    {
        public FenJsBrowserScriptEngine Engine = null!;
        public int Flushes;
        public int TargetFlushes;
        public Dictionary<Element, BoxModel> Boxes = new();
        public Document Document = null!;

        public Element Get(string id) => Assert.IsType<Element>(Document.GetElementById(id));
    }

    private static async Task<Page> CreatePageAsync()
    {
        var baseUri = new Uri("https://example.test/");
        var page = new Page { Document = new HtmlParser(Html, baseUri).Parse() };
        page.Boxes[page.Get("visible")] = Box(10, 20, 100, 50);
        page.Boxes[page.Get("below")] = Box(0, 5000, 100, 100);
        page.Engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            WindowWidth = 1280,
            WindowHeight = 800,
            FlushPendingLayout = element =>
            {
                page.Flushes++;
                if (element is Element e && e.HasAttribute("data-target")) page.TargetFlushes++;
            },
            LayoutBoxResolver = element => element is Element e && page.Boxes.TryGetValue(e, out var box) ? box : null
        };
        await page.Engine.SetDomAsync(page.Document.DocumentElement, baseUri);
        return page;
    }

    private static BoxModel Box(float x, float y, float width, float height)
    {
        var rect = new SKRect(x, y, x + width, y + height);
        return new BoxModel { BorderBox = rect, PaddingBox = rect, ContentBox = rect, MarginBox = rect };
    }

    private static async Task<string> WaitForAsync(FenJsBrowserScriptEngine engine, string expression)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
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

    [Theory]
    [InlineData("'intersectionRatio' in IntersectionObserverEntry.prototype && 'isIntersecting' in IntersectionObserverEntry.prototype && !Object.prototype.hasOwnProperty.call(new IntersectionObserverEntry({}), 'intersectionRatio')")]
    [InlineData("typeof DOMRectReadOnly === 'function' && new DOMRectReadOnly(1, 2, 3, 4).bottom === 6")]
    [InlineData("new IntersectionObserver(function () {}, { rootMargin: '10%' }).rootMargin === '10% 10% 10% 10%'")]
    [InlineData("new IntersectionObserver(function () {}, { rootMargin: '1px 2px' }).rootMargin === '1px 2px 1px 2px'")]
    [InlineData("new IntersectionObserver(function () {}, { threshold: [1, 0, 0.5] }).thresholds.join() === '0,0.5,1'")]
    [InlineData("new IntersectionObserver(function () {}).root === null")]
    [InlineData("(function () { try { new IntersectionObserver(function () {}, { threshold: 2 }); } catch (e) { return e instanceof RangeError; } })()")]
    [InlineData("(function () { try { new IntersectionObserver(function () {}, { rootMargin: '10em' }); } catch (e) { return e.name === 'SyntaxError'; } })()")]
    [InlineData("(function () { try { new IntersectionObserver(5); } catch (e) { return e instanceof TypeError; } })()")]
    [InlineData("(function () { try { new IntersectionObserver(function () {}).observe({}); } catch (e) { return e instanceof TypeError; } })()")]
    public async Task InterfaceShapeAndValidation(string assertion)
    {
        var page = await CreatePageAsync();
        Assert.Equal(true, page.Engine.Evaluate(assertion));
    }

    [Fact]
    public async Task VisibleTarget_ReportsItsLaidOutRects()
    {
        var page = await CreatePageAsync();
        page.Engine.Evaluate(
            "window.__io = ''; new IntersectionObserver(function (entries, observer) {" +
            "  var e = entries[0];" +
            "  window.__io = [entries.length, e.isIntersecting, e.intersectionRatio, e.target.id," +
            "    e.boundingClientRect.x, e.boundingClientRect.y, e.boundingClientRect.width, e.boundingClientRect.height," +
            "    e.intersectionRect.width, e.intersectionRect.height, e.rootBounds.x, e.rootBounds.y," +
            "    e instanceof IntersectionObserverEntry, this === observer].join('|');" +
            "}).observe(document.getElementById('visible'));");

        var result = await WaitForAsync(page.Engine, "window.__io");

        Assert.Equal("1|true|1|visible|10|20|100|50|100|50|0|0|true|true", result);
    }

    [Fact]
    public async Task TargetBelowTheViewport_IsNotIntersectingUntilRootMarginReachesIt()
    {
        var page = await CreatePageAsync();
        page.Engine.Evaluate(
            "window.__plain = ''; window.__margin = '';" +
            "new IntersectionObserver(function (entries) { window.__plain = entries[0].isIntersecting + '|' + entries[0].intersectionRatio; })" +
            "  .observe(document.getElementById('below'));" +
            "new IntersectionObserver(function (entries) { window.__margin = entries[0].isIntersecting + '|' + entries[0].rootBounds.height; }, { rootMargin: '0px 0px 10000px 0px' })" +
            "  .observe(document.getElementById('below'));");

        Assert.Equal("false|0", await WaitForAsync(page.Engine, "window.__plain"));
        var withMargin = await WaitForAsync(page.Engine, "window.__margin");
        Assert.StartsWith("true|", withMargin);
    }

    [Fact]
    public async Task TargetWithoutABox_GetsAnInitialNotIntersectingEntry()
    {
        var page = await CreatePageAsync();
        page.Engine.Evaluate(
            "window.__io = ''; new IntersectionObserver(function (entries) {" +
            "  window.__io = entries[0].isIntersecting + '|' + entries[0].boundingClientRect.width;" +
            "}).observe(document.getElementById('unlaid'));");

        Assert.Equal("false|0", await WaitForAsync(page.Engine, "window.__io"));
    }

    [Fact]
    public async Task UnchangedTarget_IsNotReported_ButAMoveIntoViewIs()
    {
        var page = await CreatePageAsync();
        page.Engine.Evaluate(
            "window.__calls = []; window.__observer = new IntersectionObserver(function (entries) {" +
            "  entries.forEach(function (e) { window.__calls.push(e.isIntersecting); });" +
            "}); window.__observer.observe(document.getElementById('below'));");
        Assert.Equal("false", await WaitForAsync(page.Engine, "window.__calls.join()"));

        // Another task runs: nothing moved, so nothing is queued.
        page.Engine.Evaluate("document.body.setAttribute('data-x', '1');");
        await Task.Delay(200);
        Assert.Equal("false", page.Engine.Evaluate("window.__calls.join()")?.ToString());

        page.Boxes[page.Get("below")] = Box(0, 100, 100, 100);
        page.Engine.Evaluate("document.body.setAttribute('data-x', '2');");
        Assert.Equal("false,true", await WaitForAsync(page.Engine, "window.__calls.length > 1 ? window.__calls.join() : ''"));
    }

    [Fact]
    public async Task UpdateStep_FlushesLayoutOncePerStepNotPerTarget()
    {
        // Every target is measured against one layout. The step used to flush per
        // target, and a burst of script jobs queued one step each that then ran back
        // to back: thirty observed elements cost hundreds of layout flushes.
        var page = await CreatePageAsync();
        page.Engine.Evaluate(
            "var io = new IntersectionObserver(function () {});" +
            "for (var i = 0; i < 30; i++) { var d = document.createElement('div'); d.setAttribute('data-target', ''); document.body.appendChild(d); io.observe(d); }");
        await Task.Delay(500);

        // One per step, and only a few steps for this burst; a single step that
        // flushed per target would already reach 30.
        Assert.InRange(page.TargetFlushes, 1, 12);
    }

    [Fact]
    public async Task UnobserveAndTakeRecords_StopDelivery()
    {
        var page = await CreatePageAsync();
        page.Engine.Evaluate(
            "window.__calls = 0; var io = new IntersectionObserver(function () { window.__calls++; });" +
            "var target = document.getElementById('visible'); io.observe(target); io.unobserve(target);" +
            "window.__records = io.takeRecords().length;");
        await Task.Delay(300);

        Assert.Equal("0|0", page.Engine.Evaluate("window.__calls + '|' + window.__records")?.ToString());
    }

    private static JsHostAdapter CreateHost() => new(
        navigate: _ => { },
        post: (_, _) => { },
        status: _ => { },
        log: _ => { });
}
