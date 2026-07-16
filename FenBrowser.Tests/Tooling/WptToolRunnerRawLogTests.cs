using System;
using System.IO;
using FenBrowser.Tooling;
using Xunit;

namespace FenBrowser.Tests.Tooling;

public sealed class WptToolRunnerRawLogTests
{
    [Fact]
    public void AnalyzeRawLog_ExtractsUnexpectedSubtestAndTestFailures()
    {
        var rawLogPath = Path.Combine(Path.GetTempPath(), $"fen-wpt-raw-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllLines(rawLogPath, new[]
            {
                @"{""action"":""test_start"",""test"":""/console/example.html""}",
                @"{""action"":""test_status"",""test"":""/console/example.html"",""subtest"":""descriptor shape"",""status"":""FAIL"",""expected"":""PASS"",""message"":""assert_equals: expected false got true"",""stack"":""Error\n    at test""}",
                @"{""action"":""test_status"",""test"":""/console/example.html"",""subtest"":""known failure"",""status"":""FAIL"",""expected"":""FAIL"",""message"":""expected metadata failure""}",
                @"{""action"":""test_end"",""test"":""/console/example.html"",""status"":""OK""}",
                @"{""action"":""test_end"",""test"":""/console/worker.html"",""status"":""TIMEOUT"",""expected"":""OK"",""message"":""test timed out"",""extra"":{""browser_pid"":1234}}"
            });

            var analysis = WptToolRunner.AnalyzeRawLog(rawLogPath);

            Assert.Equal(1, analysis.TestStart);
            Assert.Equal(2, analysis.TestEnd);
            Assert.Equal(1, analysis.StatusCounts["OK"]);
            Assert.Equal(1, analysis.StatusCounts["TIMEOUT"]);
            Assert.Equal(1, analysis.UnexpectedSubtestFailures);
            Assert.Equal(1, analysis.UnexpectedTestFailures);
            Assert.Collection(
                analysis.Failures,
                failure =>
                {
                    Assert.Equal("subtest", failure.Level);
                    Assert.Equal("/console/example.html", failure.Test);
                    Assert.Equal("descriptor shape", failure.Subtest);
                    Assert.Equal("FAIL", failure.Status);
                    Assert.Equal("PASS", failure.Expected);
                    Assert.Contains("assert_equals", failure.Message);
                },
                failure =>
                {
                    Assert.Equal("test", failure.Level);
                    Assert.Equal("/console/worker.html", failure.Test);
                    Assert.Equal("TIMEOUT", failure.Status);
                    Assert.Equal("OK", failure.Expected);
                    Assert.Equal(1234, failure.BrowserPid);
                });
        }
        finally
        {
            File.Delete(rawLogPath);
        }
    }

    [Fact]
    public void AnalyzeRawLog_DoesNotFlagKnownIntermittentStatus()
    {
        var rawLogPath = Path.Combine(Path.GetTempPath(), $"fen-wpt-raw-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(
                rawLogPath,
                @"{""action"":""test_end"",""test"":""/flaky.html"",""status"":""TIMEOUT"",""expected"":""OK"",""known_intermittent"":[""TIMEOUT""]}");

            var analysis = WptToolRunner.AnalyzeRawLog(rawLogPath);

            Assert.Equal(1, analysis.TestEnd);
            Assert.Equal(1, analysis.StatusCounts["TIMEOUT"]);
            Assert.Equal(0, analysis.UnexpectedTestFailures);
            Assert.Empty(analysis.Failures);
        }
        finally
        {
            File.Delete(rawLogPath);
        }
    }

    [Fact]
    public void AnalyzeRawLog_AssignsEveryCompletedTestAnExplicitResultClass()
    {
        var rawLogPath = Path.Combine(Path.GetTempPath(), $"fen-wpt-raw-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllLines(rawLogPath, new[]
            {
                @"{""action"":""test_status"",""test"":""/assertion.html"",""subtest"":""fails"",""status"":""FAIL"",""expected"":""PASS""}",
                @"{""action"":""test_end"",""test"":""/assertion.html"",""status"":""OK""}",
                @"{""action"":""test_end"",""test"":""/pass.html"",""status"":""OK""}",
                @"{""action"":""test_end"",""test"":""/crash.html"",""status"":""CRASH""}",
                @"{""action"":""test_end"",""test"":""/timeout.html"",""status"":""TIMEOUT""}",
                @"{""action"":""test_end"",""test"":""/webdriver.html"",""status"":""ERROR"",""message"":""WebDriver invalid session""}",
                @"{""action"":""test_end"",""test"":""/adapter.html"",""status"":""ERROR"",""message"":""duplicate harness results""}",
                @"{""action"":""test_end"",""test"":""/unsupported.html"",""status"":""PRECONDITION_FAILED""}",
                @"{""action"":""test_end"",""test"":""/skipped.html"",""status"":""SKIP""}"
            });

            var analysis = WptToolRunner.AnalyzeRawLog(rawLogPath);

            Assert.Equal(8, analysis.TestResults.Count);
            Assert.Equal(1, analysis.ResultClassCounts[WptToolRunner.ResultClasses.Pass]);
            Assert.Equal(1, analysis.ResultClassCounts[WptToolRunner.ResultClasses.AssertionFailure]);
            Assert.Equal(1, analysis.ResultClassCounts[WptToolRunner.ResultClasses.BrowserCrash]);
            Assert.Equal(1, analysis.ResultClassCounts[WptToolRunner.ResultClasses.Timeout]);
            Assert.Equal(1, analysis.ResultClassCounts[WptToolRunner.ResultClasses.WebDriverFailure]);
            Assert.Equal(1, analysis.ResultClassCounts[WptToolRunner.ResultClasses.ProductAdapterFailure]);
            Assert.Equal(1, analysis.ResultClassCounts[WptToolRunner.ResultClasses.Unsupported]);
            Assert.Equal(1, analysis.ResultClassCounts[WptToolRunner.ResultClasses.NotRun]);
        }
        finally
        {
            File.Delete(rawLogPath);
        }
    }

    [Fact]
    public void EmptyNonzeroRun_IsClassifiedAsHarnessStartupFailure()
    {
        Assert.Equal("wpt_startup", WptToolRunner.DetermineFailurePhase(timedOut: false, exitCode: 64, testStart: 0));
        Assert.Equal(
            WptToolRunner.ResultClasses.HarnessStartupFailure,
            WptToolRunner.DetermineInfrastructureResultClass(timedOut: false, exitCode: 64, testStart: 0));
    }

    [Theory]
    [InlineData(false, "Not run")]
    [InlineData(true, "Timeout")]
    public void AnalyzeRawLog_ClassifiesStartedTestWithoutTerminalRecord(bool runTimedOut, string expectedClass)
    {
        var rawLogPath = Path.Combine(Path.GetTempPath(), $"fen-wpt-raw-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(
                rawLogPath,
                @"{""action"":""test_start"",""test"":""/incomplete.html""}");

            var analysis = WptToolRunner.AnalyzeRawLog(rawLogPath, runTimedOut);

            var result = Assert.Single(analysis.TestResults);
            Assert.Equal("/incomplete.html", result.Test);
            Assert.Equal("INCOMPLETE", result.Status);
            Assert.Equal(expectedClass, result.ResultClass);
            Assert.Equal(1, analysis.ResultClassCounts[expectedClass]);
        }
        finally
        {
            File.Delete(rawLogPath);
        }
    }
}
