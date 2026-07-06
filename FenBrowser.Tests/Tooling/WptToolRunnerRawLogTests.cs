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
}
