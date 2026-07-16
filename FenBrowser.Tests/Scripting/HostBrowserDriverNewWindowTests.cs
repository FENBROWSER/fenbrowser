using FenBrowser.Host.Tabs;
using FenBrowser.Host.WebDriver;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class HostBrowserDriverNewWindowTests
{
    [Fact]
    public async Task NewWindow_HasLoadedAboutBlankDocumentBeforeReturn()
    {
        var tab = new BrowserTab();

        await HostBrowserDriver.InitializeNewTopLevelContextAsync(tab);

        Assert.Equal("about:blank", await tab.Browser.Host.GetCurrentUrlAsync());
        var root = await tab.Browser.Host.FindElementAsync("css selector", "html");
        Assert.NotNull(root);
        await tab.Browser.Host.ClickElementAsync(root);
    }
}
