using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// CSSOM §6.7.2 resolved values of width/height: the used size in pixels for a rendered
/// element the property applies to (border-box size under box-sizing:border-box), the
/// computed value for display:none and non-replaced inline elements.
/// </summary>
[Collection("Engine Tests")]
public sealed class ComputedSizesTests
{
    [Fact]
    public async Task SizesResolveToUsedPixels()
    {
        BrowserScriptEngineRuntime.Reset();
        try
        {
            using var browser = new BrowserHost();
            Assert.True(await browser.NavigateAsync(
                "data:text/html,<body style='margin:0'><div id='cb' style='width:200px;height:100px'>" +
                "<div id='pct' style='width:50%;height:100%'></div>" +
                "<div id='bb' style='box-sizing:border-box;width:calc(100% - 20px);height:40px;padding:5px'></div>" +
                "<div id='auto' style='height:10px;padding:0 10px'></div>" +
                "<div id='none' style='display:none;width:25%'></div>" +
                "<span id='inl' style='width:30%'>x</span></div></body>"));

            var result = await browser.ExecuteScriptAsync(@"
                (function () {
                    function read(id) {
                        var cs = getComputedStyle(document.getElementById(id));
                        return cs.width + ' ' + cs.getPropertyValue('height');
                    }
                    return ['pct', 'bb', 'auto', 'none'].map(read).join('|') + '|' +
                        getComputedStyle(document.getElementById('inl')).width;
                })();");

            Assert.Equal("100px 100px|180px 40px|180px 10px|25% auto|30%", result?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }
}
