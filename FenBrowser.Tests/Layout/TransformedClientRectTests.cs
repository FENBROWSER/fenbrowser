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

// CSSOM View §6: getBoundingClientRect() and getClientRects() report border boxes
// after the transforms of the element and its ancestors. YouTube centres every
// thumbnail with `top:50%; transform:translateY(-50%)`, and its scripts measured the
// image 67px below where it was painted.
[Collection("Engine Tests")]
public sealed class TransformedClientRectTests
{
    [Fact]
    [Trait("Category", "Layout")]
    public async Task ClientRects_IncludeOwnAndAncestorTransforms()
    {
        using var handler = new PageHandler();
        using var httpClient = new HttpClient(handler);
        var resources = new ResourceManager(httpClient, isPrivate: true);
        var navigation = new NavigationManager(resources);

        using var browser = new BrowserHost(isPrivate: true);
        SetPrivateField(browser, "_resources", resources);
        SetPrivateField(browser, "_navManager", navigation);

        Assert.True(await browser.NavigateAsync("https://transform.test/page"));

        async Task<double> Read(string expression) => double.Parse(
            (await browser.ExecuteScriptAsync("String(" + expression + ")"))?.ToString() ?? "NaN",
            CultureInfo.InvariantCulture);

        // top:50% puts the image at 72px; translateY(-50%) lifts it by half its height.
        Assert.Equal(0d, await Read("document.getElementById('img').getBoundingClientRect().top"), 1);
        Assert.Equal(0d, await Read("document.getElementById('img').getClientRects()[0].top"), 1);
        // scale(2) about the centre of a 100x50 box at (0,200) covers (-50,175)-(150,225).
        Assert.Equal(-50d, await Read("document.getElementById('scaled').getBoundingClientRect().left"), 1);
        Assert.Equal(200d, await Read("document.getElementById('scaled').getBoundingClientRect().width"), 1);
        // A child inherits its ancestor's translation.
        Assert.Equal(330d, await Read("document.getElementById('child').getBoundingClientRect().left"), 1);
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
            "<div style='position:relative;width:256px;height:144px;overflow:hidden'>" +
            "<div id='img' style='position:absolute;top:50%;left:0;width:100%;height:100%;transform:translateY(-50%)'></div></div>" +
            "<div id='scaled' style='position:absolute;top:200px;left:0;width:100px;height:50px;transform:scale(2)'></div>" +
            "<div style='position:absolute;top:300px;left:0;transform:translateX(300px)'>" +
            "<div id='child' style='margin-left:30px;width:10px;height:10px'></div></div>" +
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
