using FenBrowser.Host;
using Xunit;

namespace FenBrowser.Tests.Architecture;

public class ProgramStartupModeTests
{
    [Fact]
    public void ResolveStartupMode_RendererChildArg_TakesPrecedence()
    {
        var mode = Program.ResolveStartupMode(new[] { "--renderer-child", "--wpt", "input.js" }, _ => null);

        Assert.Equal(Program.StartupMode.RendererChild, mode);
    }

    [Fact]
    public void ResolveStartupMode_RendererChildEnv_EnablesChildModeWithoutArg()
    {
        var mode = Program.ResolveStartupMode(System.Array.Empty<string>(), name =>
            name == "FEN_RENDERER_CHILD" ? "1" : null);

        Assert.Equal(Program.StartupMode.RendererChild, mode);
    }

    [Fact]
    public void ResolveStartupMode_ToolingCli_NoLongerCreatesDedicatedStartupMode()
    {
        var mode = Program.ResolveStartupMode(new[] { "--wpt", "sample.js" }, _ => null);

        Assert.Equal(Program.StartupMode.Browser, mode);
    }

    [Fact]
    public void ResolveStartupMode_WebDriverPortArg_NoLongerCreatesDedicatedStartupMode()
    {
        var mode = Program.ResolveStartupMode(new[] { "--headless", "--port=4444" }, _ => null);

        Assert.Equal(Program.StartupMode.Browser, mode);
    }

    [Fact]
    public void ResolveStartupMode_DefaultsToBrowser()
    {
        var mode = Program.ResolveStartupMode(new[] { "https://example.com" }, _ => null);

        Assert.Equal(Program.StartupMode.Browser, mode);
    }
}
