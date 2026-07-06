using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout;

public sealed class InlineFloatAvoidanceTests
{
    [Fact]
    public void FloatManager_GetAvailableSpace_ReducesLineWidth()
    {
        var fm = new FloatManager();
        var leftFloat = new SKRect(0, 0, 100, 200);
        fm.AddFloat(leftFloat, isLeft: true);

        var space = fm.GetAvailableSpace(y: 50, height: 20, containerWidth: 500);

        Assert.Equal(100f, space.LeftOffset);
        Assert.Equal(0f, space.RightOffset);
        Assert.Equal(400f, space.AvailableWidth);
    }

    [Fact]
    public void FloatManager_Clearance_MovesBelowFloat()
    {
        var fm = new FloatManager();
        var leftFloat = new SKRect(0, 0, 100, 200);
        fm.AddFloat(leftFloat, isLeft: true);

        float clearY = fm.GetClearanceY("left", currentY: 10);

        Assert.Equal(200f, clearY); // moved to bottom of float
    }

    [Fact]
    public void FloatManager_RightFloat_ReducesAvailableWidth()
    {
        var fm = new FloatManager();
        // Right float: left edge at x=400, so dist from right = 500-400 = 100
        var rightFloat = new SKRect(400, 0, 500, 200);
        fm.AddFloat(rightFloat, isLeft: false);

        var space = fm.GetAvailableSpace(y: 50, height: 20, containerWidth: 500);

        Assert.Equal(0f, space.LeftOffset);
        Assert.Equal(100f, space.RightOffset);
        Assert.Equal(400f, space.AvailableWidth);
    }

    [Fact]
    public void FloatManager_NoFloats_ReturnsFullWidth()
    {
        var fm = new FloatManager();

        var space = fm.GetAvailableSpace(y: 50, height: 20, containerWidth: 500);

        Assert.Equal(0f, space.LeftOffset);
        Assert.Equal(0f, space.RightOffset);
        Assert.Equal(500f, space.AvailableWidth);
        Assert.False(fm.HasFloats);
    }

    [Fact]
    public void LayoutState_DefaultsToNullFloatManager()
    {
        var state = new LayoutState(new SKSize(800, 600), 800, 600, 800, 600);

        Assert.Null(state.FloatManager);
        Assert.Equal(0f, state.FloatOriginX);
        Assert.Equal(0f, state.FloatOriginY);
    }

    [Fact]
    public void LayoutState_CanCarryFloatManager()
    {
        var fm = new FloatManager();
        var state = new LayoutState(new SKSize(800, 600), 800, 600, 800, 600)
        {
            FloatManager = fm,
            FloatOriginX = 10f,
            FloatOriginY = 50f
        };

        Assert.NotNull(state.FloatManager);
        Assert.Equal(10f, state.FloatOriginX);
        Assert.Equal(50f, state.FloatOriginY);
    }
}
