using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout;

public sealed class StickyPositioningTests
{
    [Fact]
    public void ResolveStickyOffset_TopInset_SticksWhenScrolledPast()
    {
        var header = new Element("header");
        header.SetComputedStyle(new CssComputed
        {
            Position = "sticky",
            Top = 0,
            Height = 60
        });

        var containerGeometry = new BoxModel
        {
            ContentBox = new SKRect(0, 0, 800, 2000),
            PaddingBox = new SKRect(0, 0, 800, 2000)
        };

        // Normal flow position: header is at Y=200 in the container
        var box = CreateStickyBox(header, normalFlowX: 0, normalFlowY: 200, width: 800, height: 60);

        // No scroll: sticky offset should be 0 (header is below top)
        var offset0 = LayoutPositioningLogic.ResolveStickyOffset(box, containerGeometry, scrollOffsetY: 0);
        Assert.Equal(0f, offset0.Y);

        // Scrolled to Y=200: header should stick at top (offset = scrollY + top - normalY = 200 + 0 - 200 = 0)
        var offset200 = LayoutPositioningLogic.ResolveStickyOffset(box, containerGeometry, scrollOffsetY: 200);
        Assert.Equal(0f, offset200.Y);

        // Scrolled to Y=300: header should stick (offset = 300 + 0 - 200 = 100)
        var offset300 = LayoutPositioningLogic.ResolveStickyOffset(box, containerGeometry, scrollOffsetY: 300);
        Assert.Equal(100f, offset300.Y);
    }

    [Fact]
    public void ResolveStickyOffset_BottomInset_SticksToBottom()
    {
        var footer = new Element("footer");
        footer.SetComputedStyle(new CssComputed
        {
            Position = "sticky",
            Bottom = 20,
            Height = 40
        });

        var containerGeometry = new BoxModel
        {
            ContentBox = new SKRect(0, 0, 800, 1000),
            PaddingBox = new SKRect(0, 0, 800, 1000)
        };

        var box = CreateStickyBox(footer, normalFlowX: 0, normalFlowY: 900, width: 800, height: 40);

        // Scroll 0: footer at Y=900. Container bottom = 0+1000=1000.
        // maxY = scrollY + containerH - elemH - bottom = 0 + 1000 - 40 - 20 = 940
        // normalY=900 < 940, so no offset
        var offset0 = LayoutPositioningLogic.ResolveStickyOffset(box, containerGeometry, scrollOffsetY: 0);
        Assert.Equal(0f, offset0.Y);

        // Scroll 100: maxY = 100 + 1000 - 40 - 20 = 1040. normalY=900 < 1040, no offset
        var offset100 = LayoutPositioningLogic.ResolveStickyOffset(box, containerGeometry, scrollOffsetY: 100);
        Assert.Equal(0f, offset100.Y);
    }

    [Fact]
    public void ResolveStickyOffset_BothTopAndBottom_Constrained()
    {
        var panel = new Element("div");
        panel.SetComputedStyle(new CssComputed
        {
            Position = "sticky",
            Top = 10,
            Bottom = 10,
            Height = 100
        });

        var containerGeometry = new BoxModel
        {
            ContentBox = new SKRect(0, 0, 800, 500),
            PaddingBox = new SKRect(0, 0, 800, 500)
        };

        var box = CreateStickyBox(panel, normalFlowX: 0, normalFlowY: 200, width: 800, height: 100);

        // Scroll 300: top constraint = 300+10=310, but bottom constraint:
        // maxY = 300 + 500 - 100 - 10 = 690. normalY=200<310 so top wins, offset=310-200=110
        var offset = LayoutPositioningLogic.ResolveStickyOffset(box, containerGeometry, scrollOffsetY: 300);
        Assert.Equal(110f, offset.Y);
    }

    [Fact]
    public void ResolveStickyOffset_NotSticky_ReturnsZero()
    {
        var div = new Element("div");
        div.SetComputedStyle(new CssComputed
        {
            Position = "relative",
            Top = 50
        });

        var containerGeometry = new BoxModel
        {
            ContentBox = new SKRect(0, 0, 800, 1000)
        };

        var box = CreateStickyBox(div, 0, 100, 800, 50);
        var offset = LayoutPositioningLogic.ResolveStickyOffset(box, containerGeometry, scrollOffsetY: 500);
        Assert.Equal(0f, offset.Y);
    }

    [Fact]
    public void ResolveStickyOffset_NoInsets_ReturnsZero()
    {
        var nav = new Element("nav");
        nav.SetComputedStyle(new CssComputed
        {
            Position = "sticky",
            Height = 50
            // No top/bottom/left/right — nothing to stick to
        });

        var containerGeometry = new BoxModel
        {
            ContentBox = new SKRect(0, 0, 800, 1000)
        };

        var box = CreateStickyBox(nav, 0, 300, 800, 50);
        var offset = LayoutPositioningLogic.ResolveStickyOffset(box, containerGeometry, scrollOffsetY: 400);
        Assert.Equal(0f, offset.Y);
    }

    [Fact]
    public void ResolveStickyOffset_LeftInset_SticksHorizontally()
    {
        var sidebar = new Element("aside");
        sidebar.SetComputedStyle(new CssComputed
        {
            Position = "sticky",
            Left = 0,
            Width = 200,
            Height = 600
        });

        var containerGeometry = new BoxModel
        {
            ContentBox = new SKRect(0, 0, 1200, 2000)
        };

        var box = CreateStickyBox(sidebar, normalFlowX: 100, normalFlowY: 0, width: 200, height: 600);

        // Scroll 200px right: sticky left = 200+0=200, normalX=100, offset=100
        var offset = LayoutPositioningLogic.ResolveStickyOffset(
            box, containerGeometry, scrollOffsetY: 0, scrollOffsetX: 200);
        Assert.Equal(100f, offset.X);
        Assert.Equal(0f, offset.Y);
    }

    [Fact]
    public void ResolveStickyOffset_NullBox_ReturnsZero()
    {
        var offset = LayoutPositioningLogic.ResolveStickyOffset(
            null, new BoxModel(), 0);
        Assert.Equal(0f, offset.X);
        Assert.Equal(0f, offset.Y);
    }

    private static LayoutBox CreateStickyBox(
        Element element,
        float normalFlowX,
        float normalFlowY,
        float width,
        float height)
    {
        var style = element.GetComputedStyle() ?? new CssComputed();
        var store = new LayoutBoxStore();
        int id = store.CreateBox(element, style, LayoutBoxStore.BoxType.Block);
        var box = store.GetWrapper(id);

        // Set up geometry as if it was laid out in normal flow
        box.Geometry = new BoxModel
        {
            ContentBox = new SKRect(normalFlowX, normalFlowY, normalFlowX + width, normalFlowY + height),
            PaddingBox = new SKRect(normalFlowX, normalFlowY, normalFlowX + width, normalFlowY + height),
            BorderBox = new SKRect(normalFlowX, normalFlowY, normalFlowX + width, normalFlowY + height),
            MarginBox = new SKRect(normalFlowX, normalFlowY, normalFlowX + width, normalFlowY + height)
        };

        return box;
    }
}
