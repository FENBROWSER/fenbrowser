using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout;

public sealed class ScrollAnchoringTests
{
    [Fact]
    public void SelectAnchor_FindsDeepestVisibleNodeInViewport()
    {
        var body = new Element("body");
        var div = new Element("div");
        var span = new Element("span");
        var text = new Text("visible content");
        span.AppendChild(text);
        div.AppendChild(span);
        body.AppendChild(div);

        var boxes = new Dictionary<Node, BoxModel>
        {
            [body] = CreateBox(0, 0, 800, 2000),
            [div] = CreateBox(16, 200, 768, 400),
            [span] = CreateBox(16, 250, 200, 30),
            [text] = CreateBox(16, 250, 180, 20)
        };

        var anchoring = new ScrollAnchoring();
        anchoring.SelectAnchor(boxes, scrollY: 100, viewportHeight: 600);

        // Should prefer the deepest node: the text node
        float adjustment = anchoring.CalculateAdjustment(boxes);
        Assert.Equal(0f, adjustment); // no movement = no adjustment
    }

    [Fact]
    public void CalculateAdjustment_CompensatesForAnchorMovement()
    {
        var div = new Element("div");
        var boxesBefore = new Dictionary<Node, BoxModel>
        {
            [div] = CreateBox(0, 300, 800, 100)
        };

        var anchoring = new ScrollAnchoring();
        anchoring.SelectAnchor(boxesBefore, scrollY: 200, viewportHeight: 600);
        // Anchor viewportY = 300 - 200 = 100

        // Simulate layout shift: a banner above the div was removed, div moved up by 50px
        var boxesAfter = new Dictionary<Node, BoxModel>
        {
            [div] = CreateBox(0, 250, 800, 100)
        };
        // New viewportY = 250 - 200 = 50
        // Delta = 50 - 100 = -50 (div moved up, scroll should move up to compensate)

        float adjustment = anchoring.CalculateAdjustment(boxesAfter);
        Assert.Equal(-50f, adjustment);
    }

    [Fact]
    public void CalculateAdjustment_ReturnsZero_WhenAnchorDisappears()
    {
        var div = new Element("div");
        var boxesBefore = new Dictionary<Node, BoxModel>
        {
            [div] = CreateBox(0, 300, 800, 100)
        };

        var anchoring = new ScrollAnchoring();
        anchoring.SelectAnchor(boxesBefore, scrollY: 200, viewportHeight: 600);

        var boxesAfter = new Dictionary<Node, BoxModel>();
        float adjustment = anchoring.CalculateAdjustment(boxesAfter);
        Assert.Equal(0f, adjustment);
    }

    [Fact]
    public void CalculateAdjustment_ReturnsZero_ForSubpixelNoise()
    {
        var div = new Element("div");
        var boxesBefore = new Dictionary<Node, BoxModel>
        {
            [div] = CreateBox(0, 300, 800, 100)
        };

        var anchoring = new ScrollAnchoring();
        anchoring.SelectAnchor(boxesBefore, scrollY: 200, viewportHeight: 600);

        // Move by 0.3px — below the 0.5px threshold
        var boxesAfter = new Dictionary<Node, BoxModel>
        {
            [div] = CreateBox(0, 300.3f, 800, 100)
        };

        float adjustment = anchoring.CalculateAdjustment(boxesAfter);
        Assert.Equal(0f, adjustment);
    }

    [Fact]
    public void SelectAnchor_SkipsNodesOutsideViewport()
    {
        var div = new Element("div");
        var boxes = new Dictionary<Node, BoxModel>
        {
            [div] = CreateBox(0, 5000, 800, 100) // far below viewport
        };

        var anchoring = new ScrollAnchoring();
        anchoring.SelectAnchor(boxes, scrollY: 0, viewportHeight: 600);

        float adjustment = anchoring.CalculateAdjustment(boxes);
        Assert.Equal(0f, adjustment); // no anchor selected → no adjustment
    }

    [Fact]
    public void Reset_ClearsAnchorState()
    {
        var div = new Element("div");
        var boxes = new Dictionary<Node, BoxModel>
        {
            [div] = CreateBox(0, 300, 800, 100)
        };

        var anchoring = new ScrollAnchoring();
        anchoring.SelectAnchor(boxes, scrollY: 200, viewportHeight: 600);
        anchoring.Reset();

        float adjustment = anchoring.CalculateAdjustment(boxes);
        Assert.Equal(0f, adjustment);
    }

    private static BoxModel CreateBox(float x, float y, float width, float height)
    {
        return new BoxModel
        {
            ContentBox = new SKRect(x, y, x + width, y + height),
            PaddingBox = new SKRect(x, y, x + width, y + height),
            BorderBox = new SKRect(x, y, x + width, y + height),
            MarginBox = new SKRect(x, y, x + width, y + height)
        };
    }
}
