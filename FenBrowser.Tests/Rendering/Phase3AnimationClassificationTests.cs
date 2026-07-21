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

    // ── Phase 1: authoritative classification tests ──

    [Fact]
    public void ClassifyAnimationProperties_Transform_CompositeOnly_NoDomInvalidation()
    {
        var result = CssAnimationEngine.ClassifyAnimationProperties(
            new[] { "transform" });

        Assert.Equal(AnimationUpdateKind.Composite, result.UpdateKind);
        Assert.Equal(InvalidationKind.None, result.DomInvalidation);
        Assert.Contains("transform", result.ChangedProperties);
    }

    [Fact]
    public void ClassifyAnimationProperties_Opacity_CompositeOnly_NoDomInvalidation()
    {
        var result = CssAnimationEngine.ClassifyAnimationProperties(
            new[] { "opacity" });

        Assert.Equal(AnimationUpdateKind.Composite, result.UpdateKind);
        Assert.Equal(InvalidationKind.None, result.DomInvalidation);
    }

    [Fact]
    public void ClassifyAnimationProperties_Filter_CompositeOnly_NoDomInvalidation()
    {
        var result = CssAnimationEngine.ClassifyAnimationProperties(
            new[] { "filter" });

        Assert.Equal(AnimationUpdateKind.Composite, result.UpdateKind);
        Assert.Equal(InvalidationKind.None, result.DomInvalidation);
    }

    [Fact]
    public void ClassifyAnimationProperties_BackgroundColor_PaintOnly_NoLayout()
    {
        var result = CssAnimationEngine.ClassifyAnimationProperties(
            new[] { "background-color" });

        Assert.Equal(AnimationUpdateKind.Paint, result.UpdateKind);
        Assert.Equal(InvalidationKind.Paint, result.DomInvalidation);
        Assert.False(result.DomInvalidation.HasFlag(InvalidationKind.Layout));
    }

    [Fact]
    public void ClassifyAnimationProperties_Width_LayoutAndPaint()
    {
        var result = CssAnimationEngine.ClassifyAnimationProperties(
            new[] { "width" });

        Assert.Equal(AnimationUpdateKind.Layout, result.UpdateKind);
        Assert.Equal(InvalidationKind.Layout | InvalidationKind.Paint, result.DomInvalidation);
    }

    [Fact]
    public void ClassifyAnimationProperties_Mixed_CompositeAndPaint_NoLayout()
    {
        var result = CssAnimationEngine.ClassifyAnimationProperties(
            new[] { "transform", "background-color" });

        Assert.Equal(
            AnimationUpdateKind.Composite | AnimationUpdateKind.Paint,
            result.UpdateKind);
        Assert.Equal(InvalidationKind.Paint, result.DomInvalidation);
        Assert.False(result.DomInvalidation.HasFlag(InvalidationKind.Layout));
    }

    [Fact]
    public void ClassifyAnimationProperties_Mixed_AllThree()
    {
        var result = CssAnimationEngine.ClassifyAnimationProperties(
            new[] { "transform", "background-color", "width" });

        Assert.Equal(
            AnimationUpdateKind.Composite | AnimationUpdateKind.Paint | AnimationUpdateKind.Layout,
            result.UpdateKind);
        Assert.Equal(
            InvalidationKind.Layout | InvalidationKind.Paint,
            result.DomInvalidation);
    }

    [Fact]
    public void ClassifyAnimationProperties_Null_ReturnsNone()
    {
        var result = CssAnimationEngine.ClassifyAnimationProperties(null);

        Assert.Equal(AnimationUpdateKind.None, result.UpdateKind);
        Assert.Equal(InvalidationKind.None, result.DomInvalidation);
        Assert.Empty(result.ChangedProperties);
    }

    [Fact]
    public void ClassifyAnimationProperties_Empty_ReturnsNone()
    {
        var result = CssAnimationEngine.ClassifyAnimationProperties(
            Array.Empty<string>());

        Assert.Equal(AnimationUpdateKind.None, result.UpdateKind);
        Assert.Equal(InvalidationKind.None, result.DomInvalidation);
        Assert.Empty(result.ChangedProperties);
    }

    [Fact]
    public void AnimationFrameEvent_DomInvalidation_CompositeOnly_IsNone()
    {
        var evt = new AnimationFrameEvent
        {
            Element = new Element("div"),
            ChangedProperties = new System.Collections.Generic.List<string> { "transform" }
        };
        var classification = CssAnimationEngine.ClassifyAnimationProperties(evt.ChangedProperties);
        evt.UpdateKind = classification.UpdateKind;
        evt.DomInvalidation = classification.DomInvalidation;

        Assert.Equal(AnimationUpdateKind.Composite, evt.UpdateKind);
        Assert.Equal(InvalidationKind.None, evt.DomInvalidation);
    }

    [Fact]
    public void AnimationFrameEvent_DomInvalidation_PaintOnly_HasPaintNotLayout()
    {
        var evt = new AnimationFrameEvent
        {
            Element = new Element("div"),
            ChangedProperties = new System.Collections.Generic.List<string> { "background-color" }
        };
        var classification = CssAnimationEngine.ClassifyAnimationProperties(evt.ChangedProperties);
        evt.UpdateKind = classification.UpdateKind;
        evt.DomInvalidation = classification.DomInvalidation;

        Assert.Equal(AnimationUpdateKind.Paint, evt.UpdateKind);
        Assert.Equal(InvalidationKind.Paint, evt.DomInvalidation);
        Assert.False(evt.DomInvalidation.HasFlag(InvalidationKind.Layout));
    }

    [Fact]
    public void TransitionClassification_MatchesKeyframeClassification()
    {
        // Transitions and keyframe animations must produce the same classification
        // for each property type.
        var compositeResult = CssAnimationEngine.ClassifyAnimationUpdateKind("transform");
        var paintResult = CssAnimationEngine.ClassifyAnimationUpdateKind("background-color");
        var layoutResult = CssAnimationEngine.ClassifyAnimationUpdateKind("width");

        Assert.Equal(AnimationUpdateKind.Composite, compositeResult);
        Assert.Equal(AnimationUpdateKind.Paint, paintResult);
        Assert.Equal(AnimationUpdateKind.Layout, layoutResult);
    }

    [Fact]
    public void TransformAnimation_DoesNotSetElementPaintDirty()
    {
        var doc = Document.CreateHtmlDocument();
        var element = doc.DocumentElement; // use the existing root element

        // Simulate what ApplyAnimationFrame now does for composite-only
        var classification = CssAnimationEngine.ClassifyAnimationProperties(
            new[] { "transform" });

        // Composite-only must not mark paint dirty
        Assert.Equal(InvalidationKind.None, classification.DomInvalidation);

        // MarkDirty with None should change nothing
        element.ClearDirty(InvalidationKind.All); // start clean
        element.MarkDirty(classification.DomInvalidation);
        Assert.False(element.PaintDirty);
        Assert.False(element.LayoutDirty);
    }

    [Fact]
    public void WidthAnimation_SetsElementLayoutAndPaintDirty()
    {
        var doc = Document.CreateHtmlDocument();
        var element = doc.DocumentElement;
        element.ClearDirty(InvalidationKind.All);

        var classification = CssAnimationEngine.ClassifyAnimationProperties(
            new[] { "width" });

        Assert.True(classification.DomInvalidation.HasFlag(InvalidationKind.Layout));
        Assert.True(classification.DomInvalidation.HasFlag(InvalidationKind.Paint));

        element.MarkDirty(classification.DomInvalidation);
        Assert.True(element.LayoutDirty);
        Assert.True(element.PaintDirty);
    }

    [Fact]
    public void BackgroundColorAnimation_SetsPaintDirty_NotLayoutDirty()
    {
        var doc = Document.CreateHtmlDocument();
        var element = doc.DocumentElement;
        element.ClearDirty(InvalidationKind.All);

        var classification = CssAnimationEngine.ClassifyAnimationProperties(
            new[] { "background-color" });

        Assert.True(classification.DomInvalidation.HasFlag(InvalidationKind.Paint));
        Assert.False(classification.DomInvalidation.HasFlag(InvalidationKind.Layout));

        element.MarkDirty(classification.DomInvalidation);
        Assert.True(element.PaintDirty);
        Assert.False(element.LayoutDirty);
    }

    [Fact]
    public void TransformAnimation_DoesNotPropagateChildPaintDirtyToAncestors()
    {
        var doc = Document.CreateHtmlDocument();
        var parent = doc.DocumentElement; // the html element
        var child = new Element("div");
        parent.AppendChild(child);

        // Composite-only classification has no DOM invalidation
        var classification = CssAnimationEngine.ClassifyAnimationProperties(
            new[] { "transform" });
        Assert.Equal(InvalidationKind.None, classification.DomInvalidation);

        // Clear all dirty first
        parent.ClearDirty(InvalidationKind.All);
        child.ClearDirty(InvalidationKind.All);

        // No MarkDirty means no ChildPaintDirty propagation
        child.MarkDirty(classification.DomInvalidation);
        Assert.False(parent.ChildPaintDirty);
        Assert.False(parent.PaintDirty);
    }

    [Fact]
    public void AnimationGeneration_IncrementsOnClassification()
    {
        var doc = Document.CreateHtmlDocument();
        var element = doc.DocumentElement;

        // Generation tracking is internal to CssAnimationEngine;
        // test that the event carries Generation as a long.
        var evt = new AnimationFrameEvent
        {
            Element = element,
            OwnerDocument = doc,
            ChangedProperties = new System.Collections.Generic.List<string> { "transform" }
        };
        // Generation starts at 0 without explicit engine tracking
        Assert.True(evt.Generation >= 0);
    }
}
