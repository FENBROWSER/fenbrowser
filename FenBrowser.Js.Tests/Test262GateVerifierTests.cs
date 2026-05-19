using System.Text.Json;
using System.Globalization;
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
    public void Verify_ReportMarksPassedTrueWhenNoViolations()
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
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var passed = report.RootElement.GetProperty("passed").GetBoolean();
            Assert.True(passed);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesEmptyViolationsArrayWhenPassing()
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
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var violations = report.RootElement.GetProperty("violations");
            Assert.Equal(JsonValueKind.Array, violations.ValueKind);
            Assert.Equal(0, violations.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesEmptyNewFailuresArrayWhenPassing()
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
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(0, newFailures.GetArrayLength());
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
    public void Verify_ReportMarksPassedFalseWhenViolationsExist()
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
            using var report = JsonDocument.Parse(result.ReportJson);
            var passed = report.RootElement.GetProperty("passed").GetBoolean();
            Assert.False(passed);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ResultPassedMatchesReportPassed_ForPassingCase()
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
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var reportPassed = report.RootElement.GetProperty("passed").GetBoolean();
            Assert.Equal(result.Passed, reportPassed);
            Assert.True(result.Passed);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ResultPassedMatchesReportPassed_ForFailingCase()
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
            using var report = JsonDocument.Parse(result.ReportJson);
            var reportPassed = report.RootElement.GetProperty("passed").GetBoolean();
            Assert.Equal(result.Passed, reportPassed);
            Assert.False(result.Passed);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ResultViolationsMatchReportViolationsArray()
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
            using var report = JsonDocument.Parse(result.ReportJson);
            var reportViolations = report.RootElement.GetProperty("violations")
                .EnumerateArray()
                .Select(v => v.GetString())
                .Where(v => v is not null)
                .Cast<string>()
                .ToArray();

            Assert.Equal(result.Violations.Count, reportViolations.Length);
            for (var i = 0; i < reportViolations.Length; i++)
            {
                Assert.Equal(result.Violations[i], reportViolations[i]);
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportViolationsPreserveVerifierRuleOrder()
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
            using var report = JsonDocument.Parse(result.ReportJson);
            var violations = report.RootElement.GetProperty("violations");
            Assert.True(violations.GetArrayLength() >= 2);
            Assert.Equal("No crashes violated: crashes=1.", violations[0].GetString());
            Assert.Equal("No unknown crashes violated: crashes=1.", violations[1].GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportViolationsKeepBaselineRegressionOrder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Failed",
                        category = "runtime-error"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/example.js",
                        classification = "crash",
                        expected = false
                    }
                ],
                crashes: 1,
                unexpectedPasses: 0,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var violations = report.RootElement.GetProperty("violations")
                .EnumerateArray()
                .Select(v => v.GetString())
                .Where(v => v is not null)
                .Cast<string>()
                .ToArray();

            var passRegressionIndex = Array.FindIndex(violations, v => v.StartsWith("No regression in pass count violated", StringComparison.Ordinal));
            var newCrashIndex = Array.FindIndex(violations, v => v.StartsWith("No new crash violated", StringComparison.Ordinal));
            Assert.True(passRegressionIndex >= 0);
            Assert.True(newCrashIndex >= 0);
            Assert.True(passRegressionIndex < newCrashIndex);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportNewFailuresArrayMatchesExpectedDeterministicOrder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test\\new-a.js",
                        status = "Failed",
                        category = "runtime-error"
                    },
                    new
                    {
                        path = "test/new-b.js",
                        status = "Failed",
                        category = "runtime-error"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/new-a.js",
                        classification = "runtime-error",
                        expected = false
                    },
                    new
                    {
                        relativePath = "test/new-b.js",
                        classification = "runtime-error",
                        expected = false
                    }
                ],
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(2, newFailures.GetArrayLength());
            Assert.Equal("test/new-a.js", newFailures[0].GetString());
            Assert.Equal("test/new-b.js", newFailures[1].GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesViolationMessagesWhenFailing()
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
            using var report = JsonDocument.Parse(result.ReportJson);
            var violations = report.RootElement.GetProperty("violations");
            Assert.Equal(JsonValueKind.Array, violations.ValueKind);
            Assert.True(violations.GetArrayLength() > 0);

            var containsUnknownCrashViolation = false;
            foreach (var violation in violations.EnumerateArray())
            {
                if (violation.ValueKind == JsonValueKind.String &&
                    violation.GetString() is string text &&
                    text.Contains("No unknown crashes violated", StringComparison.Ordinal))
                {
                    containsUnknownCrashViolation = true;
                    break;
                }
            }

            Assert.True(containsUnknownCrashViolation);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesSourcePaths()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var source = report.RootElement.GetProperty("source");
            Assert.Equal(currentPath, source.GetProperty("current").GetString());
            Assert.Equal(previousPath, source.GetProperty("previous").GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesGeneratedAtUtcTimestamp()
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
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var generatedAtText = report.RootElement.GetProperty("generatedAtUtc").GetString();
            Assert.False(string.IsNullOrWhiteSpace(generatedAtText));
            Assert.True(DateTime.TryParse(generatedAtText, out _));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportGeneratedAtUtcParsesAsUtcKind()
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
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var generatedAtText = report.RootElement.GetProperty("generatedAtUtc").GetString();
            Assert.False(string.IsNullOrWhiteSpace(generatedAtText));

            var parsed = DateTime.Parse(generatedAtText!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesCurrentSummaryValues()
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
                        path = "test/a.js",
                        status = "Passed",
                        category = (string?)null
                    },
                    new
                    {
                        path = "test/b.js",
                        status = "UnexpectedPass",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 1,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var currentSummary = report.RootElement.GetProperty("summary").GetProperty("current");
            Assert.Equal(2, currentSummary.GetProperty("Total").GetInt32());
            Assert.Equal(1, currentSummary.GetProperty("Passed").GetInt32());
            Assert.Equal(1, currentSummary.GetProperty("UnexpectedPasses").GetInt32());
            Assert.Equal(0, currentSummary.GetProperty("Crashes").GetInt32());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesCurrentSummaryUnsupportedAndParserErrors()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 3,
                    passed = 1,
                    unsupported = 1,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new { path = "test/pass.js", status = "Passed", category = (string?)null },
                    new { path = "test/unsupported.js", status = "Failed", category = "unsupported" },
                    new { path = "test/parser.js", status = "Failed", category = "parser-error" }
                },
                failures = new object[]
                {
                    new { relativePath = "test/unsupported.js", classification = "unsupported-feature", expected = false },
                    new { relativePath = "test/parser.js", classification = "parser-error", expected = false }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var currentSummary = report.RootElement.GetProperty("summary").GetProperty("current");
            Assert.Equal(1, currentSummary.GetProperty("Unsupported").GetInt32());
            Assert.Equal(1, currentSummary.GetProperty("ParserErrors").GetInt32());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesCurrentSummaryExpectedFailures()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 2,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 2,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new { path = "test/a.js", status = "ExpectedFailure", category = "runtime-missing" },
                    new { path = "test/b.js", status = "ExpectedFailure", category = "runtime-missing" }
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/a.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    },
                    new
                    {
                        relativePath = "test/b.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var currentSummary = report.RootElement.GetProperty("summary").GetProperty("current");
            Assert.Equal(2, currentSummary.GetProperty("ExpectedFailures").GetInt32());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesPreviousSummaryValues()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/previous.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/current.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var previousSummary = report.RootElement.GetProperty("summary").GetProperty("previous");
            Assert.Equal(1, previousSummary.GetProperty("Total").GetInt32());
            Assert.Equal(1, previousSummary.GetProperty("Passed").GetInt32());
            Assert.Equal(0, previousSummary.GetProperty("UnexpectedPasses").GetInt32());
            Assert.Equal(0, previousSummary.GetProperty("Crashes").GetInt32());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesPreviousSummaryCrashesWhenNonZero()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures:
                [
                    new
                    {
                        relativePath = "test/prev-crash.js",
                        classification = "crash",
                        expected = true
                    }
                ],
                crashes: 1,
                unexpectedPasses: 0,
                passed: 0));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/current.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var previousSummary = report.RootElement.GetProperty("summary").GetProperty("previous");
            Assert.Equal(1, previousSummary.GetProperty("Crashes").GetInt32());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesPreviousSummaryUnexpectedPassesWhenNonZero()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/prev-unexpected.js",
                        status = "UnexpectedPass",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 1,
                passed: 0));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/current.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var previousSummary = report.RootElement.GetProperty("summary").GetProperty("previous");
            Assert.Equal(1, previousSummary.GetProperty("UnexpectedPasses").GetInt32());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesPreviousSummaryExpectedFailures()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 2,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 2,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new { path = "test/a.js", status = "ExpectedFailure", category = "runtime-missing" },
                    new { path = "test/b.js", status = "ExpectedFailure", category = "runtime-missing" }
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/a.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    },
                    new
                    {
                        relativePath = "test/b.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/current.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var previousSummary = report.RootElement.GetProperty("summary").GetProperty("previous");
            Assert.Equal(2, previousSummary.GetProperty("ExpectedFailures").GetInt32());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesPreviousSummaryUnsupportedAndParserErrors()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 3,
                    passed = 1,
                    unsupported = 1,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new { path = "test/pass.js", status = "Passed", category = (string?)null },
                    new { path = "test/unsupported.js", status = "Failed", category = "unsupported" },
                    new { path = "test/parser.js", status = "Failed", category = "parser-error" }
                },
                failures = new object[]
                {
                    new { relativePath = "test/unsupported.js", classification = "unsupported-feature", expected = false },
                    new { relativePath = "test/parser.js", classification = "parser-error", expected = false }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/current.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var previousSummary = report.RootElement.GetProperty("summary").GetProperty("previous");
            Assert.Equal(1, previousSummary.GetProperty("Unsupported").GetInt32());
            Assert.Equal(1, previousSummary.GetProperty("ParserErrors").GetInt32());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNullPreviousSourceWhenBaselineIsOmitted()
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
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var source = report.RootElement.GetProperty("source");
            Assert.Equal(currentPath, source.GetProperty("current").GetString());
            Assert.Equal(JsonValueKind.Null, source.GetProperty("previous").ValueKind);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashCount()
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
                        relativePath = "test/crash-a.js",
                        classification = "crash",
                        expected = false
                    },
                    new
                    {
                        relativePath = "test/crash-b.js",
                        classification = "crash",
                        expected = false
                    }
                ],
                crashes: 2));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(2, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenPassing()
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
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenCrashesAreExpected()
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
                        relativePath = "test/crash-expected.js",
                        classification = "crash",
                        expected = true
                    }
                ],
                crashes: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesMixedUnknownCrashCount()
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
                        relativePath = "test/crash-expected.js",
                        classification = "crash",
                        expected = true
                    },
                    new
                    {
                        relativePath = "test/crash-unexpected.js",
                        classification = "crash",
                        expected = false
                    }
                ],
                crashes: 2));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashCountForUppercaseCrashClassification()
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
                        relativePath = "test/crash-uppercase.js",
                        classification = "CRASH",
                        expected = false
                    }
                ],
                crashes: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_UsesSummaryCrashCountWhenFailuresSectionIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

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
    public void Verify_ReportIncludesSummaryCrashCountWhenFailuresSectionIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresSectionIsMissingAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresAndSummaryAreMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                tests = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresSectionIsMissingAndSummaryCrashesIsNonNumeric()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = "one",
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresSectionIsMissingAndSummaryCrashesZero()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNegativeUnknownCrashesWhenFailuresSectionIsMissingAndSummaryCrashesNegative()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = -1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(-1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresSectionIsMissingAndSummaryCrashesIsNull()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = (int?)null,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotUseSummaryCrashCountWhenFailuresArrayIsPresent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.Contains(result.Violations, v => v.Contains("No crashes violated", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Violations, v => v.Contains("No unknown crashes violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayIsPresent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayIsPresentAndSummaryCrashesNegative()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = -1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayIsPresentAndSummaryCrashesIsNonNumeric()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = "one",
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayIsPresentAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashWhenFailuresArrayIsPresentAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-with-missing-summary.js",
                        classification = "crash",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayHasExpectedCrashAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-expected-with-missing-summary.js",
                        classification = "crash",
                        expected = true
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayHasUppercaseExpectedCrashAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-uppercase-expected-with-missing-summary.js",
                        classification = "crash",
                        expected = true
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayHasUppercaseCrashClassificationWithExpectedTrueAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-uppercase-classification-expected-with-missing-summary.js",
                        classification = "CRASH",
                        expected = true
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayHasMixedCaseCrashClassificationWithExpectedTrueAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-mixedcase-classification-expected-with-missing-summary.js",
                        classification = "Crash",
                        expected = true
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashWhenFailuresArrayHasNonBooleanExpectedAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-malformed-expected-with-missing-summary.js",
                        classification = "crash",
                        expected = "true"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashWhenFailuresArrayHasUppercaseCrashAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-uppercase-with-missing-summary.js",
                        classification = "CRASH",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesMixedUnknownCrashCountWhenSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 2,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-expected-missing-summary.js",
                        classification = "crash",
                        expected = true
                    },
                    new
                    {
                        relativePath = "test/crash-unexpected-missing-summary.js",
                        classification = "crash",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesMixedUnknownCrashCountWhenSummaryCrashesIsMissingAndOneExpectedFlagIsMalformed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 2,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-expected-missing-summary-a.js",
                        classification = "crash",
                        expected = true
                    },
                    new
                    {
                        relativePath = "test/crash-malformed-expected-missing-summary-b.js",
                        classification = "crash",
                        expected = "true"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesTwoUnknownCrashesWhenSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 2,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-unexpected-a-missing-summary.js",
                        classification = "crash",
                        expected = false
                    },
                    new
                    {
                        relativePath = "test/crash-unexpected-b-missing-summary.js",
                        classification = "crash",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(2, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenSummaryCrashesIsMissingAndAllCrashesAreExpected()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 2,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-expected-a-missing-summary.js",
                        classification = "crash",
                        expected = true
                    },
                    new
                    {
                        relativePath = "test/crash-expected-b-missing-summary.js",
                        classification = "crash",
                        expected = true
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashWhenFailuresArrayHasMixedCaseCrashAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-mixed-case-with-missing-summary.js",
                        classification = "Crash",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashWhenFailuresArrayUsesPathFieldAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        path = "test/crash-path-field-with-missing-summary.js",
                        classification = "crash",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashesWhenFailuresArrayUsesWhitespacePathAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        path = "   ",
                        classification = "crash",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashesWhenFailuresArrayUsesWhitespaceRelativePathAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "   ",
                        classification = "crash",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashWhenFailuresArrayHasCrashWithMissingExpectedAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-missing-expected-with-missing-summary.js",
                        classification = "crash"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashWhenFailuresArrayHasCrashWithNullExpectedAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-null-expected-with-missing-summary.js",
                        classification = "crash",
                        expected = (bool?)null
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashWhenFailuresArrayHasCrashWithStringExpectedAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-string-expected-with-missing-summary.js",
                        classification = "crash",
                        expected = "true"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashWhenFailuresArrayHasCrashWithNumericExpectedAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-numeric-expected-with-missing-summary.js",
                        classification = "crash",
                        expected = 1
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayHasWhitespacePaddedCrashAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-padded-with-missing-summary.js",
                        classification = " crash ",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayHasNonStringClassificationAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-non-string-with-missing-summary.js",
                        classification = 1,
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayHasMissingClassificationAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-missing-classification-with-missing-summary.js",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayHasWhitespacePaddedCrashWithExpectedTrueAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-padded-expected-with-missing-summary.js",
                        classification = " crash ",
                        expected = true
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayHasNonCrashClassificationAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime-error-with-missing-summary.js",
                        classification = "runtime-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayHasUppercaseNonCrashClassificationAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime-error-uppercase-with-missing-summary.js",
                        classification = "RUNTIME-ERROR",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayHasWhitespacePaddedNonCrashClassificationAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime-error-padded-with-missing-summary.js",
                        classification = " runtime-error ",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayHasNonCrashClassificationWithExpectedTrueAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime-error-expected-with-missing-summary.js",
                        classification = "runtime-error",
                        expected = true
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresArrayHasNonCrashClassificationWithMalformedExpectedAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime-error-malformed-expected-with-missing-summary.js",
                        classification = "runtime-error",
                        expected = "true"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_UsesSummaryCrashCountWhenFailuresSectionIsNotArray()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new
                {
                    malformed = true
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.Contains(result.Violations, v => v.Contains("No unknown crashes violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesSummaryCrashCountWhenFailuresSectionIsNotArray()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new
                {
                    malformed = true
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresSectionIsNotArrayAndSummaryCrashesIsNonNumeric()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = "one",
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new
                {
                    malformed = true
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresSectionIsNotArrayAndSummaryCrashesIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new
                {
                    malformed = true
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresSectionIsNotArrayAndSummaryCrashesZero()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
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
                tests = Array.Empty<object>(),
                failures = new
                {
                    malformed = true
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNegativeUnknownCrashesWhenFailuresSectionIsNotArrayAndSummaryCrashesNegative()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = -1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new
                {
                    malformed = true
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(-1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresSectionIsNotArrayAndSummaryIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                tests = Array.Empty<object>(),
                failures = new
                {
                    malformed = true
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresIsStringAndSummaryIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                tests = Array.Empty<object>(),
                failures = "malformed"
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresIsNumberAndSummaryIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                tests = Array.Empty<object>(),
                failures = 1
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresIsBooleanAndSummaryIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                tests = Array.Empty<object>(),
                failures = true
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenFailuresIsNullAndSummaryIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                tests = Array.Empty<object>(),
                failures = (object?)null
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
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
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesExpectationMetadataViolationCount()
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
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var count = report.RootElement.GetProperty("expectationMetadataViolations").GetInt32();
            Assert.Equal(1, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesAggregatedExpectationMetadataViolationCount()
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
                        path = "test/runtime-a.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    },
                    new
                    {
                        path = "test/runtime-b.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/runtime-a.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var count = report.RootElement.GetProperty("expectationMetadataViolations").GetInt32();
            Assert.Equal(2, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesExpectationMetadataViolationsForMismatchedExpectedFailureRecordPaths()
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
                        path = "test/runtime-a.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/runtime-a.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "engine-team",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    },
                    new
                    {
                        relativePath = "test/runtime-b.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "engine-team",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var count = report.RootElement.GetProperty("expectationMetadataViolations").GetInt32();
            Assert.Equal(1, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroExpectationMetadataViolationsWhenPassing()
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
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var count = report.RootElement.GetProperty("expectationMetadataViolations").GetInt32();
            Assert.Equal(0, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroExpectationMetadataViolationsForMinorMilestoneFormat()
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
                        expectedOwner = "engine-team",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2.1"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var count = report.RootElement.GetProperty("expectationMetadataViolations").GetInt32();
            Assert.Equal(0, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesExpectationMetadataViolationForInvalidPatchMilestoneFormat()
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
                        expectedOwner = "engine-team",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2.1.3"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var count = report.RootElement.GetProperty("expectationMetadataViolations").GetInt32();
            Assert.Equal(1, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureOwnerIsWhitespaceOnly()
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
                        expectedOwner = "   ",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureOwnerIsNonString()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 1,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/runtime.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = 1,
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureOwnerIsUnknown()
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
                        expectedOwner = "unknown",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureOwnerIsUppercaseUnknown()
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
                        expectedOwner = "UNKNOWN",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureMilestoneIsUnknown()
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
                        expectedOwner = "js",
                        expiresAtMilestone = "unknown"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureMilestoneIsWhitespaceOnly()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "   "
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureMilestoneIsNonString()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 1,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/runtime.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = 2
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureMilestoneIsUppercaseUnknown()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "UNKNOWN"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureMilestoneFormatIsInvalid()
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
                        expectedOwner = "js",
                        expiresAtMilestone = "next-release"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureMilestoneUsesLowercasePrefix()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "m2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedFailureMilestoneWithUppercasePrefix()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedFailureMilestoneWithZeroSuffix()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M0"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedFailureMilestoneWithLeadingZeroSuffix()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M00"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedFailureMilestoneWithDecimalFormat()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2.1"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedFailureMilestoneWithDecimalLeadingZeroFraction()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2.01"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedFailureMilestoneWithZeroMajorAndFraction()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M0.0"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedFailureTimeoutRecordWhenMetadataIsValid()
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
                        category = "timeout"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/runtime.js",
                        classification = "timeout",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known timeout debt",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureMilestoneHasNoNumericSuffix()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureMilestoneHasMultipleDots()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2.1.3"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureAreaIsMissing()
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
                        expectedOwner = "js",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureAreaIsWhitespaceOnly()
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
                        expectedOwner = "js",
                        expectedArea = "   ",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureAreaIsNonString()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 1,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/runtime.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = 1,
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureAreaIsUnknown()
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
                        expectedOwner = "js",
                        expectedArea = "unknown",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureAreaIsUppercaseUnknown()
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
                        expectedOwner = "js",
                        expectedArea = "UNKNOWN",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedFailureWithValidMetadata()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known runtime limitation",
                        expiresAtMilestone = "M2.1"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_MetadataViolationMessageMentionsOwnerAreaMilestone()
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
                        expectedOwner = "unknown",
                        expectedArea = "unknown",
                        expectedReason = "unknown",
                        expiresAtMilestone = "unknown"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.Contains(result.Violations, v => string.Equals(v, "No expected failure without owner/area/reason/milestone violated: entries=1.", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_MetadataViolationMessageMentionsOwnerAreaReasonMilestone()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.Contains(result.Violations, v => string.Equals(v, "No expected failure without owner/area/reason/milestone violated: entries=1.", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureStatusLacksExpectedFailureRecord()
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
                        expected = false
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedFailureStatusWhenExpectedFailureRecordExists()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroExpectationMetadataViolationsForUppercaseExpectedFailureStatus()
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
                        status = "EXPECTEDFAILURE",
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var count = report.RootElement.GetProperty("expectationMetadataViolations").GetInt32();
            Assert.Equal(0, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroExpectationMetadataViolationsForLowercaseExpectedFailureStatus()
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
                        status = "expectedfailure",
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var count = report.RootElement.GetProperty("expectationMetadataViolations").GetInt32();
            Assert.Equal(0, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesExpectationMetadataViolationForWhitespacePaddedExpectedFailureStatus()
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
                        status = " expectedfailure ",
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var count = report.RootElement.GetProperty("expectationMetadataViolations").GetInt32();
            Assert.Equal(1, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureRecordLacksExpectedFailureStatus()
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
                        status = "Failed",
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedFailureRecordWhenExpectedFailureStatusExists()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureRecordPathIsMissingFromTests()
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
                        path = "test/other.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/runtime.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureStatusPathIsMissingFromFailures()
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
                        path = "test/runtime-a.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    },
                    new
                    {
                        path = "test/runtime-b.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/runtime-a.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenTestsSectionIsMissingButExpectedFailureRecordsExist()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 1,
                    unexpectedPasses = 0
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_CountsAllExpectedFailureRecordsWhenTestsSectionIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 2,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 2,
                    unexpectedPasses = 0
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime-a.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    },
                    new
                    {
                        relativePath = "test/runtime-b.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => string.Equals(v, "No expected failure without owner/area/reason/milestone violated: entries=2.", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesExpectationMetadataViolationsWhenTestsSectionIsNotArrayAndExpectedFailureRecordsExist()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 2,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 2,
                    crashes = 0,
                    expectedFailures = 2,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime-a.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    },
                    new
                    {
                        relativePath = "test/runtime-b.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var count = report.RootElement.GetProperty("expectationMetadataViolations").GetInt32();
            Assert.Equal(2, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesMetadataDefectCountWhenTestsSectionIsNotArrayAndExpectedFailureRecordsExist()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 1,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime-a.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var count = report.RootElement.GetProperty("expectationMetadataViolations").GetInt32();
            Assert.Equal(2, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotFailMetadataRuleWhenTestsSectionMissingAndNoExpectedFailures()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime.js",
                        classification = "runtime-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotFlagUncategorizedFailuresWhenTestsSectionIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("All enabled parser tests must be categorized violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroExpectationMetadataViolationsWhenTestsSectionIsNotArrayAndNoExpectedFailures()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/failure-without-expected.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var count = report.RootElement.GetProperty("expectationMetadataViolations").GetInt32();
            Assert.Equal(0, count);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotRequireMetadataForNonExpectedFailureRecords()
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
                        status = "Failed",
                        category = "runtime-error"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/runtime.js",
                        classification = "runtime-error",
                        expected = false
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotRequireMetadataWhenExpectedFlagIsNonBoolean()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/runtime.js",
                        status = "Failed",
                        category = "runtime-error"
                    }
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime.js",
                        classification = "runtime-error",
                        expected = "true"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenFailuresSectionMissingButExpectedFailureStatusesExist()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 1,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/runtime.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_CountsAllExpectedFailureStatusesWhenFailuresSectionIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 2,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 2,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/runtime-a.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    },
                    new
                    {
                        path = "test/runtime-b.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => string.Equals(v, "No expected failure without owner/area/reason/milestone violated: entries=2.", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedFailureLinkageAcrossSlashStyles()
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
                        path = "test\\runtime\\case.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/runtime/case.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedFailureLinkageAcrossSlashStyles_Reversed()
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
                        path = "test/runtime/case.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test\\runtime\\case.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_AllowsExpectedFailureLinkageWhenFailureUsesPathField()
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
                        path = "test/runtime/path-field.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                ],
                failures:
                [
                    new
                    {
                        path = "test/runtime/path-field.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenFailureRelativePathWhitespaceDoesNotFallbackToPath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 1,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/runtime/path-field.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "   ",
                        path = "test/runtime/path-field.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenFailurePathFieldDoesNotMatchExpectedFailureStatusPath()
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
                        path = "test/runtime/path-a.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                ],
                failures:
                [
                    new
                    {
                        path = "test/runtime/path-b.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known limitation",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureReasonIsMissing()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureReasonIsWhitespaceOnly()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "   ",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureReasonIsNonString()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 1,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/runtime.js",
                        status = "ExpectedFailure",
                        category = "runtime-missing"
                    }
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/runtime.js",
                        classification = "runtime-error",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = 1,
                        expiresAtMilestone = "M2"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureReasonIsPlaceholder()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "tbd",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureReasonIsUppercasePlaceholder()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "TBD",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenExpectedFailureReasonIsUnknown()
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
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "unknown",
                        expiresAtMilestone = "M2"
                    }
                ]));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No expected failure without owner/area/reason/milestone violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotFlagPassRegressionWhenPassBecomesUnexpectedPass()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "UnexpectedPass",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 1,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No regression in pass count violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FlagsPassRegressionWhenEffectivePassesDrop()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Failed",
                        category = "parser-bug"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/example.js",
                        classification = "parser-error",
                        expected = false
                    }
                ],
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.Contains(result.Violations, v => v.Contains("No regression in pass count violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenNewFailureAppearsInEnabledSubset()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/new-failure.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/new-failure.js",
                        status = "Failed",
                        category = "runtime-error"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/new-failure.js",
                        classification = "runtime-error",
                        expected = false
                    }
                ],
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNewFailurePath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test\\new-failure.js",
                        status = "Failed",
                        category = "runtime-error"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/new-failure.js",
                        classification = "runtime-error",
                        expected = false
                    }
                ],
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(1, newFailures.GetArrayLength());
            Assert.Equal("test/new-failure.js", newFailures[0].GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNonEmptyNewFailuresArrayWhenFailing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/report-new-failure.js",
                        status = "Failed",
                        category = "runtime-error"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/report-new-failure.js",
                        classification = "runtime-error",
                        expected = false
                    }
                ],
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.True(newFailures.GetArrayLength() > 0);
            Assert.Equal("test/report-new-failure.js", newFailures[0].GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportTreatsCurrentFailuresAsNewWhenBaselineIsOmitted()
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
                        path = "test/no-baseline-failure.js",
                        status = "Failed",
                        category = "runtime-error"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/no-baseline-failure.js",
                        classification = "runtime-error",
                        expected = false
                    }
                ],
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.True(newFailures.GetArrayLength() > 0);
            Assert.Equal("test/no-baseline-failure.js", newFailures[0].GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportTreatsCurrentFailuresAsNewWhenBaselinePathIsWhitespace()
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
                        path = "test/whitespace-baseline-failure.js",
                        status = "Failed",
                        category = "runtime-error"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/whitespace-baseline-failure.js",
                        classification = "runtime-error",
                        expected = false
                    }
                ],
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, "   ");
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.True(newFailures.GetArrayLength() > 0);
            Assert.Equal("test/whitespace-baseline-failure.js", newFailures[0].GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportTreatsCurrentFailuresAsNewWhenBaselineFileIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");
        var missingPreviousPath = Path.Combine(tempRoot, "does-not-exist.json");

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/missing-baseline-failure.js",
                        status = "Failed",
                        category = "runtime-error"
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/missing-baseline-failure.js",
                        classification = "runtime-error",
                        expected = false
                    }
                ],
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, missingPreviousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.True(newFailures.GetArrayLength() > 0);
            Assert.Equal("test/missing-baseline-failure.js", newFailures[0].GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportCapsNewFailuresArrayAtTwoHundredEntries()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var tests = new object[205];
            var failures = new object[205];
            for (var i = 0; i < 205; i++)
            {
                var path = $"test/new-{i}.js";
                tests[i] = new
                {
                    path,
                    status = "Failed",
                    category = "runtime-error"
                };
                failures[i] = new
                {
                    relativePath = path,
                    classification = "runtime-error",
                    expected = false
                };
            }

            File.WriteAllText(currentPath, BuildResultJson(
                tests: tests,
                failures: failures,
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(200, newFailures.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenCrashCountIncreasesComparedToPrevious()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 0));

            File.WriteAllText(currentPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures:
                [
                    new
                    {
                        relativePath = "test/crash-new.js",
                        classification = "crash",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known crash debt",
                        expiresAtMilestone = "M3"
                    }
                ],
                crashes: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No new crash violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenAnyCrashExistsEvenIfExpected()
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
                        relativePath = "test/crash-expected.js",
                        classification = "crash",
                        expected = true,
                        expectedOwner = "js",
                        expectedArea = "runtime",
                        expectedReason = "known crash debt",
                        expiresAtMilestone = "M4"
                    }
                ],
                crashes: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No crashes violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenNonPassingTestHasNoCategory()
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
                        path = "test/uncategorized.js",
                        status = "Failed",
                        category = (string?)null
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/uncategorized.js",
                        classification = "parser-error",
                        expected = false
                    }
                ],
                crashes: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("All enabled parser tests must be categorized violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUncategorizedFailureCount()
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
                        path = "test/uncategorized.js",
                        status = "Failed",
                        category = (string?)null
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/uncategorized.js",
                        classification = "parser-error",
                        expected = false
                    }
                ],
                crashes: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var uncategorized = report.RootElement.GetProperty("uncategorizedFailures").GetInt32();
            Assert.Equal(1, uncategorized);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUncategorizedFailureCountForWhitespaceCategory()
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
                        path = "test/uncategorized-whitespace.js",
                        status = "Failed",
                        category = "   "
                    }
                ],
                failures:
                [
                    new
                    {
                        relativePath = "test/uncategorized-whitespace.js",
                        classification = "parser-error",
                        expected = false
                    }
                ],
                crashes: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var uncategorized = report.RootElement.GetProperty("uncategorizedFailures").GetInt32();
            Assert.Equal(1, uncategorized);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUncategorizedFailuresWhenPassing()
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
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
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
    public void Verify_FailsWhenTestStatusIsMissingAndCategoryIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/missing-status.js",
                        category = (string?)null
                    }
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/missing-status.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("All enabled parser tests must be categorized violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenTestStatusIsNonStringAndCategoryIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/non-string-status.js",
                        status = 1,
                        category = (string?)null
                    }
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/non-string-status.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("All enabled parser tests must be categorized violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailsWhenTestCategoryIsNonStringForNonPassingStatus()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/non-string-category.js",
                        status = "Failed",
                        category = 1
                    }
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/non-string-category.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("All enabled parser tests must be categorized violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotTreatNonStringCategoryAsUncategorizedWhenStatusIsPassed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
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
                tests = new object[]
                {
                    new
                    {
                        path = "test/passed-non-string-category.js",
                        status = "Passed",
                        category = 1
                    }
                },
                failures = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("All enabled parser tests must be categorized violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotTreatNonStringCategoryAsUncategorizedWhenStatusIsUnexpectedPass()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 1
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/unexpectedpass-non-string-category.js",
                        status = "UnexpectedPass",
                        category = 1
                    }
                },
                failures = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("All enabled parser tests must be categorized violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotTreatNonStringCategoryAsUncategorizedWhenStatusIsLowercaseUnexpectedPass()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 1
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/unexpectedpass-lowercase-non-string-category.js",
                        status = "unexpectedpass",
                        category = 1
                    }
                },
                failures = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("All enabled parser tests must be categorized violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotTreatPassedTestWithoutCategoryAsUncategorizedFailure()
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
                        path = "test/passed.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("All enabled parser tests must be categorized violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ToleratesNonNumericSummaryFieldsByDefaultingToZero()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = "one",
                    passed = "zero",
                    unsupported = "zero",
                    parserErrors = "zero",
                    crashes = "zero",
                    expectedFailures = "zero",
                    unexpectedPasses = "zero"
                },
                tests = Array.Empty<object>(),
                failures = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.True(result.Passed);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No crashes violated", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Violations, v => v.Contains("No unknown crashes violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ToleratesNonNumericPreviousSummaryFieldsByDefaultingToZero()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = "one",
                    passed = "one",
                    unsupported = "zero",
                    parserErrors = "zero",
                    crashes = "zero",
                    expectedFailures = "zero",
                    unexpectedPasses = "zero"
                },
                tests = Array.Empty<object>(),
                failures = Array.Empty<object>()
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No regression in pass count violated", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new crash violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ToleratesMissingPreviousSummaryByDefaultingCountsToZero()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                tests = Array.Empty<object>(),
                failures = Array.Empty<object>()
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No regression in pass count violated", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new crash violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ToleratesMissingSummaryByDefaultingCountsToZero()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                tests = Array.Empty<object>(),
                failures = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.True(result.Passed);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No crashes violated", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Violations, v => v.Contains("No unknown crashes violated", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotTreatLowercasePassedStatusAsUncategorizedFailure()
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
                        path = "test/passed-lowercase.js",
                        status = "passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("All enabled parser tests must be categorized violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotTreatLowercaseUnexpectedPassStatusAsUncategorizedFailure()
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
                        path = "test/unexpected-pass-lowercase.js",
                        status = "unexpectedpass",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 1,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("All enabled parser tests must be categorized violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_UsesFailuresFallbackForNewFailureDetectionWhenTestsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/existing.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 2,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 2,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/existing.js",
                        classification = "parser-error",
                        expected = false
                    },
                    new
                    {
                        path = "test/new.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.False(result.Passed);
            Assert.Contains(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNullPreviousSummaryWhenBaselineIsOmitted()
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
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var summary = report.RootElement.GetProperty("summary");
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("previous").ValueKind);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNullPreviousSummaryWhenBaselinePathIsWhitespace()
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
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, "   ");
            using var report = JsonDocument.Parse(result.ReportJson);
            var summary = report.RootElement.GetProperty("summary");
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("previous").ValueKind);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportPreservesWhitespacePreviousSourcePath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");
        var previousPath = "   ";

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var source = report.RootElement.GetProperty("source");
            Assert.Equal(currentPath, source.GetProperty("current").GetString());
            Assert.Equal(previousPath, source.GetProperty("previous").GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportPreservesEmptyPreviousSourcePathAndNullPreviousSummary()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");
        const string previousPath = "";

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var source = report.RootElement.GetProperty("source");
            var summary = report.RootElement.GetProperty("summary");
            Assert.Equal(currentPath, source.GetProperty("current").GetString());
            Assert.Equal(previousPath, source.GetProperty("previous").GetString());
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("previous").ValueKind);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotTreatUnexpectedPassAsNewFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "UnexpectedPass",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 1,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotTreatLowercaseUnexpectedPassAsNewFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "unexpectedpass",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 1,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNoNewFailuresForLowercaseUnexpectedPass()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "unexpectedpass",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 1,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(0, newFailures.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNoNewFailuresForUppercaseUnexpectedPass()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "UNEXPECTEDPASS",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 1,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(0, newFailures.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotTreatLowercasePassedAsNewFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNoNewFailuresForLowercasePassed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(0, newFailures.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNoNewFailuresForUppercasePassed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "PASSED",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(0, newFailures.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNewFailureForWhitespacePaddedPassedStatus()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/padded-passed.js",
                        status = " passed ",
                        category = "parser-error"
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(1, newFailures.GetArrayLength());
            Assert.Equal("test/padded-passed.js", newFailures[0].GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNewFailureForWhitespacePaddedUnexpectedPassStatus()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/padded-unexpectedpass.js",
                        status = " unexpectedpass ",
                        category = "parser-error"
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(1, newFailures.GetArrayLength());
            Assert.Equal("test/padded-unexpectedpass.js", newFailures[0].GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_IgnoresBlankTestPathInFailureSetRegressionDetection()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "",
                        status = "Failed",
                        category = "parser-error"
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_IgnoresWhitespaceOnlyTestPathInFailureSetRegressionDetection()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "   ",
                        status = "Failed",
                        category = "parser-error"
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUncategorizedFailuresWhenTestsSectionIsNotArray()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/fallback-from-malformed-tests.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
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
    public void Verify_PrefersTestsFailureSetOverFailuresFallbackWhenTestsSectionExists()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var currentPayload = new
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
                tests = new object[]
                {
                    new
                    {
                        path = "test/passed.js",
                        status = "Passed",
                        category = (string?)null
                    }
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/should-be-ignored.js",
                        classification = "runtime-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_UsesFailuresFallbackWhenTestsSectionIsNotArray()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/fallback-from-malformed-tests.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.Contains(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNewFailureWhenTestsSectionIsNotArray()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/fallback-from-malformed-tests.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(1, newFailures.GetArrayLength());
            Assert.Equal("test/fallback-from-malformed-tests.js", newFailures[0].GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNewFailureWhenTestsSectionIsMalformedAndFailureUsesPathField()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new object[]
                {
                    new
                    {
                        path = "test/fallback-path-field.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(1, newFailures.GetArrayLength());
            Assert.Equal("test/fallback-path-field.js", newFailures[0].GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNoNewFailuresWhenTestsSectionIsMalformedAndFailurePathIsWhitespace()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new object[]
                {
                    new
                    {
                        path = "   ",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(0, newFailures.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNoNewFailuresWhenTestsSectionIsMalformedAndFailurePathIsNonString()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new object[]
                {
                    new
                    {
                        path = 123,
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(0, newFailures.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNoNewFailuresWhenTestsSectionIsMalformedAndFailureRelativePathIsNonString()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = 123,
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(0, newFailures.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNewFailureWhenTestsSectionIsMalformedAndFailureUsesPathAfterNonStringRelativePath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = 123,
                        path = "test/fallback-path-after-bad-relative.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(1, newFailures.GetArrayLength());
            Assert.Equal("test/fallback-path-after-bad-relative.js", newFailures[0].GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNoNewFailuresWhenTestsSectionIsMalformedAndFailureHasWhitespaceRelativePathEvenWithPathField()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "   ",
                        path = "test/fallback-path-after-whitespace-relative.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(0, newFailures.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNoNewFailuresWhenTestsSectionIsMalformedAndFailureRelativePathIsWhitespace()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "   ",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(0, newFailures.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotFlagNewFailureWhenTestsAndFailuresSectionsAreMalformed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new
                {
                    malformed = true
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNoNewFailuresWhenTestsSectionIsMalformedAndFailuresAreEmpty()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = Array.Empty<object>()
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(0, newFailures.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNoNewFailuresWhenTestsAndFailuresSectionsAreMalformed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new
                {
                    malformed = true
                },
                failures = new
                {
                    malformed = true
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var newFailures = report.RootElement.GetProperty("newFailures");
            Assert.Equal(JsonValueKind.Array, newFailures.ValueKind);
            Assert.Equal(0, newFailures.GetArrayLength());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_IgnoresNonStringTestPathInFailureSetRegressionDetection()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new
                    {
                        path = 123,
                        status = "Failed",
                        category = "parser-error"
                    }
                },
                failures = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_CountsNonStringStatusWithValidPathAsFailureSetEntry()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            File.WriteAllText(previousPath, BuildResultJson(
                tests: Array.Empty<object>(),
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 0));

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = new object[]
                {
                    new
                    {
                        path = "test/non-string-status-valid-path.js",
                        status = 1,
                        category = "parser-error"
                    }
                },
                failures = Array.Empty<object>()
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.Contains(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_CountsUnknownCrashWhenFailureUsesPathField()
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
                        path = "test/crash-path-field.js",
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
    public void Verify_TreatsNonBooleanExpectedCrashAsUnknown()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-malformed-expected.js",
                        classification = "crash",
                        expected = "true"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

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
    public void Verify_ReportIncludesUnknownCrashCountForNonBooleanExpectedCrash()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/crash-malformed-expected.js",
                        classification = "crash",
                        expected = "true"
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_TreatsUppercaseCrashClassificationAsCrash()
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
                        relativePath = "test/crash-uppercase.js",
                        classification = "CRASH",
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
    public void Verify_DoesNotCountFailureWithoutCrashClassificationAsUnknownCrash()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/no-classification.js",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No unknown crashes violated", StringComparison.Ordinal));
            Assert.Contains(result.Violations, v => v.Contains("No crashes violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesWhenCrashClassificationIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/no-classification.js",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotCountFailureWithNonStringClassificationAsUnknownCrash()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/non-string-classification.js",
                        classification = 1,
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No unknown crashes violated", StringComparison.Ordinal));
            Assert.Contains(result.Violations, v => v.Contains("No crashes violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesForNonStringClassification()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/non-string-classification.js",
                        classification = 1,
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotCountNonCrashClassificationAsUnknownCrash()
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
                        relativePath = "test/not-a-crash.js",
                        classification = "runtime-error",
                        expected = false
                    }
                ],
                crashes: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No unknown crashes violated", StringComparison.Ordinal));
            Assert.Contains(result.Violations, v => v.Contains("No crashes violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesForNonCrashClassification()
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
                        relativePath = "test/not-a-crash.js",
                        classification = "runtime-error",
                        expected = false
                    }
                ],
                crashes: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotCountWhitespacePaddedCrashClassificationAsUnknownCrash()
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
                        relativePath = "test/whitespace-crash-classification.js",
                        classification = " crash ",
                        expected = false
                    }
                ],
                crashes: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No unknown crashes violated", StringComparison.Ordinal));
            Assert.Contains(result.Violations, v => v.Contains("No crashes violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesZeroUnknownCrashesForWhitespacePaddedCrashClassification()
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
                        relativePath = "test/whitespace-crash-classification.js",
                        classification = " crash ",
                        expected = false
                    }
                ],
                crashes: 1));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(0, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_CountsUnknownCrashWhenRelativePathIsWhitespaceButClassificationIsCrash()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "   ",
                        path = "test/crash-path-field.js",
                        classification = "crash",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.Contains(result.Violations, v => v.Contains("No unknown crashes violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesUnknownCrashWhenRelativePathIsWhitespaceButClassificationIsCrash()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "   ",
                        path = "test/crash-path-field.js",
                        classification = "crash",
                        expected = false
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            using var report = JsonDocument.Parse(result.ReportJson);
            var unknownCrashes = report.RootElement.GetProperty("unknownCrashes").GetInt32();
            Assert.Equal(1, unknownCrashes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_DoesNotCountExpectedCrashWhenRelativePathIsWhitespace()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var payload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 1,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                tests = Array.Empty<object>(),
                failures = new object[]
                {
                    new
                    {
                        relativePath = "   ",
                        path = "test/crash-path-field.js",
                        classification = "crash",
                        expected = true
                    }
                }
            };

            File.WriteAllText(currentPath, JsonSerializer.Serialize(payload));

            var result = Test262GateVerifier.Verify(currentPath, previousResultPath: null);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No unknown crashes violated", StringComparison.Ordinal));
            Assert.Contains(result.Violations, v => v.Contains("No crashes violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_IgnoresMissingPreviousResultPath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");
        var missingPreviousPath = Path.Combine(tempRoot, "does-not-exist.json");

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, missingPreviousPath);
            Assert.True(result.Passed);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No regression in pass count violated", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new crash violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportIncludesNullPreviousSummaryWhenBaselineFileIsMissing()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");
        var missingPreviousPath = Path.Combine(tempRoot, "does-not-exist.json");

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, missingPreviousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var summary = report.RootElement.GetProperty("summary");
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("previous").ValueKind);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_ReportPreservesMissingBaselinePathInSourcePrevious()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var currentPath = Path.Combine(tempRoot, "current.json");
        var missingPreviousPath = Path.Combine(tempRoot, "does-not-exist.json");

        try
        {
            File.WriteAllText(currentPath, BuildResultJson(
                tests:
                [
                    new
                    {
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, missingPreviousPath);
            using var report = JsonDocument.Parse(result.ReportJson);
            var source = report.RootElement.GetProperty("source");
            Assert.Equal(currentPath, source.GetProperty("current").GetString());
            Assert.Equal(missingPreviousPath, source.GetProperty("previous").GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_IgnoresWhitespacePreviousResultPath()
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
                        path = "test/example.js",
                        status = "Passed",
                        category = (string?)null
                    }
                ],
                failures: Array.Empty<object>(),
                crashes: 0,
                unexpectedPasses: 0,
                passed: 1));

            var result = Test262GateVerifier.Verify(currentPath, "   ");
            Assert.True(result.Passed);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No regression in pass count violated", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new crash violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailureFallbackNormalizesSlashStylesAcrossBaselines()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test\\same\\case.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "test/same/case.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailureFallbackUsesPathFieldAcrossBaselines()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = new object[]
                {
                    new
                    {
                        path = "test/path-only.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = new object[]
                {
                    new
                    {
                        path = "test/path-only.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailureFallbackDoesNotUsePathWhenRelativePathIsWhitespace()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = new object[]
                {
                    new
                    {
                        relativePath = "   ",
                        path = "test/ignored-due-to-relativepath-whitespace.js",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailureFallbackIgnoresBlankPaths()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = new object[]
                {
                    new
                    {
                        path = "",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Verify_FailureFallbackIgnoresWhitespaceOnlyPaths()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var previousPath = Path.Combine(tempRoot, "previous.json");
        var currentPath = Path.Combine(tempRoot, "current.json");

        try
        {
            var previousPayload = new
            {
                summary = new
                {
                    total = 0,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 0,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = Array.Empty<object>()
            };

            var currentPayload = new
            {
                summary = new
                {
                    total = 1,
                    passed = 0,
                    unsupported = 0,
                    parserErrors = 1,
                    crashes = 0,
                    expectedFailures = 0,
                    unexpectedPasses = 0
                },
                failures = new object[]
                {
                    new
                    {
                        path = "   ",
                        classification = "parser-error",
                        expected = false
                    }
                }
            };

            File.WriteAllText(previousPath, JsonSerializer.Serialize(previousPayload));
            File.WriteAllText(currentPath, JsonSerializer.Serialize(currentPayload));

            var result = Test262GateVerifier.Verify(currentPath, previousPath);
            Assert.DoesNotContain(result.Violations, v => v.Contains("No new failure in enabled subset violated", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string BuildResultJson(object[] tests, object[] failures, int crashes = 0, int unexpectedPasses = 0, int? passed = null)
    {
        var payload = new
        {
            summary = new
            {
                total = tests.Length,
                passed = passed ?? 0,
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
