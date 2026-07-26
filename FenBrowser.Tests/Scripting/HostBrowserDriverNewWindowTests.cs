using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Host.Tabs;
using FenBrowser.Host.WebDriver;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class HostBrowserDriverNewWindowTests
{
    [Fact]
    public void DocumentRoot_RemainsFallbackTargetWhenPaintHitTestMisses()
    {
        var document = new HtmlParser(
            "<html><body><button></button></body></html>",
            new Uri("about:blank")).Parse();

        Assert.Same(
            document.DocumentElement,
            BrowserHost.GetWebDriverClickFallbackTarget(document.DocumentElement));
        Assert.Null(
            BrowserHost.GetWebDriverClickFallbackTarget(
                document.QuerySelector("button")));
    }

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
