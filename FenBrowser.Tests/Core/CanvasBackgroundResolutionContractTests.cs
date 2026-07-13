using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;

namespace FenBrowser.Tests.Core;

public sealed class CanvasBackgroundResolutionContractTests
{
    [Fact]
    public void ResolveCanvasBackgroundColor_PrefersAuthoredBodyOverHtmlFallback()
    {
        var document = new Document();
        var html = new Element("html");
        var body = new Element("body");
        document.AppendChild(html);
        html.AppendChild(body);

        var bodyStyle = new CssComputed
        {
            Display = "block",
            BackgroundColor = SKColors.White
        };
        bodyStyle.Map["background-color"] = "#fff";

        var styles = new Dictionary<Node, CssComputed>
        {
            [html] = new CssComputed
            {
                Display = "block",
                BackgroundColor = SKColors.Black
            },
            [body] = bodyStyle
        };

        var background = SkiaDomRenderer.ResolveCanvasBackgroundColor(document, styles);

        Assert.Equal(SKColors.White, background);
    }
}
