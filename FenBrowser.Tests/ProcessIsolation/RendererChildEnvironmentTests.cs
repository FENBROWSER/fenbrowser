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
        startInfo.Environment["DISPLAY"] = ":0";
        startInfo.Environment["WAYLAND_DISPLAY"] = "wayland-0";
        startInfo.Environment["XDG_RUNTIME_DIR"] = "/run/user/1000";
        startInfo.Environment["FEN_PARENT_SECRET"] = "must-not-cross-process-boundary";
        startInfo.Environment["UNRELATED_API_TOKEN"] = "must-not-cross-process-boundary";

        RendererChildEnvironment.ResetToSafeBase(startInfo);

        Assert.Equal(@"C:\Windows", startInfo.Environment["SystemRoot"]);
        Assert.DoesNotContain("FEN_PARENT_SECRET", startInfo.Environment.Keys);
        Assert.DoesNotContain("UNRELATED_API_TOKEN", startInfo.Environment.Keys);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(":0", startInfo.Environment["DISPLAY"]);
            Assert.Equal("wayland-0", startInfo.Environment["WAYLAND_DISPLAY"]);
            Assert.Equal("/run/user/1000", startInfo.Environment["XDG_RUNTIME_DIR"]);
        }
    }
}
