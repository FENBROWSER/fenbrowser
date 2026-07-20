using System;
using System.Collections.Generic;
using System.Reflection;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering;

/// <summary>
/// Phase 5 / Phase 6 regression: image repaint/relayout callbacks must be scoped
/// to the owning browsing context and must not also fire the process-global
/// fallback when a scoped callback was available. This prevents one image load
/// from producing duplicate global repaint signals and from waking unrelated tabs.
/// </summary>
public class Phase5ImageScopingTests
{
    [Fact]
    public void ScopedCallback_PreventsGlobalFallback()
    {
        int scopedCalls = 0;
        int globalCalls = 0;

        var context = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = "owner-A",
            RequestRepaint = () => scopedCalls++
        };

        var previousGlobal = ImageLoader.RequestRepaint;
        ImageLoader.RequestRepaint = () => globalCalls++;
        try
        {
            InvokeRepaint(new List<ImageLoader.ImageLoaderRequestContext> { context });

            Assert.Equal(1, scopedCalls);
            Assert.Equal(0, globalCalls);
        }
        finally
        {
            ImageLoader.RequestRepaint = previousGlobal;
        }
    }

    [Fact]
    public void NoScopedCallback_FallsBackToGlobal()
    {
        int globalCalls = 0;
        var previousGlobal = ImageLoader.RequestRepaint;
        ImageLoader.RequestRepaint = () => globalCalls++;
        try
        {
            // Empty context list: only the global fallback should fire.
            InvokeRepaint(new List<ImageLoader.ImageLoaderRequestContext>());
            Assert.Equal(1, globalCalls);
        }
        finally
        {
            ImageLoader.RequestRepaint = previousGlobal;
        }
    }

    [Fact]
    public void NullScopedCallbacks_DoNotThrowAndFallBack()
    {
        int globalCalls = 0;
        var previousGlobal = ImageLoader.RequestRepaint;
        ImageLoader.RequestRepaint = () => globalCalls++;
        try
        {
            var nullContext = new ImageLoader.ImageLoaderRequestContext { OwnerId = "x" };
            InvokeRepaint(new List<ImageLoader.ImageLoaderRequestContext> { nullContext });
            Assert.Equal(1, globalCalls);
        }
        finally
        {
            ImageLoader.RequestRepaint = previousGlobal;
        }
    }

    [Fact]
    public void RepaintAnimatedImageOwners_ScopedOnly_NoGlobalFallback()
    {
        // Phase 5 regression: when an animated GIF is displayed by one owner, the
        // per-frame GIF timer must repaint only that owner and must NOT fall back
        // to the process-global RequestRepaint (which would wake unrelated tabs).
        // The renderer no longer keys its paint signal on the global
        // HasActiveAnimatedImages flag, so this scoping is what keeps tab B idle.
        int scopedCalls = 0;
        int globalCalls = 0;

        var ownerContext = new ImageLoader.ImageLoaderRequestContext
        {
            OwnerId = "tab-A",
            RequestRepaint = () => scopedCalls++
        };

        var previousGlobal = ImageLoader.RequestRepaint;
        ImageLoader.RequestRepaint = () => globalCalls++;

        var ownersField = typeof(ImageLoader)
            .GetField("_animatedGifOwners", BindingFlags.NonPublic | BindingFlags.Static)!;
        var owners = (System.Collections.Concurrent.ConcurrentDictionary<string, ImageLoader.ImageLoaderRequestContext>)ownersField.GetValue(null)!;

        var originalOwners = new System.Collections.Generic.Dictionary<string, ImageLoader.ImageLoaderRequestContext>();
        foreach (var kvp in owners) originalOwners[kvp.Key] = kvp.Value;

        try
        {
            owners["tab-A"] = ownerContext;

            typeof(ImageLoader)
                .GetMethod("RepaintAnimatedImageOwners", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, null);

            Assert.Equal(1, scopedCalls);
            Assert.Equal(0, globalCalls);
        }
        finally
        {
            owners.Clear();
            foreach (var kvp in originalOwners) owners[kvp.Key] = kvp.Value;
            ImageLoader.RequestRepaint = previousGlobal;
        }
    }

    private static void InvokeRepaint(List<ImageLoader.ImageLoaderRequestContext> contexts)
    {
        typeof(ImageLoader)
            .GetMethod("InvokeRepaint", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { contexts });
    }
}
