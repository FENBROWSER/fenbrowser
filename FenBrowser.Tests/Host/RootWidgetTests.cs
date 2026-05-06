using FenBrowser.Host;
using FenBrowser.Host.Widgets;
using Xunit;

namespace FenBrowser.Tests.Host;

public class RootWidgetTests
{
    [Fact]
    public void ResolveSiteInfo_UsesActualHostname_ForAbsoluteHttpsUrl()
    {
        var result = RootWidget.ResolveSiteInfo("https://example.com/path?q=1", SecurityState.Unknown);

        Assert.Equal("example.com", result.Hostname);
        Assert.True(result.Secure);
    }

    [Fact]
    public void ResolveSiteInfo_UsesParsedHostname_ForSchemeLessAddressBarText()
    {
        var result = RootWidget.ResolveSiteInfo("docs.example.org/page", SecurityState.Insecure);

        Assert.Equal("docs.example.org", result.Hostname);
        Assert.False(result.Secure);
    }

    [Fact]
    public void ResolveSiteInfo_FallsBackToUnknownSite_ForEmptyAddressBarText()
    {
        var result = RootWidget.ResolveSiteInfo("   ", SecurityState.Unknown);

        Assert.Equal("unknown site", result.Hostname);
        Assert.False(result.Secure);
    }
}
