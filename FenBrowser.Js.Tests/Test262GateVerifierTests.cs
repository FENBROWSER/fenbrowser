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
