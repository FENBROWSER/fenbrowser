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
        var appendRemove = report.Results.Single(result => result.Name == "append-remove");
        Assert.Equal(20_000, appendRemove.ObservedCallbacks);
        Assert.True(appendRemove.AverageAllocatedBytes < 4_500_000);
        var noListeners = report.Results.Single(result => result.Name == "event-dispatch-no-listeners");
        var listeners = report.Results.Single(result => result.Name == "event-dispatch-listeners");
        Assert.Equal(0, noListeners.ObservedCallbacks);
        Assert.Equal(60_000, listeners.ObservedCallbacks);
        Assert.True(noListeners.AverageAllocatedBytes < 13_000_000);
        Assert.True(listeners.AverageAllocatedBytes < 20_000_000);
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
