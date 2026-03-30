using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Performance;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class RenderPerformanceBenchmarkRunnerTests
    {
        [Fact]
        public async Task RunDefaultSuiteAsync_ProducesNamedResultsAndThresholdEvaluation()
        {
            var runner = new RenderPerformanceBenchmarkRunner();

            var report = await runner.RunDefaultSuiteAsync();

            Assert.NotNull(report);
            Assert.NotEmpty(report.Results);
            Assert.Contains(report.Results, result => result.Name == "first-frame-heavy-layout");
            Assert.Contains(report.Results, result => result.Name == "steady-state-damage-animation");
            Assert.Contains(report.Results, result => result.Name == "dense-text-flow");
            Assert.All(report.Results, result =>
            {
                Assert.True(result.Iterations > 0);
                Assert.True(result.DomNodeCount > 0);
                Assert.True(result.BoxCount > 0);
                Assert.True(result.PaintNodeCount > 0);
            });
        }

        [Fact]
        public async Task WriteReportAsync_PersistsBenchmarkArtifact()
        {
            var runner = new RenderPerformanceBenchmarkRunner();
            var report = await runner.RunDefaultSuiteAsync();

            string outputPath = Path.Combine(DiagnosticPaths.GetLogsDirectory(), "render_perf_benchmark_test.json");
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            var writtenPath = await runner.WriteReportAsync(report, outputPath);

            Assert.Equal(outputPath, writtenPath);
            Assert.True(File.Exists(writtenPath));
            Assert.Contains("steady-state-damage-animation", await File.ReadAllTextAsync(writtenPath));
        }
    }
}
