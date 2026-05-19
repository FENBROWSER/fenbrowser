using System.Text.Json;
using FenBrowser.Js.Test262;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class Test262RunnerTests
{
    [Fact]
    public void Run_DashboardMode_DoesNotLoadExpectations()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");
        var dashboardOut = Path.Combine(tempRoot, "dashboard.json");
        var invalidExpectationsPath = Path.Combine(tempRoot, "missing-expectations.json");

        try
        {
            File.WriteAllText(currentPath, JsonSerializer.Serialize(new
            {
                summary = new
                {
                    total = 1,
                    passed = 1,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new[]
                {
                    new { path = "test/language/foo.js", status = "Passed", category = (string?)null }
                },
                failures = Array.Empty<object>()
            }));

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: false,
                dashboard: true,
                verifyGates: false,
                outputPath: dashboardOut,
                max: 10,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: invalidExpectationsPath,
                inputPath: currentPath,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(dashboardOut));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_VerifyGatesMode_DoesNotLoadExpectations()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");
        var verifyOut = Path.Combine(tempRoot, "verify.json");
        var invalidExpectationsPath = Path.Combine(tempRoot, "missing-expectations.json");

        try
        {
            File.WriteAllText(currentPath, JsonSerializer.Serialize(new
            {
                summary = new
                {
                    total = 1,
                    passed = 1,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new[]
                {
                    new { path = "test/language/foo.js", status = "Passed", category = (string?)null }
                },
                failures = Array.Empty<object>()
            }));

            var runner = new Test262Runner();
            var exitCode = runner.Run(
                rootPath: tempRoot,
                list: false,
                dryRun: false,
                parserSubset: false,
                runtimeSubset: false,
                dashboard: false,
                verifyGates: true,
                outputPath: verifyOut,
                max: 10,
                timeoutMs: 1000,
                engine: "FenJS",
                expectationsPath: invalidExpectationsPath,
                inputPath: currentPath,
                previousPath: null,
                test262Path: null,
                test262File: null,
                featuresCsv: null,
                supportedFeaturesCsv: null);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(verifyOut));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
