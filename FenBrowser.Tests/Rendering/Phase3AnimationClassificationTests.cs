using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Host;
using Xunit;

namespace FenBrowser.Tests.Rendering;

/// <summary>
/// Phase 3 regression: animation properties must be classified by the cheapest
/// correct update path (composite / paint / layout) so the renderer/host can avoid
/// rebuilding the paint tree or re-rasterizing content when only a compositor-only
/// or paint-only property changed.
/// </summary>
public class Phase3AnimationClassificationTests
{
    [Theory]
    [InlineData("transform", AnimationUpdateKind.Composite)]
    [InlineData("opacity", AnimationUpdateKind.Composite)]
    [InlineData("filter", AnimationUpdateKind.Composite)]
    [InlineData("clip-path", AnimationUpdateKind.Composite)]
    [InlineData("translate", AnimationUpdateKind.Composite)]
    [InlineData("rotate", AnimationUpdateKind.Composite)]
    [InlineData("scale", AnimationUpdateKind.Composite)]
    [InlineData("background-color", AnimationUpdateKind.Paint)]
    [InlineData("border-color", AnimationUpdateKind.Paint)]
    [InlineData("color", AnimationUpdateKind.Paint)]
    [InlineData("box-shadow", AnimationUpdateKind.Paint)]
    [InlineData("width", AnimationUpdateKind.Layout)]
    [InlineData("height", AnimationUpdateKind.Layout)]
    [InlineData("margin", AnimationUpdateKind.Layout)]
    [InlineData("padding", AnimationUpdateKind.Layout)]
    [InlineData("font-size", AnimationUpdateKind.Layout)]
    [InlineData("flex-basis", AnimationUpdateKind.Layout)]
    public void ClassifyAnimationUpdateKind_ReturnsExpectedKind(string property, AnimationUpdateKind expected)
    {
        Assert.Equal(expected, CssAnimationEngine.ClassifyAnimationUpdateKind(property));
    }

    [Fact]
    public void ClassifyAnimationUpdateKind_UnknownVisualProperty_DefaultsToPaint_NotLayout()
    {
        // An unknown visual property must not be reduced to a full layout, per the
        // property-specific invalidation requirement.
        var kind = CssAnimationEngine.ClassifyAnimationUpdateKind("not-a-real-property");
        Assert.Equal(AnimationUpdateKind.Paint, kind);
    }

    [Fact]
    public void DetermineAnimationUpdateKind_MixedProperties_CarriesEveryBit()
    {
        var kind = CssAnimationEngine.DetermineAnimationUpdateKind(
            new[] { "transform", "background-color", "width" });
        Assert.Equal(
            AnimationUpdateKind.Composite | AnimationUpdateKind.Paint | AnimationUpdateKind.Layout,
            kind);
    }

    [Fact]
    public void DetermineAnimationUpdateKind_CompositorOnly_IsCompositeAlone()
    {
        var kind = CssAnimationEngine.DetermineAnimationUpdateKind(
            new[] { "transform", "opacity" });
        Assert.Equal(AnimationUpdateKind.Composite, kind);
    }

    [Fact]
    public void AnimationFrameEvent_PopulatesUpdateKindFromChangedProperties()
    {
        var doc = Document.CreateHtmlDocument();
        var element = new Element("div");

        var evt = new AnimationFrameEvent
        {
            Element = element,
            OwnerDocument = doc,
            Invalidation = InvalidationKind.Paint,
            ChangedProperties = new System.Collections.Generic.List<string> { "transform" }
        };

        evt.UpdateKind = CssAnimationEngine.DetermineAnimationUpdateKind(evt.ChangedProperties);

        Assert.Equal(AnimationUpdateKind.Composite, evt.UpdateKind);
    }

    [Fact]
    public void MapAnimationInvalidation_CompositorOnly_RequestsAnimationOnly_NoPaintOrLayout()
    {
        var evt = new AnimationFrameEvent
        {
            Element = new Element("div"),
            ChangedProperties = new System.Collections.Generic.List<string> { "transform" }
        };
        evt.UpdateKind = CssAnimationEngine.DetermineAnimationUpdateKind(evt.ChangedProperties);

        var reason = BrowserIntegration.MapAnimationInvalidation(evt);

        Assert.Equal(RenderFrameInvalidationReason.Animation, reason);
        Assert.False(reason.HasFlag(RenderFrameInvalidationReason.Paint));
        Assert.False(reason.HasFlag(RenderFrameInvalidationReason.Layout));
    }

    [Fact]
    public void MapAnimationInvalidation_PaintOnly_RequestsPaintButNotLayout()
    {
        var evt = new AnimationFrameEvent
        {
            Element = new Element("div"),
            ChangedProperties = new System.Collections.Generic.List<string> { "background-color" }
        };
        evt.UpdateKind = CssAnimationEngine.DetermineAnimationUpdateKind(evt.ChangedProperties);

        var reason = BrowserIntegration.MapAnimationInvalidation(evt);

        Assert.True(reason.HasFlag(RenderFrameInvalidationReason.Animation));
        Assert.True(reason.HasFlag(RenderFrameInvalidationReason.Paint));
        Assert.False(reason.HasFlag(RenderFrameInvalidationReason.Layout));
    }

    [Fact]
    public void MapAnimationInvalidation_LayoutProperty_RequestsLayoutAndPaint()
    {
        var evt = new AnimationFrameEvent
        {
            Element = new Element("div"),
            ChangedProperties = new System.Collections.Generic.List<string> { "width" }
        };
        evt.UpdateKind = CssAnimationEngine.DetermineAnimationUpdateKind(evt.ChangedProperties);

        var reason = BrowserIntegration.MapAnimationInvalidation(evt);

        Assert.True(reason.HasFlag(RenderFrameInvalidationReason.Layout));
        Assert.True(reason.HasFlag(RenderFrameInvalidationReason.Paint));
    }

    [Fact]
    public void MapAnimationInvalidation_NullEvent_FallsBackToAnimationOnly()
    {
        var reason = BrowserIntegration.MapAnimationInvalidation((AnimationFrameEvent)null);
        Assert.Equal(RenderFrameInvalidationReason.Animation, reason);
    }
}
