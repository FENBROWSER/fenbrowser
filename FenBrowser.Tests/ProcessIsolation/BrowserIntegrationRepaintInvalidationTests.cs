using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Host;
using Xunit;

namespace FenBrowser.Tests.ProcessIsolation;

public sealed class BrowserIntegrationRepaintInvalidationTests
{
    [Fact]
    public void ClassifyRepaintReady_WithStableSnapshot_RequestsPaintOnly()
    {
        var root = new Element("html");

        var reason = BrowserIntegration.ClassifyRepaintReadyInvalidation(
            root,
            rootChanged: false,
            stylesChanged: false,
            hasFirstStyledRender: true);

        Assert.Equal(RenderFrameInvalidationReason.Paint, reason);
    }

    [Fact]
    public void ClassifyRepaintReady_WithLayoutDirtySnapshot_RequestsLayoutPaintOnly()
    {
        var root = new Element("html");
        var body = new Element("body");
        root.AppendChild(body);
        root.ClearDirty(InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
        body.ClearDirty(InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);
        body.MarkDirty(InvalidationKind.Layout | InvalidationKind.Paint);

        var reason = BrowserIntegration.ClassifyRepaintReadyInvalidation(
            root,
            rootChanged: false,
            stylesChanged: false,
            hasFirstStyledRender: true);

        Assert.True((reason & RenderFrameInvalidationReason.Layout) != 0);
        Assert.True((reason & RenderFrameInvalidationReason.Paint) != 0);
        Assert.False((reason & RenderFrameInvalidationReason.Dom) != 0);
        Assert.False((reason & RenderFrameInvalidationReason.Style) != 0);
    }

    [Fact]
    public void IsFirstRenderSnapshotPresentable_WithUnstableEmptyStyles_BlocksFirstFrame()
    {
        var root = new Element("html");
        var styles = new Dictionary<Node, CssComputed>();

        Assert.False(BrowserIntegration.IsFirstRenderSnapshotPresentable(
            root,
            styles,
            hasStableStyles: false));
    }

    [Fact]
    public void IsFirstRenderSnapshotPresentable_WithStableEmptyStyles_AllowsFirstFrame()
    {
        var root = new Element("html");
        var styles = new Dictionary<Node, CssComputed>();

        Assert.True(BrowserIntegration.IsFirstRenderSnapshotPresentable(
            root,
            styles,
            hasStableStyles: true));
    }

    [Fact]
    public void IsFirstContentFrameReady_WithLoadingNewTab_BlocksFirstFrame()
    {
        var root = new Element("html");
        var styles = new Dictionary<Node, CssComputed>();

        Assert.False(BrowserIntegration.IsFirstContentFrameReady(
            root,
            styles,
            hasStableStyles: true,
            url: "fen://newtab/",
            isLoading: true));
    }

    [Fact]
    public void IsFirstContentFrameReady_WithLoadedNewTab_AllowsFirstFrame()
    {
        var root = new Element("html");
        var styles = new Dictionary<Node, CssComputed>();

        Assert.True(BrowserIntegration.IsFirstContentFrameReady(
            root,
            styles,
            hasStableStyles: true,
            url: "fen://newtab/",
            isLoading: false));
    }

    [Fact]
    public void IsFirstContentFrameReady_WithLoadingWebPage_AllowsStableFirstFrame()
    {
        var root = new Element("html");
        var styles = new Dictionary<Node, CssComputed>();

        Assert.True(BrowserIntegration.IsFirstContentFrameReady(
            root,
            styles,
            hasStableStyles: true,
            url: "https://www.google.com/",
            isLoading: true));
    }

    [Theory]
    [InlineData("fen://newtab")]
    [InlineData("fen://newtab/")]
    [InlineData("about:newtab")]
    public void IsNewTabSurfaceUrl_WithNewTabUrl_ReturnsTrue(string url)
    {
        Assert.True(BrowserIntegration.IsNewTabSurfaceUrl(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://www.google.com/")]
    [InlineData("fen://settings")]
    public void IsNewTabSurfaceUrl_WithNonNewTabUrl_ReturnsFalse(string url)
    {
        Assert.False(BrowserIntegration.IsNewTabSurfaceUrl(url));
    }
}
