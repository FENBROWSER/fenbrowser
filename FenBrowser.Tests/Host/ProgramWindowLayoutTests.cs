using FenBrowser.Host;
using FenBrowser.Host.Platform;
using Xunit;

namespace FenBrowser.Tests.Host;

/// <summary>
/// Startup window geometry contract. The browser opens maximized for real
/// sessions; --windowed / --window-size pin it to a fixed size so screenshots
/// can be compared against another browser at a known viewport.
/// </summary>
public class ProgramWindowLayoutTests
{
    [Fact]
    public void ResolveWindowLayout_DefaultsToMaximized()
    {
        var (state, size) = Program.ResolveWindowLayout(new[] { "https://example.com" });

        Assert.Equal(WindowState.Maximized, state);
        Assert.Equal(1280, size.Width);
        Assert.Equal(800, size.Height);
    }

    [Fact]
    public void ResolveWindowLayout_NoArgs_DefaultsToMaximized()
    {
        var (state, size) = Program.ResolveWindowLayout(System.Array.Empty<string>());

        Assert.Equal(WindowState.Maximized, state);
        Assert.Equal(1280, size.Width);
        Assert.Equal(800, size.Height);
    }

    [Fact]
    public void ResolveWindowLayout_WindowedFlag_OpensAtFixedDefaultSize()
    {
        var (state, size) = Program.ResolveWindowLayout(new[] { "https://example.com", "--windowed" });

        Assert.Equal(WindowState.Normal, state);
        Assert.Equal(1280, size.Width);
        Assert.Equal(800, size.Height);
    }

    [Fact]
    public void ResolveWindowLayout_ExplicitSizeImpliesWindowed()
    {
        var (state, size) = Program.ResolveWindowLayout(
            new[] { "https://example.com", "--window-size", "1024x768" });

        Assert.Equal(WindowState.Normal, state);
        Assert.Equal(1024, size.Width);
        Assert.Equal(768, size.Height);
    }

    [Fact]
    public void ResolveWindowLayout_MalformedSizeKeepsMaximizedDefault()
    {
        var (state, size) = Program.ResolveWindowLayout(
            new[] { "https://example.com", "--window-size", "wide" });

        Assert.Equal(WindowState.Maximized, state);
        Assert.Equal(1280, size.Width);
        Assert.Equal(800, size.Height);
    }

    [Fact]
    public void ResolveWindowLayout_NonPositiveSizeIsRejected()
    {
        var (state, _) = Program.ResolveWindowLayout(
            new[] { "https://example.com", "--window-size", "0x800" });

        Assert.Equal(WindowState.Maximized, state);
    }

    [Fact]
    public void ResolveWindowLayout_MissingSizeValueIsIgnored()
    {
        var (state, size) = Program.ResolveWindowLayout(
            new[] { "https://example.com", "--window-size" });

        Assert.Equal(WindowState.Maximized, state);
        Assert.Equal(1280, size.Width);
    }
}
