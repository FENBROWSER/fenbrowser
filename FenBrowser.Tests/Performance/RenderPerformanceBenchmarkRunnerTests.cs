using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Performance;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    [Collection("Performance diagnostics")]
    public class RenderPerformanceBenchmarkRunnerTests
    {
        [Fact]
        public async Task RunDefaultSuiteAsync_CapturesPhaseMemoryGcAndEnvironmentMetrics()
        {
            var runner = new RenderPerformanceBenchmarkRunner();

            var report = await runner.RunDefaultSuiteAsync();

            Assert.NotNull(report.Environment);
            Assert.False(string.IsNullOrWhiteSpace(report.Environment.OperatingSystem));
            Assert.False(string.IsNullOrWhiteSpace(report.Environment.Framework));
            Assert.True(report.Environment.ProcessorCount > 0);
            Assert.True(report.Environment.TotalAvailableMemoryBytes > 0);
            Assert.Contains(report.Results, result => result.Name == "first-frame-heavy-layout");
            Assert.Contains(report.Results, result => result.Name == "steady-state-damage-animation");
            Assert.Contains(report.Results, result => result.Name == "dense-text-flow");
            Assert.Contains(report.Results, result => result.Name == "wrapped-multiline-text");
            Assert.All(report.Results, result =>
            {
                Assert.True(result.Iterations > 0);
                Assert.True(result.HtmlParseMs >= 0);
                Assert.True(result.HtmlParseAllocatedBytes > 0);
                Assert.True(result.CssParseAndStyleMs >= 0);
                Assert.True(result.CssParseAndStyleAllocatedBytes > 0);
                Assert.True(result.CssCoreTotalMs >= 0);
                Assert.True(result.CssCoreTotalMs <= result.CssParseAndStyleMs + 1);
                Assert.True(result.CssQueueWaitMs >= 0);
                Assert.True(result.CssDiscoveryAndFetchMs >= 0);
                Assert.True(result.CssImportExpansionMs >= 0);
                Assert.True(result.CssRuleParseMs >= 0);
                Assert.True(result.CssVariableResolutionMs >= 0);
                Assert.True(result.CssCascadeMs >= 0);
                Assert.True(result.CssSourceCount > 0);
                Assert.True(result.CssRuleCount > 0);
                Assert.True(result.ComputedStyleCount > 0);
                Assert.True(result.InlineStyleCacheHits >= 0);
                Assert.True(result.InlineStyleCacheMisses >= 0);
                Assert.True(result.InlineStyleCacheEvictions >= 0);
                Assert.InRange(result.InlineStyleCacheEntries, 0, 256);
                Assert.True(result.AverageLayoutMs >= 0);
                Assert.True(result.AveragePaintGenerationMs >= 0);
                Assert.True(result.AverageRasterMs >= 0);
                Assert.True(result.RenderAllocatedBytes > 0);
                Assert.True(result.PipelineDurationMs >= result.AverageTotalMs);
                Assert.True(result.ManagedAllocatedBytes > 0);
                Assert.True(result.ManagedHeapBytesAfter > 0);
                Assert.True(result.WorkingSetBytesAfter > 0);
                Assert.True(result.Gen0Collections >= 0);
                Assert.True(result.Gen1Collections >= 0);
                Assert.True(result.Gen2Collections >= 0);
                Assert.True(result.DomNodeCount > 0);
                Assert.True(result.BoxCount > 0);
                Assert.True(result.PaintNodeCount > 0);
            });
        }

        [Fact]
        public async Task WriteReportAsync_PersistsStructuredMeasurementArtifact()
        {
            var runner = new RenderPerformanceBenchmarkRunner();
            var report = await runner.RunDefaultSuiteAsync();
            string outputPath = Path.Combine(DiagnosticPaths.GetLogsDirectory(), "render_perf_benchmark_test.json");

            var writtenPath = await runner.WriteReportAsync(report, outputPath);
            string json = await File.ReadAllTextAsync(writtenPath);

            Assert.Equal(outputPath, writtenPath);
            Assert.Contains("\"Environment\"", json);
            Assert.Contains("\"HtmlParseMs\"", json);
            Assert.Contains("\"HtmlParseAllocatedBytes\"", json);
            Assert.Contains("\"CssParseAndStyleAllocatedBytes\"", json);
            Assert.Contains("\"CssRuleParseMs\"", json);
            Assert.Contains("\"CssCascadeMs\"", json);
            Assert.Contains("\"InlineStyleCacheHits\"", json);
            Assert.Contains("\"RenderAllocatedBytes\"", json);
            Assert.Contains("\"ManagedAllocatedBytes\"", json);
            Assert.Contains("steady-state-damage-animation", json);
        }

        [Fact]
        public async Task WriteReportAsync_DefaultPathUsesResultsPerformanceDirectory()
        {
            var runner = new RenderPerformanceBenchmarkRunner();
            var report = await runner.RunDefaultSuiteAsync();

            string writtenPath = await runner.WriteReportAsync(report);

            string expectedDirectory = Path.Combine(
                DiagnosticPaths.GetWorkspaceRoot(),
                "Results",
                "performance");
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(writtenPath));
        }
    }
}
