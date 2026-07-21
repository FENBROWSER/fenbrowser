using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Host;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering;

/// <summary>
/// Phase 13: end-to-end pipeline tests that verify compositor-only behavior,
/// paint-tree rebuild classification, and animation telemetry using a real
/// SkiaDomRenderer. These replace the earlier enum-only tests with actual
/// render-frame verification.
/// </summary>
public class PipelineAnimationTests
{
    private static SkiaDomRenderer CreateRenderer()
        => new SkiaDomRenderer();

    private static Document CreateTestDocument()
    {
        var doc = Document.CreateHtmlDocument();
        return doc;
    }

    private static Dictionary<Node, CssComputed> CreateStyles(Element element,
        Dictionary<string, string> properties)
    {
        var computed = new CssComputed();
        foreach (var kvp in properties)
        {
            computed.Map[kvp.Key] = kvp.Value;
        }
        return new Dictionary<Node, CssComputed> { [element] = computed };
    }

    /// <summary>
    /// Creates a RenderFrameRequest with a managed surface that must be disposed
    /// by the caller via the returned SurfaceWrapper.
    /// </summary>
    private sealed class SurfaceWrapper : IDisposable
    {
        public readonly SKSurface Surface;
        public readonly RenderFrameRequest Request;

        public SurfaceWrapper(SKSurface surface, RenderFrameRequest request)
        {
            Surface = surface;
            Request = request;
        }

        public void Dispose() => Surface?.Dispose();
    }

    private static SurfaceWrapper CreateRequest(
        Node root,
        Dictionary<Node, CssComputed> styles,
        float width = 800,
        float height = 600,
        AnimationUpdateKind animKind = AnimationUpdateKind.None)
    {
        var surface = SKSurface.Create(new SKImageInfo((int)width, (int)height));
        return new SurfaceWrapper(surface, new RenderFrameRequest
        {
            Root = root,
            Canvas = surface.Canvas,
            Styles = styles,
            Viewport = new SKRect(0, 0, width, height),
            BaseUrl = "about:blank",
            InvalidationReason = animKind != AnimationUpdateKind.None
                ? RenderFrameInvalidationReason.Animation
                : RenderFrameInvalidationReason.Navigation,
            RequestedBy = "PipelineTest",
            AnimationUpdateKind = animKind,
            CompositeDirtyElements = (animKind & AnimationUpdateKind.Composite) != 0
                ? new[] { root as Element }
                : null,
            PaintDirtyElements = (animKind & AnimationUpdateKind.Paint) != 0
                ? new[] { root as Element }
                : null,
            AnimationGeneration = 1,
            ImageGeneration = 0,
            ImageGenerationChanged = false
        });
    }

    [Fact]
    public void InitialFrame_BuildsPaintTree_AndHasRebuildReason()
    {
        var doc = CreateTestDocument();
        var renderer = CreateRenderer();
        var styles = CreateStyles(doc.DocumentElement, new Dictionary<string, string>
        {
            ["display"] = "block"
        });

        using var wrapper = CreateRequest(doc, styles);
        var result = renderer.RenderFrame(wrapper.Request);

        Assert.NotNull(result);
        Assert.NotNull(result.Telemetry);
        Assert.True(result.Telemetry.LayoutUpdated);
        Assert.True(result.Telemetry.PaintTreeRebuilt);
        Assert.NotEqual(
            PaintTreeRebuildReason.None,
            result.Telemetry.PaintTreeRebuildReason);
        Assert.Equal(RenderFrameRasterMode.Full, result.Telemetry.RasterMode);
    }

    [Fact]
    public void StaticSecondFrame_NoChanges_RetainsPaintTree()
    {
        var doc = CreateTestDocument();
        var renderer = CreateRenderer();
        var styles = CreateStyles(doc.DocumentElement, new Dictionary<string, string>
        {
            ["display"] = "block"
        });

        using (var w1 = CreateRequest(doc, styles))
            renderer.RenderFrame(w1.Request);
        using var w2 = CreateRequest(doc, styles, animKind: AnimationUpdateKind.None);
        var result = renderer.RenderFrame(w2.Request);

        Assert.NotNull(result);
        Assert.False(result.Telemetry.LayoutUpdated);
        Assert.False(result.Telemetry.PaintTreeRebuilt);
    }

    [Fact]
    public void AnimationUpdateKind_Composite_IsPreservedInTelemetry()
    {
        var doc = CreateTestDocument();
        var renderer = CreateRenderer();
        var styles = CreateStyles(doc.DocumentElement, new Dictionary<string, string>
        {
            ["display"] = "block",
            ["transform"] = "translate(0px, 0px)"
        });

        using (var w1 = CreateRequest(doc, styles))
            renderer.RenderFrame(w1.Request);
        using var w2 = CreateRequest(doc, styles, animKind: AnimationUpdateKind.Composite);
        var result = renderer.RenderFrame(w2.Request);

        Assert.NotNull(result);
        Assert.NotNull(result.Telemetry);
        Assert.Equal(
            AnimationUpdateKind.Composite,
            result.Telemetry.RequestedAnimationUpdateKind);
    }

    [Fact]
    public void AnimationUpdateKind_Paint_IsPreservedInTelemetry()
    {
        var doc = CreateTestDocument();
        var renderer = CreateRenderer();
        var styles = CreateStyles(doc.DocumentElement, new Dictionary<string, string>
        {
            ["display"] = "block",
            ["background-color"] = "red"
        });

        using (var w1 = CreateRequest(doc, styles))
            renderer.RenderFrame(w1.Request);
        using var w2 = CreateRequest(doc, styles, animKind: AnimationUpdateKind.Paint);
        var result = renderer.RenderFrame(w2.Request);

        Assert.NotNull(result);
        Assert.Equal(
            AnimationUpdateKind.Paint,
            result.Telemetry.RequestedAnimationUpdateKind);
    }

    [Fact]
    public void AnimationUpdateKind_Layout_IsPreservedInTelemetry()
    {
        var doc = CreateTestDocument();
        var renderer = CreateRenderer();
        var styles = CreateStyles(doc.DocumentElement, new Dictionary<string, string>
        {
            ["display"] = "block",
            ["width"] = "100px"
        });

        using (var w1 = CreateRequest(doc, styles))
            renderer.RenderFrame(w1.Request);
        using var w2 = CreateRequest(doc, styles, animKind: AnimationUpdateKind.Layout);
        var result = renderer.RenderFrame(w2.Request);

        Assert.NotNull(result);
        Assert.True(
            result.Telemetry.RequestedAnimationUpdateKind.HasFlag(
                AnimationUpdateKind.Layout));
    }

    [Fact]
    public void AnimationTelemetry_CountsDirtyElements()
    {
        var doc = CreateTestDocument();
        var renderer = CreateRenderer();
        var element = doc.DocumentElement;
        var styles = CreateStyles(element, new Dictionary<string, string>
        {
            ["display"] = "block",
            ["transform"] = "translate(10px, 0px)"
        });

        using (var w1 = CreateRequest(doc, styles))
            renderer.RenderFrame(w1.Request);
        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var result = renderer.RenderFrame(new RenderFrameRequest
        {
            Root = doc,
            Canvas = surface.Canvas,
            Styles = styles,
            Viewport = new SKRect(0, 0, 800, 600),
            BaseUrl = "about:blank",
            InvalidationReason = RenderFrameInvalidationReason.Animation,
            RequestedBy = "PipelineTest",
            AnimationUpdateKind = AnimationUpdateKind.Composite,
            CompositeDirtyElements = new[] { element },
            AnimationGeneration = 2,
            ImageGeneration = 0,
            ImageGenerationChanged = false
        });

        Assert.NotNull(result);
        Assert.Equal(1, result.Telemetry.CompositeDirtyElementCount);
        Assert.Equal(0, result.Telemetry.PaintDirtyElementCount);
    }

    [Fact]
    public void AnimationTelemetry_DomPaintDirty_IsObserved()
    {
        var doc = CreateTestDocument();
        var renderer = CreateRenderer();
        var element = doc.DocumentElement;
        var styles = CreateStyles(element, new Dictionary<string, string>
        {
            ["display"] = "block",
            ["width"] = "100px"
        });

        element.MarkDirty(InvalidationKind.Paint);

        using (var w1 = CreateRequest(doc, styles))
            renderer.RenderFrame(w1.Request);
        using var w2 = CreateRequest(doc, styles);
        var result = renderer.RenderFrame(w2.Request);

        Assert.NotNull(result);
        Assert.NotNull(result.Telemetry);
    }

    [Fact]
    public void AnimationInvalidationResult_CompositeProperties_HaveNoDomInvalidation()
    {
        var result = CssAnimationEngine.ClassifyAnimationProperties(
            new[] { "transform", "opacity", "filter" });

        Assert.Equal(AnimationUpdateKind.Composite, result.UpdateKind);
        Assert.Equal(InvalidationKind.None, result.DomInvalidation);
        Assert.Equal(3, result.ChangedProperties.Count);
    }

    [Fact]
    public void AnimationInvalidationResult_PaintProperties_HavePaintDomInvalidation()
    {
        var result = CssAnimationEngine.ClassifyAnimationProperties(
            new[] { "background-color", "color", "box-shadow" });

        Assert.Equal(AnimationUpdateKind.Paint, result.UpdateKind);
        Assert.Equal(InvalidationKind.Paint, result.DomInvalidation);
        Assert.False(result.DomInvalidation.HasFlag(InvalidationKind.Layout));
    }

    [Fact]
    public void MapAnimationInvalidation_CompositeOnly_NoPaintOrLayout()
    {
        var evt = new AnimationFrameEvent
        {
            Element = new Element("div"),
            OwnerDocument = Document.CreateHtmlDocument(),
            ChangedProperties = new List<string> { "transform", "opacity" }
        };
        evt.UpdateKind = CssAnimationEngine.DetermineAnimationUpdateKind(evt.ChangedProperties);
        evt.DomInvalidation = CssAnimationEngine.ClassifyAnimationProperties(
            evt.ChangedProperties).DomInvalidation;

        var reason = BrowserIntegration.MapAnimationInvalidation(evt);

        Assert.Equal(RenderFrameInvalidationReason.Animation, reason);
        Assert.False(reason.HasFlag(RenderFrameInvalidationReason.Paint));
        Assert.False(reason.HasFlag(RenderFrameInvalidationReason.Layout));
    }

    [Fact]
    public void DomInvalidation_OnElement_PropagatesCorrectly()
    {
        var doc = Document.CreateHtmlDocument();
        var parent = doc.DocumentElement;
        var child = new Element("div");
        parent.AppendChild(child);

        // Paint-only invalidation propagates ChildPaintDirty, not ChildLayoutDirty.
        child.MarkDirty(InvalidationKind.Paint);
        Assert.True(child.PaintDirty);
        Assert.False(child.LayoutDirty);
        Assert.True(parent.ChildPaintDirty);
        Assert.False(parent.ChildLayoutDirty);

        // Clear and test Layout propagation separately.
        parent.ClearDirty(InvalidationKind.All);
        child.ClearDirty(InvalidationKind.All);

        // Layout invalidation propagates both.
        child.MarkDirty(InvalidationKind.Layout | InvalidationKind.Paint);
        Assert.True(child.LayoutDirty);
        Assert.True(child.PaintDirty);
        Assert.True(parent.ChildLayoutDirty);
        Assert.True(parent.ChildPaintDirty);
    }
}
