using System.Text.Json;
using FenBrowser.Core.Performance;
using Xunit;

namespace FenBrowser.Tests.Performance;

public sealed class DomPerformanceBenchmarkRunnerTests
{
    [Fact]
    public void RunDefaultSuite_ValidatesMutationAndEventResults()
    {
        var report = new DomPerformanceBenchmarkRunner().RunDefaultSuite();

        Assert.Equal(3, report.Results.Count);
        Assert.All(report.Results, result =>
        {
            Assert.True(result.AverageExecutionMs > 0);
            Assert.True(result.AverageAllocatedBytes > 0);
            Assert.True(result.Operations > 0);
        });
        Assert.Equal(20_000, report.Results.Single(result => result.Name == "append-remove").ObservedCallbacks);
        Assert.Equal(0, report.Results.Single(result => result.Name == "event-dispatch-no-listeners").ObservedCallbacks);
        Assert.Equal(60_000, report.Results.Single(result => result.Name == "event-dispatch-listeners").ObservedCallbacks);
    }

    [Fact]
    public async Task WriteReportAsync_PersistsStructuredResults()
    {
        var runner = new DomPerformanceBenchmarkRunner();
        var report = runner.RunDefaultSuite();
        var outputPath = Path.Combine(Path.GetTempPath(), $"dom-perf-{Guid.NewGuid():N}.json");

        try
        {
            var writtenPath = await runner.WriteReportAsync(report, outputPath);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(writtenPath));

            Assert.Equal(outputPath, writtenPath);
            Assert.Equal(3, json.RootElement.GetProperty("Results").GetArrayLength());
            Assert.True(json.RootElement.GetProperty("Results")[0].TryGetProperty("AverageAllocatedBytes", out _));
            Assert.True(json.RootElement.GetProperty("Environment").TryGetProperty("GitCommit", out _));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }
}
