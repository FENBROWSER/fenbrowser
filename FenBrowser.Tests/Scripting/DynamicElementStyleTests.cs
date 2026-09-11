using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// A script-created element that was styled (className) before insertion was already
/// StyleDirty, so inserting it never propagated ChildStyleDirty to the new ancestors and
/// the incremental recascade never visited it: it kept default styles and a 0px box.
/// google.com's Settings menu measured 1280px wide that way and its positioner gave up.
/// </summary>
[Collection("Engine Tests")]
public sealed class DynamicElementStyleTests
{
    [Fact]
    public async Task CreatedElement_GetsStylesheetRulesAndGeometry()
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(
                "data:text/html,<style>.w50{width:50px;height:20px}.abs{position:absolute;left:100px;top:200px;width:30px;height:30px}</style><div id='host'></div>"));

            var result = await browser.ExecuteScriptAsync(@"
                (function () {
                    var host = document.getElementById('host');
                    var a = document.createElement('div'); a.className = 'w50'; host.appendChild(a);
                    var b = document.createElement('div'); b.className = 'abs'; host.appendChild(b);
                    var ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
                    return [ra.width, ra.height, rb.left, rb.top, rb.width, getComputedStyle(a).width, getComputedStyle(b).position].join('|');
                })();");

            var later = await browser.ExecuteScriptAsync(@"
                (function () {
                    var a = document.querySelector('.w50'), b = document.querySelector('.abs');
                    var ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
                    return [ra.width, ra.height, rb.left, rb.top, rb.width, getComputedStyle(a).width, getComputedStyle(b).position].join('|');
                })();");
            Assert.Equal("50|20|100|200|30|50px|absolute", result?.ToString());
            Assert.Equal("50|20|100|200|30|50px|absolute", later?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }
}
