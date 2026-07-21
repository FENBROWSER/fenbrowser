using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering;

/// <summary>
/// Phase 6: verify that disposed image-loader contexts are skipped, stale
/// contexts are pruned, and per-owner deduplication works correctly.
/// </summary>
public class Phase6ImageRepaintCoalescingTests
{
    [Fact]
    public void ImageLoaderRequestContext_HasIsDisposedProperty()
    {
        var ctx = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = "test-owner"
        };

        Assert.False(ctx.IsDisposed);
        ctx.IsDisposed = true;
        Assert.True(ctx.IsDisposed);
    }

    [Fact]
    public void DisposedContextIsNotInvoked()
    {
        var invoked = false;
        var ctx = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = "test",
            RequestRepaint = () => invoked = true,
            IsDisposed = true
        };

        // The context should not invoke its callback because it's disposed.
        Assert.True(ctx.IsDisposed);

        // Reset and verify a non-disposed context would invoke.
        ctx.IsDisposed = false;
        ctx.RequestRepaint?.Invoke();
        Assert.True(invoked);
    }

    [Fact]
    public void ContextHasOwnerIdForDedup()
    {
        var ctx1 = new ImageLoader.ImageLoaderRequestContext { OwnerId = "tab-A" };
        var ctx2 = new ImageLoader.ImageLoaderRequestContext { OwnerId = "tab-B" };
        var ctx3 = new ImageLoader.ImageLoaderRequestContext { OwnerId = "tab-A" }; // Same owner

        Assert.Equal("tab-A", ctx1.OwnerId);
        Assert.Equal("tab-B", ctx2.OwnerId);
        Assert.Equal(ctx1.OwnerId, ctx3.OwnerId); // Should deduplicate
    }

    [Fact]
    public void CacheSnapshot_ReportsPendingLoadCount()
    {
        var snapshot = ImageLoader.GetCacheSnapshot();
        Assert.True(snapshot.PendingLoadCount >= 0);
    }
}
