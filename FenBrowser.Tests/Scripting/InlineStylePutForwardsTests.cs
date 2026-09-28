using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// [PutForwards] attributes: assigning to <c>element.style</c> sets its cssText
/// (CSSOM §6.7 ElementCSSInlineStyle) and assigning to <c>element.classList</c>
/// sets its value (DOM §4.9). css/css-lists/counter-set-002 clears inline styles
/// with <c>el.style = ''</c>; the assignment used to be dropped.
/// </summary>
[Collection("Engine Tests")]
public sealed class InlineStylePutForwardsTests
{
    [Fact]
    public async Task AssigningStyleAndClassListForwardsToTheirText()
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(
                "data:text/html,<div id='t' class='x' style='color: red'></div>"));

            var result = await browser.ExecuteScriptAsync(@"
                (function () {
                    var t = document.getElementById('t'), out = [];
                    var style = t.style;
                    t.style = '';
                    out.push(JSON.stringify(t.getAttribute('style')), t.style.color, t.style === style);
                    t.style = 'color: green';
                    out.push(t.style.color, t.getAttribute('style'));
                    t.classList = 'a b';
                    out.push(t.className, t.classList.length);
                    return out.join('|');
                })();");

            Assert.Equal("\"\"||true|green|color: green|a b|2", result?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }
}
