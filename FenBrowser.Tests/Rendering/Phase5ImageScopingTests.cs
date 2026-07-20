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

    private static void InvokeRepaint(List<ImageLoader.ImageLoaderRequestContext> contexts)
    {
        typeof(ImageLoader)
            .GetMethod("InvokeRepaint", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { contexts });
    }
}
