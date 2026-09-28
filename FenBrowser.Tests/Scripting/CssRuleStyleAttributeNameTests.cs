using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// CSSOM §6.7: a CSSStyleDeclaration's camel-cased, webkit-cased and cssFloat
/// attributes name the dashed CSS property. A style rule's declaration block took
/// `rule.style.listStyleType = v` as a property literally named "listStyleType"
/// (css/css-counter-styles/symbols-function/dynamic).
/// </summary>
[Collection("Engine Tests")]
public sealed class CssRuleStyleAttributeNameTests
{
    [Fact]
    public async Task CamelCasedAttributesNameTheDashedProperty()
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(
                "data:text/html,<style id='s'>%23t { color: red; }</style><div id='t'></div>"));

            var result = await browser.ExecuteScriptAsync(@"
                (function () {
                    var style = document.getElementById('s').sheet.cssRules[0].style;
                    style.listStyleType = 'square';
                    style.cssFloat = 'left';
                    style.webkitLineClamp = '2';
                    return [style.getPropertyValue('list-style-type'), style.listStyleType,
                            style.getPropertyValue('float'), style.getPropertyValue('-webkit-line-clamp'),
                            style.cssText.indexOf('listStyleType') < 0].join('|');
                })();");

            Assert.Equal("square|square|left|2|true", result?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }
}
