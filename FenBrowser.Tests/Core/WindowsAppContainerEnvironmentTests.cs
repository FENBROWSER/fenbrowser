using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using FenBrowser.Core.Security.Sandbox.Windows;
using Xunit;

namespace FenBrowser.Tests.Core;

[SupportedOSPlatform("windows")]
public sealed class WindowsAppContainerEnvironmentTests
{
    [Fact]
    public void BuildEnvironmentBlock_PreservesAndSortsChildVariables()
    {
        var block = WindowsAppContainerSandbox.BuildEnvironmentBlock(new Dictionary<string, string>
        {
            ["FEN_RENDERER_PIPE_NAME"] = "fen_renderer_42",
            ["FEN_RENDERER_AUTH_TOKEN"] = "secret-token",
            ["FEN_RENDERER_CAPABILITIES"] = "navigate,input,frame"
        });

        Assert.Equal(
            "FEN_RENDERER_AUTH_TOKEN=secret-token\0" +
            "FEN_RENDERER_CAPABILITIES=navigate,input,frame\0" +
            "FEN_RENDERER_PIPE_NAME=fen_renderer_42\0\0",
            new string(block));
    }

    [Fact]
    public void BuildCommandLine_QuotesEachArgumentUsingWindowsRules()
    {
        var startInfo = new ProcessStartInfo { FileName = @"C:\Program Files\Fen\fen.exe" };
        startInfo.ArgumentList.Add("--renderer-child");
        startInfo.ArgumentList.Add("value with spaces");
        startInfo.ArgumentList.Add("quoted\"value");
        startInfo.ArgumentList.Add(@"trailing slash\");
        startInfo.ArgumentList.Add(string.Empty);

        Assert.Equal(
            "\"C:\\Program Files\\Fen\\fen.exe\" --renderer-child \"value with spaces\" \"quoted\\\"value\" \"trailing slash\\\\\" \"\"",
            WindowsAppContainerSandbox.BuildCommandLine(startInfo));
    }

    [Fact]
    public void BuildCommandLine_RejectsLegacyArguments()
    {
        var startInfo = new ProcessStartInfo("fen.exe", "--renderer-child");

        Assert.Throws<System.InvalidOperationException>(
            () => WindowsAppContainerSandbox.BuildCommandLine(startInfo));
    }
}
