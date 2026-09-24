using System.Text.Json;
using FenBrowser.Js.Test262;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// The gates must not pass a result whose tests did not run or timed out, even
/// when it is compared with itself.
/// </summary>
public sealed class Test262GateStrictnessTests
{
    private static Test262GateVerifier.GateVerificationResult VerifyAgainstItself(object summary, object[] tests)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "fenjs-test262-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "result.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { summary, tests, failures = Array.Empty<object>() }));
            return Test262GateVerifier.Verify(path, path);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void HarnessUnsupportedTestFailsTheGates()
    {
        var result = VerifyAgainstItself(
            new { total = 1, passed = 0, harnessUnsupported = 1 },
            new object[] { new { path = "test/a.js", status = "HarnessUnsupported", category = "host-not-applicable" } });

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Contains("harnessUnsupported=1"));
    }

    [Fact]
    public void UnexpectedTimeoutFailsTheGates()
    {
        var result = VerifyAgainstItself(
            new { total = 1, passed = 0, timedOut = 1 },
            new object[] { new { path = "test/a.js", status = "TimedOut", category = "timeout" } });

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Contains("timedOut=1"));
    }

    [Fact]
    public void CleanResultPassesTheGates()
    {
        var result = VerifyAgainstItself(
            new { total = 1, passed = 1 },
            new object[] { new { path = "test/a.js", status = "Passed", category = (string?)null } });

        Assert.True(result.Passed);
    }
}
