using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Host;
using FenBrowser.Host.Tabs;
using Xunit;

namespace FenBrowser.Tests.Host;

/// <summary>
/// Phase 17 final integration regression: a three-tab workload (one active
/// animated tab, one inactive complex static tab, one inactive animated-GIF tab)
/// must exhibit the target cross-tab isolation and background-throttling behavior:
///   * an animation owned by the active tab wakes only the active tab;
///   * inactive tabs request zero animation-triggered frames;
///   * a compositor-only animation (transform/opacity) requests an Animation-only
///     frame (no Paint/Layout bits) on the active tab.
/// This composes the Phase 1 / Phase 3 / Phase 16 guarantees into the multi-tab
/// scenario described by the performance-remediation acceptance criteria.
/// </summary>
public class Phase17MultiTabPerformanceTests
{
    [Fact]
    public void ActiveAnimatedTab_WakesOnlyItself_InactiveTabsStayIdle()
    {
        var docA = Document.CreateHtmlDocument();
        var docB = Document.CreateHtmlDocument();
        var docC = Document.CreateHtmlDocument();

        var elemA = new Element("div");
        var elemB = new Element("div");
        var elemC = new Element("div");
        SetOwnerDocument(elemA, docA);
        SetOwnerDocument(elemB, docB);
        SetOwnerDocument(elemC, docC);

        // Tab A: active, animated. Tabs B and C: inactive (background).
        using var tabA = new BrowserTab();
        using var tabB = new BrowserTab();
        using var tabC = new BrowserTab();

        var biA = tabA.Browser;
        var biB = tabB.Browser;
        var biC = tabC.Browser;

        SetRoot(biA, elemA);
        SetRoot(biB, elemB);
        SetRoot(biC, elemC);

        tabA.IsActive = true;
        tabB.IsActive = false;
        tabC.IsActive = false;

        ResetFrameState(biA);
        ResetFrameState(biB);
        ResetFrameState(biC);

        // Tab A's transform animation ticks. Because the engine is process-global,
        // the event is delivered to every integration handler.
        var animationA = new AnimationFrameEvent
        {
            Element = elemA,
            OwnerDocument = docA,
            Invalidation = InvalidationKind.Paint,
            UpdateKind = AnimationUpdateKind.Composite,
            ChangedProperties = new System.Collections.Generic.List<string> { "transform" }
        };

        for (int i = 0; i < 30; i++)
        {
            InvokeOnAnimationFrame(biA, animationA);
            InvokeOnAnimationFrame(biB, animationA);
            InvokeOnAnimationFrame(biC, animationA);
        }

        Assert.True(GetNeedsRepaint(biA), "Active animated tab A must request frames.");
        Assert.False(GetNeedsRepaint(biB), "Inactive static tab B must request zero animation frames.");
        Assert.False(GetNeedsRepaint(biC), "Inactive GIF tab C must request zero animation frames.");
    }

    [Fact]
    public void CompositorOnlyAnimation_OnActiveTab_RequestsAnimationOnly_NoPaintOrLayout()
    {
        var docA = Document.CreateHtmlDocument();
        var elemA = new Element("div");
        SetOwnerDocument(elemA, docA);

        using var tabA = new BrowserTab();
        var biA = tabA.Browser;
        SetRoot(biA, elemA);
        tabA.IsActive = true;
        ResetFrameState(biA);
        SetPendingInvalidation(biA, RenderFrameInvalidationReason.None);

        var animation = new AnimationFrameEvent
        {
            Element = elemA,
            OwnerDocument = docA,
            Invalidation = InvalidationKind.Paint,
            UpdateKind = AnimationUpdateKind.Composite,
            ChangedProperties = new System.Collections.Generic.List<string> { "transform" }
        };

        InvokeOnAnimationFrame(biA, animation);

        var pending = GetPendingInvalidation(biA);
        Assert.True(pending.HasFlag(RenderFrameInvalidationReason.Animation),
            "Compositor-only animation must request an Animation frame.");
        Assert.False(pending.HasFlag(RenderFrameInvalidationReason.Paint),
            "Compositor-only animation must not force a Paint invalidation.");
        Assert.False(pending.HasFlag(RenderFrameInvalidationReason.Layout),
            "Compositor-only animation must not force a Layout invalidation.");
    }

    [Fact]
    public void LayoutAnimation_OnActiveTab_EscalatesToLayoutAndPaint()
    {
        var docA = Document.CreateHtmlDocument();
        var elemA = new Element("div");
        SetOwnerDocument(elemA, docA);

        using var tabA = new BrowserTab();
        var biA = tabA.Browser;
        SetRoot(biA, elemA);
        tabA.IsActive = true;
        ResetFrameState(biA);
        SetPendingInvalidation(biA, RenderFrameInvalidationReason.None);

        var animation = new AnimationFrameEvent
        {
            Element = elemA,
            OwnerDocument = docA,
            Invalidation = InvalidationKind.Layout | InvalidationKind.Paint,
            UpdateKind = AnimationUpdateKind.Layout,
            ChangedProperties = new System.Collections.Generic.List<string> { "width" }
        };

        InvokeOnAnimationFrame(biA, animation);

        var pending = GetPendingInvalidation(biA);
        Assert.True(pending.HasFlag(RenderFrameInvalidationReason.Layout));
        Assert.True(pending.HasFlag(RenderFrameInvalidationReason.Paint));
    }

    private static void SetOwnerDocument(Element element, Document doc)
    {
        typeof(Node)
            .GetField("_ownerDocument", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(element, doc);
    }

    private static void SetRoot(BrowserIntegration bi, Element root)
    {
        typeof(BrowserIntegration)
            .GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(bi, root);
    }

    private static void ResetFrameState(BrowserIntegration bi)
    {
        typeof(BrowserIntegration)
            .GetField("_needsRepaint", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(bi, false);
        typeof(BrowserIntegration)
            .GetField("_animationFrameInFlight", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(bi, 0);
        typeof(BrowserIntegration)
            .GetField("_animationFramePending", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(bi, 0);
    }

    private static bool GetNeedsRepaint(BrowserIntegration bi)
    {
        return (bool)typeof(BrowserIntegration)
            .GetField("_needsRepaint", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(bi);
    }

    private static void SetPendingInvalidation(BrowserIntegration bi, RenderFrameInvalidationReason reason)
    {
        typeof(BrowserIntegration)
            .GetField("_pendingInvalidationReasons", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(bi, reason);
    }

    private static RenderFrameInvalidationReason GetPendingInvalidation(BrowserIntegration bi)
    {
        return (RenderFrameInvalidationReason)typeof(BrowserIntegration)
            .GetField("_pendingInvalidationReasons", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(bi);
    }

    private static void InvokeOnAnimationFrame(BrowserIntegration bi, AnimationFrameEvent evt)
    {
        typeof(BrowserIntegration)
            .GetMethod("OnAnimationFrame", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(bi, new object[] { evt });
    }
}
