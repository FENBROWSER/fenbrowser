using System.Text.Json;
using FenBrowser.Js.Test262;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class Test262ExpectationMatchingTests
{
    [Fact]
    public void VerifyGates_TreatsUnexpectedPassAsNonFailureForRegressionAccounting()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new { path = "test/language/foo.js", status = "ExpectedFailure", category = "parser-bug" }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/language/foo.js",
                        classification = "parser-error",
                        expected = true,
                        expectedOwner = "js",
                        expiresAtMilestone = "M1"
                    }
                ],
                passed: 0,
                expectedFailures: 1,
                unexpectedPasses: 0));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new { path = "test/language/foo.js", status = "UnexpectedPass", category = (string?)null }
                ],
                failures: Array.Empty<object>(),
                passed: 0,
                expectedFailures: 0,
                unexpectedPasses: 1));

            var verify = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(verify.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
            Assert.DoesNotContain(verify.Violations, v => v.Contains("No regression in pass count violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Dashboard_UnexpectedPass_DoesNotContributeToFailingDirectoryCounts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-expect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");
        var outPath = Path.Combine(tempRoot, "dashboard.json");

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new { path = "test/language/foo.js", status = "UnexpectedPass", category = (string?)null }
                ],
                failures: Array.Empty<object>(),
                passed: 0,
                expectedFailures: 0,
                unexpectedPasses: 1));

            Test262DashboardWriter.WriteDashboard(currentPath, previousResultPath: null, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var topFailing = doc.RootElement.GetProperty("topFailingDirectories");
            Assert.Equal(0, topFailing.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string BuildResultJson(object[] tests, object[] failures, int passed, int expectedFailures, int unexpectedPasses)
    {
        var payload = new
        {
            summary = new
            {
                total = tests.Length,
                passed,
                unsupported = 0,
                parserErrors = 0,
                crashes = 0,
                expectedFailures,
                unexpectedPasses
            },
            tests,
            failures
        };

        return JsonSerializer.Serialize(payload);
    }
}
