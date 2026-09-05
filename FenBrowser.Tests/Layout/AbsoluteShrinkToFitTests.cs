using System;
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

// CSS 2.1 10.3.7: an absolutely positioned box with `left` set and both `width`
// and `right` auto is sized shrink-to-fit. Measuring that width means asking the
// contents how wide they want to be - and a percentage width cannot answer,
// because it resolves against the box being measured. Reading the child's
// provisional pixel width instead made the measurement circular: the box
// reported whatever it had been stretched to on the way in.
//
// reCAPTCHA's challenge wrapper is exactly this shape and its only content is an
// iframe at width:100%, so it came out as wide as the viewport - measured at
// 1904px where Chrome gives 306px - and painted as a full-width white panel the
// one time the widget revealed it.
[Collection("Engine Tests")]
public sealed class AbsoluteShrinkToFitTests
{
    [Fact]
    [Trait("Category", "Layout")]
    public async Task AbsoluteAutoWidth_WithPercentWidthReplacedChild_DoesNotTakeTheContainingBlockWidth()
    {
        using var handler = new PageHandler();
        using var httpClient = new HttpClient(handler);
        var resources = new ResourceManager(httpClient, isPrivate: true);
        var navigation = new NavigationManager(resources);

        using var browser = new BrowserHost(isPrivate: true);
        SetPrivateField(browser, "_resources", resources);
        SetPrivateField(browser, "_navManager", navigation);

        Assert.True(await browser.NavigateAsync("https://shrink.test/page"));

        var wrapperWidth = double.Parse(
            (await browser.ExecuteScriptAsync(
                "String(document.getElementById('wrap').getBoundingClientRect().width)"))?.ToString()
            ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);
        // The iframe has no intrinsic size, so it contributes the 300x150 default
        // every engine uses, and the wrapper lands near 302 with its border. The
        // failure this guards against reports the containing block's width, which
        // in this harness is over 1900.
        Assert.InRange(wrapperWidth, 1d, 400d);
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
            "<!doctype html><html><body style=\"padding:20px\">" +
            "<div style='max-width:400px'><p>line one</p><p>line two</p></div>" +
            "<div id='wrap' style=\"background-color:#fff;border:1px solid #ccc;position:absolute;" +
            "opacity:0;visibility:hidden;left:0px;top:-10000px;\">" +
            "<div id='inner' style='position:relative;'>" +
            "<iframe id='fr' style='width: 100%; height: 100%;'></iframe></div></div>" +
            "</body></html>";

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
