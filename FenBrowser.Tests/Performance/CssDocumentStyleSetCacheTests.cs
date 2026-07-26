using System;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Performance;

[Collection("Performance diagnostics")]
public sealed class CssDocumentStyleSetCacheTests
{
    [Fact]
    public async Task UnchangedStylesheets_ReuseCompiledStyleSetForDirtyElements()
    {
        const string html = """
            <!doctype html>
            <html>
              <head><link rel="stylesheet" href="/site.css"></head>
              <body><div id="target" class="before">Target</div></body>
            </html>
            """;
        var document = new HtmlParser(html, new Uri("https://example.test/")).Parse();
        var root = Assert.IsType<Element>(document.DocumentElement);
        var target = Assert.IsType<Element>(document.GetElementById("target"));
        var fetchCount = 0;

        Task<string> Fetch(Uri _)
        {
            Interlocked.Increment(ref fetchCount);
            return Task.FromResult(".before { color: red; } .after { color: blue; }");
        }

        CssLoader.ClearCaches();
        var initial = await CssLoader.ComputeWithResultAsync(
            root, new Uri("https://example.test/"), Fetch, 1280, 800);
        Assert.False(initial.Timing.StyleSetCacheHit);
        Assert.Equal<SKColor?>(SKColors.Red, initial.Computed[target].ForegroundColor);

        target.SetAttribute("class", "after");
        var incremental = await CssLoader.ComputeWithResultAsync(
            root, new Uri("https://example.test/"), Fetch, 1280, 800);

        Assert.True(incremental.Timing.StyleSetCacheHit);
        Assert.Equal(1, Volatile.Read(ref fetchCount));
        Assert.Equal<SKColor?>(SKColors.Blue, incremental.Computed[target].ForegroundColor);
    }

    [Fact]
    public async Task ChangedStyleElement_InvalidatesCompiledStyleSet()
    {
        const string html = """
            <!doctype html>
            <html>
              <head><style id="rules">#target { color: red; }</style></head>
              <body><div id="target">Target</div></body>
            </html>
            """;
        var document = new HtmlParser(html, new Uri("https://example.test/")).Parse();
        var root = Assert.IsType<Element>(document.DocumentElement);
        var rules = Assert.IsType<Element>(document.GetElementById("rules"));
        var target = Assert.IsType<Element>(document.GetElementById("target"));

        CssLoader.ClearCaches();
        var initial = await CssLoader.ComputeWithResultAsync(
            root, new Uri("https://example.test/"), null, 1280, 800);
        Assert.Equal<SKColor?>(SKColors.Red, initial.Computed[target].ForegroundColor);

        rules.TextContent = "#target { color: blue; }";
        var updated = await CssLoader.ComputeWithResultAsync(
            root, new Uri("https://example.test/"), null, 1280, 800);

        Assert.False(updated.Timing.StyleSetCacheHit);
        Assert.Equal<SKColor?>(SKColors.Blue, updated.Computed[target].ForegroundColor);
    }
}
