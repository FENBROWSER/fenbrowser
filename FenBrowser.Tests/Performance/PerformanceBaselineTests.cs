using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering.Performance;
using Xunit;

namespace FenBrowser.Tests.Performance;

/// <summary>
/// Phase 0 — repeatable performance baseline. Exercises the scenario pages
/// defined in RenderPerformanceBenchmarkRunner.BuildPerformanceBaselineSuite
/// and asserts the global counters are captured deterministically. These
/// counters become the regression signals for later phases (paint-tree
/// rebuilds, full rasters, hidden-tab frames, no-op frames, etc.).
/// </summary>
public class PerformanceBaselineTests
{
    [Fact]
    public async Task StaticPage_CapturesBaselineCounters()
    {
        var runner = new RenderPerformanceBenchmarkRunner();
        var scenario = Find("baseline.static");
        var result = await runner.RunScenarioAsync(scenario);

        Assert.True(result.CommittedFrames >= 1);
        Assert.Equal(result.CommittedFrames, result.FrameRequests);
        // First frame must build the paint tree and perform a full raster.
        Assert.True(result.PaintTreeRebuilds >= 1);
        Assert.True(result.FullRasters >= 1);
        // Paint-tree rebuilds must never exceed committed frames.
        Assert.True(result.PaintTreeRebuilds <= result.CommittedFrames);
    }

    [Fact]
    public async Task AnimationPages_RecordCounters()
    {
        var runner = new RenderPerformanceBenchmarkRunner();
        foreach (var name in new[]
        {
            "baseline.opacity-animation",
            "baseline.transform-animation",
            "baseline.background-color-animation",
            "baseline.width-animation",
            "baseline.animated-gif"
        })
        {
            var result = await runner.RunScenarioAsync(Find(name));
            Assert.True(result.CommittedFrames >= 1, $"{name}: committed frames");
            Assert.True(result.PaintTreeRebuilds >= 1, $"{name}: paint tree rebuilt at least once");
            Assert.True(result.FullRasters >= 1, $"{name}: initial full raster");
        }
    }

    [Fact]
    public async Task NoOpRepaintReady_SteadyStateCapturesCounters()
    {
        var runner = new RenderPerformanceBenchmarkRunner();
        var scenario = Find("baseline.no-op-repaint-ready");
        Assert.True(scenario.PreferSteadyStateDamage);

        var result = await runner.RunScenarioAsync(scenario);

        Assert.True(result.CommittedFrames >= 1);
        Assert.Equal(result.CommittedFrames, result.FrameRequests);
        Assert.True(result.PaintTreeRebuilds >= 1);
        // Every committed frame must be accounted for by exactly one raster mode.
        Assert.Equal(
            result.CommittedFrames,
            result.FullRasters + result.DamageRasters + result.CompositorOnlyUpdates);
    }

    [Fact]
    public async Task BaselineSuite_AllScenariosProduceResults()
    {
        var runner = new RenderPerformanceBenchmarkRunner();
        var suite = RenderPerformanceBenchmarkRunner.BuildPerformanceBaselineSuite();

        Assert.True(suite.Count >= 9, "Phase 0 requires the 9+ baseline scenarios.");

        foreach (var scenario in suite)
        {
            var result = await runner.RunScenarioAsync(scenario);
            Assert.True(result.CommittedFrames >= 1, $"{scenario.Name}: committed frames");
            Assert.True(result.FailureGatePassed, $"{scenario.Name}: within failure gate");
        }
    }

    private static RenderPerformanceBenchmarkScenario Find(string name)
    {
        var suite = RenderPerformanceBenchmarkRunner.BuildPerformanceBaselineSuite();
        var scenario = suite.FirstOrDefault(s => s.Name == name);
        Assert.True(scenario != null, $"baseline scenario '{name}' must exist");
        return scenario;
    }
}
