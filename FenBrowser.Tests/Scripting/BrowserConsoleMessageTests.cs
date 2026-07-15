using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class BrowserConsoleMessageTests
{
    [Fact]
    public async Task FenJsConsoleLog_ForwardsToBrowserHostConsoleMessage()
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            var messages = new List<string>();
            browser.ConsoleMessage += messages.Add;

            Assert.True(await browser.NavigateAsync("data:text/html,<title>console</title>"));
            await browser.ExecuteScriptAsync("console.log('fen-console-forwarding');");

            Assert.Contains("fen-console-forwarding", messages);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }
}
