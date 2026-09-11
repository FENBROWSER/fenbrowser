using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// CSSOM §6.7.1 setProperty step 4 / the camel-case setters: assigning the empty
/// string removes the declaration instead of storing an empty value. google.com shows
/// its footer Settings menu with `popup.style.display = ''`, expecting the stylesheet
/// display to take over.
/// </summary>
[Collection("Engine Tests")]
public sealed class InlineStyleEmptyValueTests
{
    [Fact]
    public async Task SettingInlineDisplayToEmptyString_RemovesTheDeclaration()
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(
                "data:text/html,<style>%23p{display:block;width:40px;height:30px}</style><div id='p' style='display:none;z-index:200'>menu</div>"));

            var result = await browser.ExecuteScriptAsync(@"
                (function () {
                    var p = document.getElementById('p');
                    var before = getComputedStyle(p).display;
                    p.style.display = '';
                    var attr = p.getAttribute('style');
                    var after = getComputedStyle(p).display;
                    var rect = p.getBoundingClientRect();
                    p.style.setProperty('z-index', '');
                    return [before, attr, after, rect.width > 0 && rect.height > 0, p.getAttribute('style')].join('|');
                })();");

            Assert.Equal("none|z-index:200;|block|true|", result?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }
}
