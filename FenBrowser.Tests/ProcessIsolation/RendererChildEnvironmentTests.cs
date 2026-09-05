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

    // Page script, layout and paint run in the renderer child, so a diagnostic
    // knob that stops at the host silently does nothing where it is meant to
    // act - which reads as "the instrumented code never ran" rather than as a
    // misconfiguration.
    [Fact]
    public void ResetToSafeBase_PreservesDiagnosticTogglesTheChildActsOn()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["FEN_FENJS_PROFILE"] = "1";
        startInfo.Environment["FEN_LOG_PRESET"] = "developer";
        startInfo.Environment["FEN_JIT_DISABLE"] = "1";
        startInfo.Environment["FEN_AUTOMATION_MODE"] = "1";

        RendererChildEnvironment.ResetToSafeBase(startInfo);

        Assert.Equal("1", startInfo.Environment["FEN_FENJS_PROFILE"]);
        Assert.Equal("developer", startInfo.Environment["FEN_LOG_PRESET"]);
        Assert.Equal("1", startInfo.Environment["FEN_JIT_DISABLE"]);
        Assert.Equal("1", startInfo.Environment["FEN_AUTOMATION_MODE"]);
    }

    // The launcher assigns each child its own pipe name and auth token. Letting
    // the parent's cross would hand one child kind another's channel.
    [Fact]
    public void ResetToSafeBase_DropsPerChildWiringAndCredentials()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment["FEN_RENDERER_AUTH_TOKEN"] = "must-not-cross-process-boundary";
        startInfo.Environment["FEN_NETWORK_PIPE_NAME"] = "must-not-cross-process-boundary";
        startInfo.Environment["FEN_TARGET_CAPABILITIES"] = "must-not-cross-process-boundary";
        startInfo.Environment["FEN_PROCESS_ISOLATION"] = "brokered";

        RendererChildEnvironment.ResetToSafeBase(startInfo);

        Assert.DoesNotContain("FEN_RENDERER_AUTH_TOKEN", startInfo.Environment.Keys);
        Assert.DoesNotContain("FEN_NETWORK_PIPE_NAME", startInfo.Environment.Keys);
        Assert.DoesNotContain("FEN_TARGET_CAPABILITIES", startInfo.Environment.Keys);
        Assert.DoesNotContain("FEN_PROCESS_ISOLATION", startInfo.Environment.Keys);
    }
}
