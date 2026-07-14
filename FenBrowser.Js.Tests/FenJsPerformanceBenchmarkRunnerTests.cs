using System.Text.Json;
using FenBrowser.Js.Performance;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class FenJsPerformanceBenchmarkRunnerTests
{
    [Fact]
    public void RunDefaultSuite_SeparatesPipelineAllocationsAndValidatesResults()
    {
        var report = new FenJsPerformanceBenchmarkRunner().RunDefaultSuite();

        Assert.Equal(5, report.Results.Count);
        Assert.False(string.IsNullOrWhiteSpace(report.Environment.BuildConfiguration));
        Assert.All(report.Results, result =>
        {
            Assert.True(result.AverageParseMs >= 0);
            Assert.True(result.AverageParseAllocatedBytes > 0);
            Assert.True(result.AverageBytecodeGenerationMs >= 0);
            Assert.True(result.AverageBytecodeAllocatedBytes > 0);
            Assert.True(result.AverageExecutionMs > 0);
            Assert.True(result.AverageExecutionAllocatedBytes > 0);
            Assert.True(result.TotalInstructionCount >= result.TopLevelInstructionCount);
            Assert.True(result.InstructionsExecuted > result.TotalInstructionCount);
            Assert.True(result.LiveHeapCells > 0);
        });
    }

    [Fact]
    public async Task WriteReportAsync_PersistsStructuredResults()
    {
        var runner = new FenJsPerformanceBenchmarkRunner();
        var report = runner.RunDefaultSuite();
        var outputPath = Path.Combine(Path.GetTempPath(), $"fenjs-perf-{Guid.NewGuid():N}.json");

        try
        {
            var writtenPath = await runner.WriteReportAsync(report, outputPath);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(writtenPath));

            Assert.Equal(outputPath, writtenPath);
            Assert.Equal(5, json.RootElement.GetProperty("Results").GetArrayLength());
            Assert.True(json.RootElement.GetProperty("Results")[0].TryGetProperty("AverageParseAllocatedBytes", out _));
            Assert.True(json.RootElement.GetProperty("Results")[0].TryGetProperty("InstructionsExecuted", out _));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }
}
