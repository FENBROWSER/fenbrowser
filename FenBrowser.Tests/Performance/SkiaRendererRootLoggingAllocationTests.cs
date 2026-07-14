using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Tests.Logging;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Performance;

[Collection(EngineLogTestCollection.Name)]
public sealed class SkiaRendererRootLoggingAllocationTests
{
    [Fact]
    public void Render_FilteredRootDiagnosticsStayWithinAllocationBudget()
    {
        const int rootCount = 100;
        const int iterations = 10;
        const long allocationBudget = 21_000;

        var wasEnabled = EngineLogCompat.IsEnabled;
        try
        {
            EngineLogCompat.IsEnabled = true;
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = true,
                GlobalMinimumSeverity = LogSeverity.Info,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = false
            });

            var roots = new List<PaintNodeBase>(rootCount);
            for (var index = 0; index < rootCount; index++)
            {
                roots.Add(new CustomPaintNode
                {
                    Bounds = new SKRect(10 + index, 10, 11 + index, 11)
                });
            }

            var tree = new ImmutablePaintTree(roots);
            var renderer = new SkiaRenderer();
            var viewport = new SKRect(0, 0, 1, 1);
            using var surface = SKSurface.Create(new SKImageInfo(1, 1));

            for (var warmup = 0; warmup < 3; warmup++)
            {
                renderer.Render(surface.Canvas, tree, viewport, SKColors.White, captureDebugScreenshot: false);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                renderer.Render(surface.Canvas, tree, viewport, SKColors.White, captureDebugScreenshot: false);
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(
                allocated <= allocationBudget,
                $"Allocated {allocated:N0} bytes; budget is {allocationBudget:N0} bytes.");
        }
        finally
        {
            EngineLogCompat.IsEnabled = wasEnabled;
        }
    }
}
