using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// The CSSOM surface Closure's popup positioning walks: CSSOM View §9 offsetParent,
/// initial values on getComputedStyle (§6.7.5) and a CSSStyleSheet behind
/// HTMLStyleElement.sheet (§6.1) that google.com's safeStyleSheet helper validates
/// CSS-in-JS rules with.
/// </summary>
[Collection("Engine Tests")]
public sealed class CssomPositioningSurfaceTests
{
    [Fact]
    public async Task OffsetParent_ComputedInitialValues_AndStyleSheetRules()
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(
                "data:text/html,<style>%23lb{position:absolute;top:-1000px}</style>" +
                "<div id='plain'><span id='inner'>x</span></div>" +
                "<div id='lb'><div id='pop' style='position:absolute'>menu</div></div>"));

            var result = await browser.ExecuteScriptAsync(@"
                (function () {
                    var out = [];
                    var inner = document.getElementById('inner'), pop = document.getElementById('pop'), lb = document.getElementById('lb');
                    out.push(inner.offsetParent === document.body);
                    out.push(pop.offsetParent === lb);
                    out.push(lb.offsetParent === document.body);
                    out.push(document.body.offsetParent === null);
                    out.push(getComputedStyle(lb).overflow === 'visible');
                    out.push(getComputedStyle(lb).getPropertyValue('overflow-x') === 'visible');
                    out.push(getComputedStyle(inner).position === 'static');

                    var d = document.implementation.createHTMLDocument('');
                    var s = d.createElement('style'); d.head.appendChild(s);
                    var sheet = s.sheet;
                    sheet.insertRule('.os-s { position: fixed; inset: 0; }', 0);
                    var rule = sheet.cssRules[0];
                    out.push(sheet instanceof CSSStyleSheet);
                    out.push(rule instanceof CSSStyleRule && rule instanceof CSSRule);
                    out.push(rule.cssText === '.os-s { position: fixed; inset: 0; }');
                    out.push(sheet.cssRules.length === 1);
                    sheet.deleteRule(0);
                    out.push(sheet.cssRules.length === 0);
                    return out.join(',');
                })();");

            Assert.Equal("true,true,true,true,true,true,true,true,true,true,true,true", result?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }
}
