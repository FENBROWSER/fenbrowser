using System.Diagnostics;
using FenBrowser.Host.ProcessIsolation;
using Xunit;

namespace FenBrowser.Tests.ProcessIsolation;

public sealed class RendererChildEnvironmentTests
{
    [Fact]
    public void ResetToSafeBase_RemovesInheritedSecretsAndPreservesBootstrapRoot()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["SystemRoot"] = @"C:\Windows";
        startInfo.Environment["FEN_PARENT_SECRET"] = "must-not-cross-process-boundary";
        startInfo.Environment["UNRELATED_API_TOKEN"] = "must-not-cross-process-boundary";

        RendererChildEnvironment.ResetToSafeBase(startInfo);

        Assert.Equal(@"C:\Windows", startInfo.Environment["SystemRoot"]);
        Assert.DoesNotContain("FEN_PARENT_SECRET", startInfo.Environment.Keys);
        Assert.DoesNotContain("UNRELATED_API_TOKEN", startInfo.Environment.Keys);
    }
}
