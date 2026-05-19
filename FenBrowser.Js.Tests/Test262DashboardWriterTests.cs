using System.Text.Json;
using FenBrowser.Js.Test262;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class Test262DashboardWriterTests
{
    [Fact]
    public void WriteDashboard_DoesNotTreatUnexpectedPassAsFailureOrRegression()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var outputPath = Path.Combine(tempRoot, "dashboard.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new { path = "test/a.js", status = "Passed" }
                ]));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new { path = "test/a.js", status = "UnexpectedPass" }
                ]));

            Test262DashboardWriter.WriteDashboard(currentPath, previousPath, outputPath);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var root = doc.RootElement;

            var newRegressions = root.GetProperty("newRegressions");
            Assert.Equal(0, newRegressions.GetArrayLength());

            var topFailingDirectories = root.GetProperty("topFailingDirectories");
            Assert.Equal(0, topFailingDirectories.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void WriteDashboard_TreatsExpectedFailureAsFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");
        var outputPath = Path.Combine(tempRoot, "dashboard.json");

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new { path = "test/language/foo/bar.js", status = "ExpectedFailure" }
                ]));

            Test262DashboardWriter.WriteDashboard(currentPath, previousResultPath: null, outputPath);
            using var doc = JsonDocument.Parse(File.ReadAllText(outputPath));
            var topFailingDirectories = doc.RootElement.GetProperty("topFailingDirectories");
            Assert.Equal(1, topFailingDirectories.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string BuildResultJson(object[] tests)
    {
        var payload = new
        {
            summary = new
            {
                total = tests.Length,
                passed = 0,
                unsupported = 0,
                parserErrors = 0,
                crashes = 0,
                expectedFailures = 0,
                unexpectedPasses = 0
            },
            tests,
            failures = Array.Empty<object>()
        };

        return JsonSerializer.Serialize(payload);
    }
}
