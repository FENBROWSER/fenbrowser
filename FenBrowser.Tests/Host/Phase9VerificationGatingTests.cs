using System.Reflection;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Host;
using FenBrowser.Host.Tabs;
using Xunit;

namespace FenBrowser.Tests.Host;

/// <summary>
/// Phase 9 regression: verification reports / debug screenshots must be OFF for
/// normal browsing frames (including generic Paint and Animation invalidations)
/// and only enabled by an explicit one-shot <see cref="VerificationRequest"/>.
/// </summary>
public sealed class Phase9VerificationGatingTests
{
    [Fact]
    public void NormalPaintFrame_DoesNotEmitVerification()
    {
        using var tab = new BrowserTab();
        var bi = tab.Browser;

        Assert.False(ShouldEmit(bi, RenderFrameInvalidationReason.Paint));
        Assert.False(ShouldEmit(bi, RenderFrameInvalidationReason.Animation));
        Assert.False(ShouldEmit(bi, RenderFrameInvalidationReason.Dom));
        Assert.False(ShouldEmit(bi, RenderFrameInvalidationReason.Navigation));
    }

    [Fact]
    public void ExplicitVerificationRequest_EmitsExactlyOneFrame()
    {
        using var tab = new BrowserTab();
        var bi = tab.Browser;

        bi.RequestVerification(new VerificationRequest
        {
            Enabled = true,
            Reason = "unit-test",
            CaptureScreenshot = true
        });

        // First frame after the request consumes it and emits verification.
        Assert.True(ShouldEmit(bi, RenderFrameInvalidationReason.Paint));
        // The one-shot token is consumed: subsequent frames do not emit.
        Assert.False(ShouldEmit(bi, RenderFrameInvalidationReason.Paint));
    }

    [Fact]
    public void DisabledVerificationRequest_IsIgnored()
    {
        using var tab = new BrowserTab();
        var bi = tab.Browser;

        bi.RequestVerification(new VerificationRequest { Enabled = false });
        Assert.False(ShouldEmit(bi, RenderFrameInvalidationReason.Paint));
    }

    private static bool ShouldEmit(BrowserIntegration bi, RenderFrameInvalidationReason reason)
    {
        var m = typeof(BrowserIntegration).GetMethod(
            "ShouldEmitVerificationReport",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (bool)m.Invoke(bi, new object[] { reason });
    }
}
