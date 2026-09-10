using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Scripting;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Scripting;

public sealed class FenJsElementClientRectsTests
{
    [Fact]
    public async Task GetClientRects_ReturnsTheResolvedBorderBox()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body><div id='target'></div></body></html>", baseUri).Parse();
        var target = Assert.IsType<Element>(document.GetElementById("target"));
        var box = new BoxModel
        {
            BorderBox = new SKRect(10, 20, 110, 70)
        };
        var flushes = 0;
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            FlushPendingLayout = _ => flushes++,
            LayoutBoxResolver = element => ReferenceEquals(element, target) ? box : null
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        var result = engine.Evaluate(
            "var rects=document.getElementById('target').getClientRects();" +
            "var rect=rects.item(0);" +
            "[rects.length,rect.x,rect.y,rect.width,rect.height,rect.right,rect.bottom," +
            "String(rects.item(1))].join('|')");

        Assert.Equal("1|10|20|100|50|110|70|null", result?.ToString());
        Assert.Equal(1, flushes);
    }

    [Fact]
    public async Task GetClientRects_WithoutLayout_ReturnsAnEmptyDomRectList()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body><div id='target'></div></body></html>", baseUri).Parse();
        var flushes = 0;
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            FlushPendingLayout = _ => flushes++,
            LayoutBoxResolver = _ => null
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        var result = engine.Evaluate(
            "var rects=document.getElementById('target').getClientRects();" +
            "[rects.length,String(rects.item(0))].join('|')");

        Assert.Equal("0|null", result?.ToString());
        Assert.Equal(1, flushes);
    }

    private static JsHostAdapter CreateHost() => new(
        navigate: _ => { },
        post: (_, _) => { },
        status: _ => { },
        log: _ => { });
}
