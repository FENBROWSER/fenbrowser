using System.Text.Json;
using FenBrowser.Js.Test262;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class Test262GateVerifierTests
{
    [Fact]
    public void Verify_DoesNotTreatUnexpectedPassAsUncategorizedFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                new[]
                {
                    new
                    {
                        path = "test/example.js",
                        status = "UnexpectedPass",
                        category = (string?)null
                    }
                },
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.True(result.Passed);

            using var report = JsonDocument.Parse(result.ReportJson);
            var uncategorized = report.RootElement.GetProperty("uncategorizedFailures").GetInt32();
            Assert.Equal(0, uncategorized);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenUnknownCrashExists()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures:
                [
                    new
                    {
                        relativePath = "test/crash.js",
                        classification = "crash",
                        expected = false
                    }
                ],
                crashes: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No unknown crashes violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedCrashWithoutUnknownCrashViolation()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures:
                [
                    new
                    {
                        relativePath = "test/crash.js",
                        classification = "crash",
                        expected = true
                    }
                ],
                crashes: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No unknown crashes violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureMissesOwnerOrMilestone()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/runtime.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/runtime.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "",
                        expiresAtMilestone = ""
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string BuildResultJson(object[] tests, object[] failures, int crashes = 0, int unexpectedPasses = 0)
    {
        var payload = new
        {
            summary = new
            {
                total = tests.Length,
                passed = 0,
                unsupported = 0,
                parserErrors = 0,
                crashes,
                expectedFailures = 0,
                unexpectedPasses
            },
            tests,
            failures
        };

        return JsonSerializer.Serialize(payload);
    }
}
