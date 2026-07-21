using System.Collections.Generic;
using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Host;
using FenBrowser.Host.Tabs;
using Xunit;

namespace FenBrowser.Tests.Host;

/// <summary>
/// Phase 2 regression: a free-running animation timer must not stack unbounded
/// frame requests on a slow renderer. Each integration coalesces animation ticks
/// into at most one in-flight frame plus one pending follow-up.
/// </summary>
public class Phase2AnimationCoalescingTests
{
    [Fact]
    public void RapidAnimationTicks_CoalesceToSingleInFlightFrame()
    {
        var doc = Document.CreateHtmlDocument();
        var elem = new Element("div");
        SetOwnerDocument(elem, doc);

        using var tab = new BrowserTab();
        var bi = tab.Browser;
        SetRoot(bi, elem);
        tab.IsActive = true;
        ResetFrameState(bi);
        // Stop the background engine loop so it cannot call CompleteAnimationFrame
        // and race with the deterministic single-flight assertions below.
        SetRunning(bi, false);

        var evt = new AnimationFrameEvent
        {
            Element = elem,
            OwnerDocument = doc,
            Invalidation = InvalidationKind.Paint,
            ChangedProperties = new List<string> { "opacity" }
        };

        // Simulate 1000 animation ticks arriving while no frame has committed yet.
        for (int i = 0; i < 1000; i++)
        {
            InvokeOnAnimationFrame(bi, evt);
        }

        // Exactly one frame was requested (needsRepaint set once), and the gate
        // never accumulated more than one in-flight frame. Phase 3: pending bits
        // now carry typed reason flags, not a bare boolean.
        Assert.True(GetNeedsRepaint(bi));
        Assert.Equal(1, GetInt(bi, "_animationFrameInFlight"));
        // The pending reason bits must be non-zero (Animation+Paint from 999 merged ticks).
        int pendingReason = GetInt(bi, "_pendingAnimationReasonBits");
        Assert.NotEqual(0, pendingReason);

        // Simulate the engine thread committing the frame: the pending follow-up
        // is released as exactly one additional frame request.
        InvokeCompleteAnimationFrame(bi);
        Assert.Equal(0, GetInt(bi, "_animationFrameInFlight"));
        Assert.Equal(0, GetInt(bi, "_pendingAnimationReasonBits"));
        Assert.True(GetNeedsRepaint(bi));

        // Committing again with no pending work must not request more frames.
        InvokeCompleteAnimationFrame(bi);
        Assert.Equal(0, GetInt(bi, "_animationFrameInFlight"));
        Assert.Equal(0, GetInt(bi, "_pendingAnimationReasonBits"));
    }

    [Fact]
    public void InactiveTab_TicksNeverRequestFrames()
    {
        var doc = Document.CreateHtmlDocument();
        var elem = new Element("div");
        SetOwnerDocument(elem, doc);

        using var tab = new BrowserTab();
        var bi = tab.Browser;
        SetRoot(bi, elem);
        tab.IsActive = false;
        ResetFrameState(bi);
        SetRunning(bi, false);

        var evt = new AnimationFrameEvent
        {
            Element = elem,
            OwnerDocument = doc,
            Invalidation = InvalidationKind.Paint,
            ChangedProperties = new List<string> { "opacity" }
        };

        for (int i = 0; i < 1000; i++)
        {
            InvokeOnAnimationFrame(bi, evt);
        }

        Assert.False(GetNeedsRepaint(bi));
        Assert.Equal(0, GetInt(bi, "_animationFrameInFlight"));
        Assert.Equal(0, GetInt(bi, "_pendingAnimationReasonBits"));
        Assert.Equal(0, GetInt(bi, "_pendingAnimationUpdateKindBits"));
    }

    private static void SetOwnerDocument(Element element, Document doc)
    {
        typeof(Node).GetField("_ownerDocument", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(element, doc);
    }

    private static void SetRoot(BrowserIntegration bi, Element root)
    {
        typeof(BrowserIntegration).GetField("_root", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(bi, root);
    }

    private static void ResetFrameState(BrowserIntegration bi)
    {
        typeof(BrowserIntegration).GetField("_needsRepaint", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(bi, false);
    }

    private static bool GetNeedsRepaint(BrowserIntegration bi)
    {
        return (bool)typeof(BrowserIntegration)
            .GetField("_needsRepaint", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(bi)!;
    }

    private static int GetInt(BrowserIntegration bi, string name)
    {
        return (int)typeof(BrowserIntegration)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(bi)!;
    }

    private static void InvokeOnAnimationFrame(BrowserIntegration bi, AnimationFrameEvent evt)
    {
        typeof(BrowserIntegration)
            .GetMethod("OnAnimationFrame", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(bi, new object[] { evt });
    }

    private static void SetRunning(BrowserIntegration bi, bool value)
    {
        typeof(BrowserIntegration).GetField("_running", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(bi, value);
    }

    private static void InvokeCompleteAnimationFrame(BrowserIntegration bi)
    {
        typeof(BrowserIntegration)
            .GetMethod("CompleteAnimationFrame", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(bi, null);
    }
}
