using FenBrowser.Tooling;

namespace FenBrowser.Tests.Tooling;

public sealed class WebDriverLargeStackBootstrapTests
{
    [Theory]
    [InlineData("webdriver", null, true)]
    [InlineData("WebDriver", "", true)]
    [InlineData("webdriver", "1", false)]
    [InlineData("wpt", null, false)]
    public void RequiresWebDriverLargeStack_RoutesOnlyTheInitialWebDriverEntry(
        string command,
        string? marker,
        bool expected)
    {
        Assert.Equal(
            expected,
            Program.RequiresWebDriverLargeStack(new[] { command }, marker));
        Assert.Equal(16 * 1024 * 1024, Program.WebDriverMainStackBytes);
    }
}
