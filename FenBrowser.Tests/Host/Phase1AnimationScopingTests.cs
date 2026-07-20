using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Host;
using FenBrowser.Host.Tabs;
using Xunit;

namespace FenBrowser.Tests.Host;

/// <summary>
/// Phase 1 regression: animation notifications must be scoped to the document
/// (and active tab) that owns them. The process-global CssAnimationEngine must
/// not wake unrelated tabs.
/// </summary>
public class Phase1AnimationScopingTests
{
    [Fact]
    public void AnimationInTabA_DoesNotRequestFrameInTabB()
    {
        var docA = Document.CreateHtmlDocument();
        var docB = Document.CreateHtmlDocument();

        var elemA = new Element("div");
        var elemB = new Element("div");
        SetOwnerDocument(elemA, docA);
        SetOwnerDocument(elemB, docB);

        using var tabA = new BrowserTab();
        using var tabB = new BrowserTab();
        var biA = tabA.Browser;
        var biB = tabB.Browser;

        SetRoot(biA, elemA);
        SetRoot(biB, elemB);
        ResetFrameState(biA);
        ResetFrameState(biB);

        // An animation event owned by docA is delivered to both integrations'
        // handlers (the engine is global). Only tab A must request a frame.
        var evt = new AnimationFrameEvent
        {
            Element = elemA,
            OwnerDocument = docA,
            Invalidation = InvalidationKind.Paint,
            ChangedProperties = new System.Collections.Generic.List<string> { "opacity" }
        };

        InvokeOnAnimationFrame(biA, evt);
        InvokeOnAnimationFrame(biB, evt);

        Assert.True(GetNeedsRepaint(biA), "Tab A (owning document) should have requested a frame.");
        Assert.False(GetNeedsRepaint(biB), "Tab B (unrelated document) must not request a frame.");
    }

    [Fact]
    public void InactiveTab_ReceivesNoNormalFrequencyFrame()
    {
        var docB = Document.CreateHtmlDocument();
        var elemB = new Element("div");
        SetOwnerDocument(elemB, docB);

        using var tabB = new BrowserTab();
        var biB = tabB.Browser;
        SetRoot(biB, elemB);
        tabB.IsActive = false;
        ResetFrameState(biB);

        var evt = new AnimationFrameEvent
        {
            Element = elemB,
            OwnerDocument = docB,
            Invalidation = InvalidationKind.Paint,
            ChangedProperties = new System.Collections.Generic.List<string> { "opacity" }
        };

        InvokeOnAnimationFrame(biB, evt);

        Assert.False(GetNeedsRepaint(biB), "Inactive tab must not request a normal-frequency frame.");
    }

    [Fact]
    public void DisposedIntegration_IgnoresAnimationEvents()
    {
        var docA = Document.CreateHtmlDocument();
        var elemA = new Element("div");
        SetOwnerDocument(elemA, docA);

        var tabA = new BrowserTab();
        var biA = tabA.Browser;
        SetRoot(biA, elemA);
        ResetFrameState(biA);

        tabA.Dispose();

        var evt = new AnimationFrameEvent
        {
            Element = elemA,
            OwnerDocument = docA,
            Invalidation = InvalidationKind.Paint,
            ChangedProperties = new System.Collections.Generic.List<string> { "opacity" }
        };

        InvokeOnAnimationFrame(biA, evt);

        Assert.False(GetNeedsRepaint(biA), "Disposed integration must ignore animation events.");
    }

    private static void SetOwnerDocument(Element element, Document doc)
    {
        var f = typeof(Node).GetField("_ownerDocument", BindingFlags.NonPublic | BindingFlags.Instance);
        f.SetValue(element, doc);
    }

    private static void SetRoot(BrowserIntegration bi, Element root)
    {
        var f = typeof(BrowserIntegration).GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance);
        f.SetValue(bi, root);
    }

    private static void ResetFrameState(BrowserIntegration bi)
    {
        typeof(BrowserIntegration)
            .GetField("_needsRepaint", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(bi, false);
    }

    private static bool GetNeedsRepaint(BrowserIntegration bi)
    {
        return (bool)typeof(BrowserIntegration)
            .GetField("_needsRepaint", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(bi);
    }

    private static void InvokeOnAnimationFrame(BrowserIntegration bi, AnimationFrameEvent evt)
    {
        var m = typeof(BrowserIntegration).GetMethod(
            "OnAnimationFrame", BindingFlags.NonPublic | BindingFlags.Instance);
        m.Invoke(bi, new object[] { evt });
    }
}
