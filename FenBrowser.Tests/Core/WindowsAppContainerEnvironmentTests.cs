using System.Collections.Generic;
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
}
