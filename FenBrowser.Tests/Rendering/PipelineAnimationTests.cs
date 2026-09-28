using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
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

    [Fact]
    public async Task RenderFrame_StartsAnimationDeclaredOnlyByShorthand()
    {
        const string htmlSource = "<!doctype html><html><head><style>@keyframes spin { to { transform: rotate(360deg); } } #target { animation: spin .75s linear infinite; }</style></head><body><div id='target'></div></body></html>";

        var baseUri = new Uri("https://test.local/");
        var parser = new HtmlParser(htmlSource, baseUri);
        var doc = parser.Parse();
        var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var target = doc.GetElementById("target");
        var styles = await CssLoader.ComputeAsync(html, baseUri, _ => Task.FromResult(string.Empty), viewportWidth: 128, viewportHeight: 128);
        var renderer = CreateRenderer();

        using var bitmap = new SKBitmap(128, 128);
        using var canvas = new SKCanvas(bitmap);
        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = html,
            Canvas = canvas,
            Styles = styles,
            Viewport = new SKRect(0, 0, 128, 128),
            BaseUrl = baseUri.AbsoluteUri,
            InvalidationReason = RenderFrameInvalidationReason.Style,
            RequestedBy = "PipelineAnimationTests.ShorthandAnimation",
            EmitVerificationReport = false
        });

        var animationEngineField = typeof(SkiaDomRenderer).GetField(
            "_animationEngine",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(animationEngineField);
        var animationEngine = Assert.IsType<FenBrowser.FenEngine.Rendering.CssAnimationEngine>(
            animationEngineField.GetValue(renderer));
        Assert.True(animationEngine.HasActiveAnimations(target));
    }

    [Fact]
    public async Task RenderFrame_StartsAnimationAfterInPlaceRecascadeRevealsElement()
    {
        const string source = "<!doctype html><style>@keyframes revealSpin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } } #target { display:none; width:28px; height:28px; animation:revealSpin 2s linear infinite; }</style><div id='target'></div>";
        var uri = new Uri("https://animation-reveal.test/");
        var doc = new HtmlParser(source, uri).Parse();
        var root = doc.DocumentElement;
        var target = doc.GetElementById("target");
        var styles = await CssLoader.ComputeAsync(root, uri, _ => Task.FromResult(string.Empty), 128, 128);
        var renderer = CreateRenderer();
        var engine = renderer.AnimationEngine;
        var now = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
        var previousClock = CssAnimationEngine.NowProvider;
        using var bitmap = new SKBitmap(128, 128);
        using var canvas = new SKCanvas(bitmap);
        try
        {
            CssAnimationEngine.NowProvider = () => now;
            var request = new RenderFrameRequest
            {
                Root = root, Canvas = canvas, Styles = styles,
                Viewport = new SKRect(0, 0, 128, 128), BaseUrl = uri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Style,
                EmitVerificationReport = false
            };
            renderer.RenderFrame(request);
            Assert.False(engine.HasActiveAnimations(target));

            // Incremental recascade publishes into the existing dictionary and
            // consumes StyleDirty, leaving layout/paint invalidation for rendering.
            var visible = styles[target].Clone();
            CssStyleApplicator.ApplyProperty(visible, "display", "block");
            styles[target] = visible;
            target.SetComputedStyle(visible);
            target.MarkDirty(InvalidationKind.Layout | InvalidationKind.Paint);
            Assert.False(root.StyleDirty || root.ChildStyleDirty);
            request.InvalidationReason = RenderFrameInvalidationReason.Layout | RenderFrameInvalidationReason.Paint;
            renderer.RenderFrame(request);
            Assert.True(engine.HasActiveAnimations(target));

            engine.Stop();
            now = now.AddMilliseconds(500);
            InvokeAnimationTick(engine);
            var first = engine.GetAnimatedProperties(target)["transform"];
            now = now.AddMilliseconds(500);
            InvokeAnimationTick(engine);
            Assert.NotEqual(first, engine.GetAnimatedProperties(target)["transform"]);
        }
        finally
        {
            engine.Stop();
            CssAnimationEngine.NowProvider = previousClock;
        }
    }

    [Fact]
    public async Task ComputeAsync_RegistersCachedKeyframesForEachDocument()
    {
        const string htmlSource = "<!doctype html><html><head><style>@keyframes iframeCacheSpin_6f17 { to { transform: rotate(360deg); } } .spinner { animation: iframeCacheSpin_6f17 .75s linear infinite; }</style></head><body><span class='spinner'></span></body></html>";
        var baseUri = new Uri("https://iframe-animation-cache.test/anchor.html");

        var first = new HtmlParser(htmlSource, baseUri).Parse();
        await CssLoader.ComputeAsync(
            first.DocumentElement,
            baseUri,
            _ => Task.FromResult(string.Empty),
            viewportWidth: 128,
            viewportHeight: 128);
        Assert.NotNull(CssLoader.GetKeyframes("iframeCacheSpin_6f17", first.QuerySelector(".spinner")));

        // The second document intentionally has identical CSS and base URI so its
        // rules come from the process-wide parse cache.
        var second = new HtmlParser(htmlSource, baseUri).Parse();
        await CssLoader.ComputeAsync(
            second.DocumentElement,
            baseUri,
            _ => Task.FromResult(string.Empty),
            viewportWidth: 128,
            viewportHeight: 128);

        Assert.NotNull(CssLoader.GetKeyframes("iframeCacheSpin_6f17", second.QuerySelector(".spinner")));
    }

    [Fact]
    public async Task CssAnimationEngine_ForwardsFillRetainsFinalEffectWithoutRemainingActive()
    {
        const string htmlSource = "<!doctype html><html><head><style>@keyframes reveal { from { opacity: 0; } to { opacity: 1; } } #target { opacity: 0; animation: reveal 100ms linear 1 forwards; }</style></head><body><div id='target'></div></body></html>";
        var now = new DateTime(2026, 8, 25, 0, 0, 0, DateTimeKind.Utc);
        var previousNowProvider = CssAnimationEngine.NowProvider;
        var engine = new CssAnimationEngine();

        try
        {
            CssAnimationEngine.NowProvider = () => now;
            var baseUri = new Uri("https://test.local/");
            var parser = new HtmlParser(htmlSource, baseUri);
            var doc = parser.Parse();
            var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var target = doc.GetElementById("target");
            var styles = await CssLoader.ComputeAsync(html, baseUri, _ => Task.FromResult(string.Empty), viewportWidth: 128, viewportHeight: 128);
            target.SetComputedStyle(styles[target]);
            var animationEndCount = 0;
            engine.OnAnimationEnd += (_, _) => animationEndCount++;

            engine.StartAnimation(target, styles[target]);
            var active = GetAnimations(engine, target).Single();
            Assert.Equal(100d, active.DurationMs);
            Assert.Equal(1, active.IterationCount);
            Assert.Equal("forwards", active.FillMode);
            engine.Stop();
            now = now.AddMilliseconds(150);
            InvokeAnimationTick(engine);

            string timeline = null;
            target.GetComputedStyle()?.Map.TryGetValue("animation-timeline", out timeline);
            Assert.True(active.IsComplete, $"start={active.StartTime:o}, now={now:o}, provider={CssAnimationEngine.NowProvider():o}, play={active.PlayState}, delay={active.DelayMs}, connected={target.IsConnected}, display={target.GetComputedStyle()?.Display}, timeline={timeline}");
            Assert.False(engine.HasActiveAnimations(target));
            Assert.Contains(target, engine.GetAllActiveAnimationElements());
            Assert.Equal("1", engine.GetAnimatedProperties(target)["opacity"]);
            Assert.Equal(1, animationEndCount);

            InvokeAnimationTick(engine);
            Assert.Equal("1", engine.GetAnimatedProperties(target)["opacity"]);
            Assert.Equal(1, animationEndCount);

            styles[target].Map["animation-name"] = "none";
            styles[target].Map.Remove("animation");
            engine.StartAnimation(target, styles[target]);
            Assert.DoesNotContain(target, engine.GetAllActiveAnimationElements());
            Assert.Empty(engine.GetAnimatedProperties(target));
        }
        finally
        {
            engine.Stop();
            CssAnimationEngine.NowProvider = previousNowProvider;
        }
    }

    // CSS Animations 1 §4: a finished animation does not run again while its name
    // stays applied. The renderer offers every animated element to StartAnimation
    // on each layout-dirty frame, so without this x.com's toast container replayed
    // its one-shot slideUp - a `bottom` animation, so a full layout - forever.
    [Fact]
    public async Task CssAnimationEngine_FinishedAnimationDoesNotRestartWhileItsNameStillApplies()
    {
        const string htmlSource = "<!doctype html><html><head><style>@keyframes slideUp { from { bottom: 0; opacity: 0; } to { bottom: 16px; opacity: 1; } } #target { position: fixed; bottom: 16px; animation: slideUp 300ms ease-out; }</style></head><body><div id='target'></div></body></html>";
        var now = new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        var previousNowProvider = CssAnimationEngine.NowProvider;
        var engine = new CssAnimationEngine();

        try
        {
            CssAnimationEngine.NowProvider = () => now;
            var baseUri = new Uri("https://test.local/");
            var doc = new HtmlParser(htmlSource, baseUri).Parse();
            var html = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var target = doc.GetElementById("target");
            var styles = await CssLoader.ComputeAsync(html, baseUri, _ => Task.FromResult(string.Empty), viewportWidth: 128, viewportHeight: 128);
            target.SetComputedStyle(styles[target]);

            engine.StartAnimation(target, styles[target]);
            engine.Stop();
            Assert.True(engine.HasActiveAnimations(target));

            now = now.AddMilliseconds(400);
            InvokeAnimationTick(engine);
            Assert.False(engine.HasActiveAnimations(target));

            // A later frame offers the element again with the same declaration.
            engine.StartAnimation(target, styles[target]);
            engine.Stop();
            Assert.False(engine.HasActiveAnimations(target));
            Assert.DoesNotContain(target, engine.GetAllActiveAnimationElements());

            // Removing the name and applying it again starts a new animation.
            styles[target].Map["animation-name"] = "none";
            styles[target].Map.Remove("animation");
            engine.StartAnimation(target, styles[target]);
            styles[target].Map["animation"] = "slideUp 300ms ease-out";
            styles[target].Map.Remove("animation-name");
            engine.StartAnimation(target, styles[target]);
            engine.Stop();
            Assert.True(engine.HasActiveAnimations(target));
        }
        finally
        {
            engine.Stop();
            CssAnimationEngine.NowProvider = previousNowProvider;
        }
    }

    [Fact]
    public async Task CssAnimationEngine_StylePlayStateChangesPreserveProgress()
    {
        var uri = new Uri("https://animation-pause.test/");
        var doc = new HtmlParser("<style>@keyframes pauseSpin { from { transform:rotate(0deg); } to { transform:rotate(360deg); } } #target { animation:pauseSpin 2s linear infinite; animation-play-state:paused; }</style><div id='target'></div>", uri).Parse();
        var styles = await CssLoader.ComputeAsync(doc.DocumentElement, uri, _ => Task.FromResult(string.Empty), 128, 128);
        var target = doc.GetElementById("target");
        var style = styles[target];
        var engine = new CssAnimationEngine();
        var now = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
        var previousClock = CssAnimationEngine.NowProvider;
        try
        {
            CssAnimationEngine.NowProvider = () => now;
            engine.StartAnimation(target, style);
            engine.Stop();
            var animation = GetAnimations(engine, target).Single();
            now = now.AddSeconds(10);
            style.Map["animation-play-state"] = "running";
            engine.StartAnimation(target, style);
            engine.Stop();
            Assert.Same(animation, GetAnimations(engine, target).Single());
            Assert.Equal("running", animation.PlayState);
            Assert.Equal(now, animation.StartTime);

            now = now.AddMilliseconds(500);
            InvokeAnimationTick(engine);
            var first = engine.GetAnimatedProperties(target)["transform"];
            style.Map["animation-play-state"] = "paused";
            engine.StartAnimation(target, style);
            engine.Stop();
            now = now.AddSeconds(10);
            InvokeAnimationTick(engine);
            Assert.Equal(first, engine.GetAnimatedProperties(target)["transform"]);
            style.Map["animation-play-state"] = "running";
            engine.StartAnimation(target, style);
            engine.Stop();
            Assert.Equal(500d, (now - animation.StartTime).TotalMilliseconds);
            now = now.AddMilliseconds(500);
            InvokeAnimationTick(engine);
            Assert.NotEqual(first, engine.GetAnimatedProperties(target)["transform"]);
        }
        finally
        {
            engine.Stop();
            CssAnimationEngine.NowProvider = previousClock;
        }
    }

    private static void InvokeAnimationTick(CssAnimationEngine engine)
    {
        var runningField = typeof(CssAnimationEngine).GetField("_isRunning", BindingFlags.Instance | BindingFlags.NonPublic);
        var tick = typeof(CssAnimationEngine).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(runningField);
        Assert.NotNull(tick);
        runningField.SetValue(engine, true);
        tick.Invoke(engine, new object[] { null });
    }

    private static List<CssAnimationEngine.ActiveAnimation> GetAnimations(CssAnimationEngine engine, Element element)
    {
        var animationsField = typeof(CssAnimationEngine).GetField("_activeAnimations", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(animationsField);
        var animations = Assert.IsType<Dictionary<Element, List<CssAnimationEngine.ActiveAnimation>>>(animationsField.GetValue(engine));
        return animations[element];
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
