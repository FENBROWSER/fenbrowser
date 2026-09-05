using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Layout;

// A percentage on an inline-level box resolves against its containing block: the
// nearest ancestor that is neither an inline box nor an anonymous block. The
// layout state was answering instead, and it carried the viewport's size - then,
// once the box had been sized, the size it had just produced, so the error
// squared on the next pass.
//
// reCAPTCHA's image challenge is built from this exact shape. Each of the nine
// tiles is one <img> of the whole payload at width/height:300% inside a small
// overflow:hidden wrapper, nudged into place with left/top:-100%, so the wrapper
// frames one ninth of it. Against a 100px wrapper Chrome puts that image at
// -100,-100 300x300; we made it 5760px wide, then 17280px, and offset it by its
// own width - so every tile framed a sliver of a hugely magnified image and the
// challenge rendered as smeared colour bands instead of photographs.
[Collection("Engine Tests")]
public sealed class PercentSizedInlineBoxTests
{
    [Fact]
    [Trait("Category", "Layout")]
    public async Task PercentSizedImage_ResolvesAgainstItsWrapper_NotTheViewport()
    {
        using var handler = new PageHandler();
        using var httpClient = new HttpClient(handler);
        var resources = new ResourceManager(httpClient, isPrivate: true);
        var navigation = new NavigationManager(resources);

        using var browser = new BrowserHost(isPrivate: true);
        SetPrivateField(browser, "_resources", resources);
        SetPrivateField(browser, "_navManager", navigation);

        Assert.True(await browser.NavigateAsync("https://tile.test/page"));

        var rect = (await browser.ExecuteScriptAsync(
            "var b=document.getElementById('tile').getBoundingClientRect();" +
            "[Math.round(b.left),Math.round(b.top),Math.round(b.width),Math.round(b.height)].join('/')"))?.ToString();

        // Chrome, same markup: -100/-100/300/300.
        Assert.Equal("-100/-100/300/300", rect);
    }

    private static void SetPrivateField(object owner, string fieldName, object value)
    {
        var field = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(owner, value);
    }

    private sealed class PageHandler : HttpMessageHandler
    {
        private const string Html =
            "<!doctype html><html><body style='margin:0'>" +
            "<div id='wrap' style='overflow:hidden;position:relative;width:100px;height:100px'>" +
            "<img id='tile' style='width:300%;height:300%;position:relative;left:-100%;top:-100%'>" +
            "</div></body></html>";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(Html, Encoding.UTF8, "text/html")
            });
        }
    }
}
