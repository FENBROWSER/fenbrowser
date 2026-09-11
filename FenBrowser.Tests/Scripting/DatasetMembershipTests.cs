using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML §3.2.6.1 DOMStringMap: the map's own properties are exactly the element's
/// data-* attributes, so `in` is true only for a present attribute. google.com's
/// document click handler navigates to data-cthref whenever `'cthref' in el.dataset`
/// passes; answering true for every key sent the Settings click to /null.
/// </summary>
[Collection("Engine Tests")]
public sealed class DatasetMembershipTests
{
    [Fact]
    public async Task InOperator_OnDataset_ReflectsOnlyPresentDataAttributes()
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync("data:text/html,<div id='root' data-vt-d='1'></div>"));

            var result = await browser.ExecuteScriptAsync(@"
                (function () {
                    var host = document.getElementById('root');
                    host.dataset.vtFlag = 'ok';
                    return [
                        'vtD' in host.dataset,
                        'vtFlag' in host.dataset,
                        'cthref' in host.dataset,
                        host.dataset.cthref === undefined,
                        host.dataset.vtD === '1',
                        host.getAttribute('data-vt-flag') === 'ok'
                    ].join(',');
                })();");

            Assert.Equal("true,true,false,true,true,true", result?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }
}
