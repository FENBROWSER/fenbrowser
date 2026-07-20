using System.Reflection;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Host;
using FenBrowser.Host.Tabs;
using Xunit;

namespace FenBrowser.Tests.Host;

/// <summary>
/// Phase 14 regression: frame-request bookkeeping must stay bounded and coalesced.
/// The accumulated "requestedBy" source string must not grow without limit when
/// many distinct sources request frames for one still-pending frame.
/// </summary>
public sealed class Phase14FrameRequestCoalescingTests
{
    [Fact]
    public void MergeInvalidationSource_IsBounded()
    {
        var merge = typeof(BrowserIntegration).GetMethod(
            "MergeInvalidationSource",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(merge);

        string acc = "idle";
        for (int i = 0; i < 1000; i++)
        {
            acc = (string)merge.Invoke(null, new object[] { acc, "source" + i });
        }

        var tokens = acc.Split('|');
        // At most the tracked cap plus the single "+more" sentinel token.
        Assert.True(tokens.Length <= 9, $"Source string grew unbounded: {tokens.Length} tokens.");
        Assert.Contains("+more", acc);
    }

    [Fact]
    public void RepeatedIdenticalSource_DoesNotDuplicate()
    {
        var merge = typeof(BrowserIntegration).GetMethod(
            "MergeInvalidationSource",
            BindingFlags.NonPublic | BindingFlags.Static);

        string acc = "idle";
        for (int i = 0; i < 100; i++)
        {
            acc = (string)merge.Invoke(null, new object[] { acc, "CssAnimationEngine" });
        }

        Assert.Equal("CssAnimationEngine", acc);
    }

    [Fact]
    public void RepeatedRequestFrame_CoalescesReasons()
    {
        using var tab = new BrowserTab();
        var bi = tab.Browser;

        var request = typeof(BrowserIntegration).GetMethod(
            "RequestFrame",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // Reset pending state.
        typeof(BrowserIntegration).GetField("_needsRepaint", BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(bi, false);

        for (int i = 0; i < 500; i++)
        {
            request.Invoke(bi, new object[] { RenderFrameInvalidationReason.Paint, "loop", false });
        }
        request.Invoke(bi, new object[] { RenderFrameInvalidationReason.Dom, "dom", false });

        var reasons = (RenderFrameInvalidationReason)typeof(BrowserIntegration)
            .GetField("_pendingInvalidationReasons", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue(bi);

        // Both reasons merged into the single pending bitset.
        Assert.True((reasons & RenderFrameInvalidationReason.Paint) != 0);
        Assert.True((reasons & RenderFrameInvalidationReason.Dom) != 0);
    }
}
