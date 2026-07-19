using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.FenEngine.Layout;
using Xunit;

namespace FenBrowser.Tests.StyleLayoutContract;

[Collection("Engine Tests")]
public sealed class IframeSubdocumentRecascadeTests
{
    [Fact]
    public async Task ComputeSubtreeAsync_NewIframeDocument_UsesFrameLocalStylesheets()
    {
        var parentUri = new Uri("https://parent.example.test/");
        var parentDocument = new HtmlParser(
            "<!doctype html><html><body><div id='dirty'><iframe width='304' height='78'></iframe></div></body></html>",
            parentUri).Parse();
        var parentRoot = parentDocument.DocumentElement ?? parentDocument.Children.OfType<Element>().First();
        var dirtyRoot = parentRoot.Descendants().OfType<Element>().Single(element => element.Id == "dirty");
        var iframe = dirtyRoot.Descendants().OfType<Element>().Single(element =>
            string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase));

        var frameUri = new Uri("https://frame.example.test/anchor");
        var frameDocument = new HtmlParser(
            "<!doctype html><html><head><style>.checkbox{display:inline-block;width:28px;height:28px}</style></head><body><span id='checkbox' class='checkbox'></span></body></html>",
            frameUri).Parse();
        iframe.AppendChild(frameDocument);
        var checkbox = frameDocument.GetElementById("checkbox");
        Assert.NotNull(checkbox);

        var computed = await CssLoader.ComputeSubtreeAsync(
            parentRoot,
            dirtyRoot,
            parentUri,
            fetchExternalCssAsync: null,
            viewportWidth: 1280,
            viewportHeight: 800);

        Assert.True(computed.TryGetValue(checkbox!, out var checkboxStyle));
        Assert.Equal("inline-block", checkboxStyle.Display);
        Assert.Equal(28d, checkboxStyle.Width);
        Assert.Equal(28d, checkboxStyle.Height);
    }

    [Fact]
    public async Task ComputeSubtreeAsync_PercentageIframeUsesDefiniteContainingBlockAsViewport()
    {
        var parentUri = new Uri("https://parent.example.test/");
        var parentDocument = new HtmlParser(
            "<!doctype html><html><body><div id='challenge' style='width:300px;height:480px'><iframe style='width:100%;height:100%'></iframe></div></body></html>",
            parentUri).Parse();
        var parentRoot = parentDocument.DocumentElement ?? parentDocument.Children.OfType<Element>().First();
        var iframe = parentRoot.Descendants().OfType<Element>().Single(element =>
            string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase));

        var frameUri = new Uri("https://frame.example.test/challenge");
        var frameDocument = new HtmlParser(
            "<!doctype html><html><head><style>#viewport{width:100vw;height:100vh}</style></head><body><div id='viewport'></div></body></html>",
            frameUri).Parse();
        iframe.AppendChild(frameDocument);
        await CssLoader.ComputeSubtreeAsync(
            parentRoot,
            parentRoot,
            parentUri,
            fetchExternalCssAsync: null,
            viewportWidth: 1280,
            viewportHeight: 800);

        Assert.Equal(300d, CssLoader.ResolveFrameViewportDimension(iframe, "width", 1280));
        Assert.Equal(480d, CssLoader.ResolveFrameViewportDimension(iframe, "height", 800));
    }

    [Fact]
    public async Task ComputeLayout_ResizedIframeRelayoutsPercentageSubdocument()
    {
        var parentUri = new Uri("https://parent.example.test/");
        var parentDocument = new HtmlParser(
            "<!doctype html><html><body><iframe id='challenge' style='width:300px;height:78px'></iframe></body></html>",
            parentUri).Parse();
        var parentRoot = parentDocument.DocumentElement ?? parentDocument.Children.OfType<Element>().First();
        var iframe = Assert.IsType<Element>(parentDocument.GetElementById("challenge"));
        var frameUri = new Uri("https://frame.example.test/challenge");
        var frameDocument = new HtmlParser(
            "<!doctype html><html style='height:100%'><body style='height:100%'><div>Challenge</div></body></html>",
            frameUri).Parse();
        iframe.AppendChild(frameDocument);

        var styles = await CssLoader.ComputeSubtreeAsync(
            parentRoot,
            parentRoot,
            parentUri,
            fetchExternalCssAsync: null,
            viewportWidth: 1280,
            viewportHeight: 800);
        var layout = new LayoutEngine(styles, 1280, 800);
        layout.ComputeLayout(parentRoot, 0, 0, 1280, availableHeight: 800);
        Assert.Equal(78f, layout.AllBoxes[frameDocument.Body!].BorderBox.Height, 0.5f);

        iframe.SetAttribute("style", "width:300px;height:480px");
        styles[iframe].Height = 480;
        iframe.MarkDirty(InvalidationKind.Layout | InvalidationKind.Paint);
        layout.ComputeLayout(parentRoot, 0, 0, 1280, availableHeight: 800);

        Assert.Equal(480f, layout.AllBoxes[iframe].ContentBox.Height, 0.5f);
        Assert.Equal(480f, layout.AllBoxes[frameDocument.Body!].BorderBox.Height, 0.5f);
    }
}
