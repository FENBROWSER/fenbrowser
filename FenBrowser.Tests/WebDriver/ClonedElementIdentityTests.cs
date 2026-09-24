using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.WebDriver;

/// <summary>
/// A web element reference identifies one node. The engine tags referenced
/// elements with a DOM attribute, which a clone copies; the clone must get its
/// own reference instead of taking over the original's.
/// </summary>
[Collection("Engine Tests")]
public sealed class ClonedElementIdentityTests
{
    [Fact]
    public async Task CloneOfAReferencedElementGetsItsOwnReference()
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync("data:text/html,<div id='host'><p class='item'>original</p></div>"));

            var originalId = await browser.FindElementAsync("css selector", "p.item");
            await browser.ExecuteScriptAsync(
                "var p = document.querySelector('p.item'); var c = p.cloneNode(true); c.textContent = 'clone'; " +
                "c.className = 'copy'; document.getElementById('host').appendChild(c);");
            var cloneId = await browser.FindElementAsync("css selector", "p.copy");

            Assert.NotEqual(originalId, cloneId);
            Assert.Equal("original", await browser.GetElementTextAsync(originalId));
            Assert.Equal("clone", await browser.GetElementTextAsync(cloneId));
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }
}
