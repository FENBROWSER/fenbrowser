using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// CSSOM §6.7.2 resolved values of top/right/bottom/left: computed values for a
/// static or box-less element, used values for a positioned one.
/// </summary>
[Collection("Engine Tests")]
public sealed class ComputedInsetsTests
{
    [Fact]
    public async Task InsetsResolvePerPositionScheme()
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(
                "data:text/html,<div id='cb' style='width:200px;height:100px'><div id='t'></div></div>"));

            var result = await browser.ExecuteScriptAsync(@"
                (function () {
                    var t = document.getElementById('t'), out = [];
                    function read(css) {
                        t.style.cssText = css;
                        var cs = getComputedStyle(t);
                        return [cs.top, cs.right, cs.bottom, cs.left].join(' ');
                    }
                    out.push(read('position: static; font-size: 10px; top: 1em; left: 10%'));
                    out.push(read('display: none; font-size: 10px; top: 2em; bottom: calc(10% - 1px)'));
                    out.push(read('position: relative; top: 10%; left: 25%'));
                    out.push(read('position: relative; bottom: 3px'));
                    out.push(read('position: sticky; top: 10%'));
                    return out.join('|');
                })();");

            Assert.Equal(
                "10px auto auto 10%|20px auto calc(10% - 1px) auto|10px -50px -10px 50px|-3px 0px 3px 0px|10px auto auto auto",
                result?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }
}
