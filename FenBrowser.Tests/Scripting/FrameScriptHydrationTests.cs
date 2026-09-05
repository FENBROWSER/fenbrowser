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

namespace FenBrowser.Tests.Scripting;

// Setting a frame document up binds its realm, which discards whatever the
// previous document's scripts had built. Doing it twice for the same document
// therefore empties a frame that was already running: on reCAPTCHA the anchor
// widget rendered, its bundle wired the checkbox, and a frame switch arriving as
// setup finished wiped `window.recaptcha` and every listener with it, so the
// click that followed reached nothing and the challenge frame never opened.
//
// The guard against that keyed off a marker attribute written when setup ends,
// which leaves a window: a caller that checked just before the write found no
// marker, and by the time it looked for a setup to join, that setup had already
// finished. Both tests missed and the frame was set up a second time.
[Collection("Engine Tests")]
public sealed class FrameScriptHydrationTests
{
    [Fact]
    public async Task FrameSwitchAfterSetupCompletes_DoesNotRunTheFrameDocumentTwice()
    {
        using var handler = new FrameContentHandler();
        using var httpClient = new HttpClient(handler);
        var resources = new ResourceManager(httpClient, isPrivate: true);
        var navigation = new NavigationManager(resources);

        using var browser = new BrowserHost(isPrivate: true);
        SetPrivateField(browser, "_resources", resources);
        SetPrivateField(browser, "_navManager", navigation);

        Assert.True(await browser.NavigateAsync("https://frame.test/page"));

        var frameId = await browser.FindElementAsync("css selector", "#child");
        await WaitForFrameSetupAsync(browser, frameId);

        // State the frame's own scripts built. A second setup discards it.
        await browser.ExecuteScriptAsync("globalThis.__wired = 'yes';");

        // Stand in for the race: the marker is written only once setup ends, so
        // a caller that looked a moment earlier saw exactly this state.
        await browser.ExecuteScriptAsync(
            "document.documentElement.removeAttribute('data-fen-wd-frame-scripts-hydrated');" +
            "document.documentElement.removeAttribute('data-fen-wd-frame-scripts-hydrated-url');");

        // Finding an element inside the selected frame asks for that frame's
        // document to be set up; with no marker to stop it, the old guard let a
        // second setup start because the first had already finished.
        await browser.FindElementAsync("css selector", "#frame-body");

        Assert.Equal(
            "yes",
            (await browser.ExecuteScriptAsync("String(globalThis.__wired)"))?.ToString());
        Assert.Equal(
            "1",
            (await browser.ExecuteScriptAsync("String(globalThis.__setupRuns)"))?.ToString());
    }

    // The frame is fetched and set up off the navigation, so poll until its
    // script has run rather than assuming it has by the time navigate returns.
    private static async Task WaitForFrameSetupAsync(BrowserHost browser, string frameId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await browser.SwitchToFrameAsync(frameId);
            if ((await browser.ExecuteScriptAsync("String(globalThis.__setupRuns)"))?.ToString() == "1")
            {
                return;
            }

            await browser.SwitchToFrameAsync(null);
            await Task.Delay(100);
        }

        Assert.Fail("the frame document never ran its script");
    }

    private static void SetPrivateField(object owner, string fieldName, object value)
    {
        var field = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(owner, value);
    }

    private sealed class FrameContentHandler : HttpMessageHandler
    {
        private const string ParentHtml =
            "<!doctype html><html><body><p id='marker'>parent</p>" +
            "<iframe id='child' src='/frame' width='200' height='100'></iframe>" +
            "</body></html>";

        private const string FrameHtml =
            "<!doctype html><html><body id='frame-body'>" +
            "<script>globalThis.__setupRuns = (globalThis.__setupRuns || 0) + 1;</script>" +
            "</body></html>";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = path == "/frame" ? FrameHtml : ParentHtml;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "text/html")
            });
        }
    }
}
