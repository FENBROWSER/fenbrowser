using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Network;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Scripting;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

[CollectionDefinition("Synthetic CAPTCHA", DisableParallelization = true)]
public sealed class SyntheticCaptchaTestCollection;

[Collection("Synthetic CAPTCHA")]
public sealed class SyntheticCaptchaFlowTests
{
    [Fact]
    public async Task CrossOriginChallenge_LoadsRedirectedImages_ExpandsAndAcceptsInput()
    {
        const int viewportWidth = 640;
        const int viewportHeight = 480;
        var parentUri = new Uri("https://parent.test/challenge");
        var frameUri = new Uri("https://challenge.test/widget");
        var imageBytes = CreatePngBytes(24, 24, SKColors.CornflowerBlue);
        var redirectedCookies = new List<string>();
        var redirectedSites = new List<string>();
        var redirectedDestinations = new List<string>();
        var previousThirdPartySetting = BrowserSettings.Instance.BlockThirdPartyCookies;

        BrowserSettings.Instance.BlockThirdPartyCookies = false;
        ImageLoader.ClearCache();
        try
        {
            using var client = new HttpClient(new StubHandler(request =>
            {
                if (string.Equals(request.RequestUri?.Host, "challenge.test", StringComparison.OrdinalIgnoreCase))
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect)
                    {
                        RequestMessage = request
                    };
                    response.Headers.Location = new Uri(
                        $"https://assets.challenge.test{request.RequestUri.AbsolutePath.Replace("/start/", "/final/")}");
                    response.Headers.TryAddWithoutValidation(
                        "Set-Cookie",
                        "challenge_session=opaque; Domain=challenge.test; Path=/; Secure; SameSite=None");
                    return response;
                }

                redirectedCookies.Add(request.Headers.TryGetValues("Cookie", out var cookies)
                    ? string.Join(";", cookies)
                    : string.Empty);
                redirectedSites.Add(Assert.Single(request.Headers.GetValues("Sec-Fetch-Site")));
                redirectedDestinations.Add(Assert.Single(request.Headers.GetValues("Sec-Fetch-Dest")));
                var imageResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(imageBytes)
                };
                imageResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return imageResponse;
            }));
            var resources = new ResourceManager(client, isPrivate: true);
            using var host = new BrowserHost(isPrivate: true);
            var renderer = new SkiaDomRenderer();
            host.EnableJavaScript = true;
            host.SetActiveRenderer(renderer);
            host.Engine.SetExternalRenderer(renderer);

            const string parentHtml = """
<!doctype html>
<html>
<head><style>html,body{margin:0}iframe{display:block;border:0;margin:20px}</style></head>
<body>
  <iframe id="challenge" src="https://challenge.test/widget" width="304" height="78"></iframe>
  <script>
    addEventListener('message', function (event) {
      if (event.data === 'expand') {
        var frame = document.getElementById('challenge');
        frame.setAttribute('width', '420');
        frame.setAttribute('height', '300');
      }
      if (event.data === 'verified') document.body.setAttribute('data-verified', 'yes');
    });
  </script>
</body>
</html>
""";

            await host.Engine.RenderAsync(
                parentHtml,
                parentUri,
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<Stream>(null),
                _ => { },
                viewportWidth,
                viewportHeight,
                forceJavascript: true);

            var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
            var iframe = Assert.IsType<Element>(root.OwnerDocument?.GetElementById("challenge"));
            while (iframe.FirstChild != null)
            {
                iframe.RemoveChild(iframe.FirstChild);
            }

            var frameDocument = new HtmlParser(
                """
<!doctype html>
<html>
<head>
  <style>
    html,body{margin:0;width:100%;height:100%}
    #anchor{display:block;width:180px;height:40px;margin:12px}
    #grid{display:none;margin:8px 12px}
    #grid.visible{display:block}
    #tile,#verify{display:block;width:180px;height:42px;margin-top:12px}
    img{width:24px;height:24px}
  </style>
</head>
<body data-selected="no">
  <button id="anchor" type="button">I am human</button>
  <div id="grid">
    <button id="tile" type="button"><img id="image-1" data-src="https://challenge.test/start/one.png">Select image</button>
    <img id="image-2" data-src="https://challenge.test/start/two.png">
    <button id="verify" type="button">Verify</button>
  </div>
  <script>
    document.getElementById('anchor').addEventListener('click', function () {
      document.body.setAttribute('data-anchor', 'yes');
      var grid = document.getElementById('grid');
      grid.setAttribute('class', 'visible');
      var images = document.querySelectorAll('img');
      for (var i = 0; i < images.length; i++) images[i].setAttribute('src', images[i].getAttribute('data-src'));
      parent.postMessage('expand', '*');
    });
    document.getElementById('tile').addEventListener('click', function () {
      document.body.setAttribute('data-selected', 'yes');
    });
    document.getElementById('verify').addEventListener('click', function () {
      document.body.setAttribute('data-verify-clicked', 'yes');
      if (document.body.getAttribute('data-selected') === 'yes') document.body.setAttribute('data-verified', 'yes');
    });
  </script>
</body>
</html>
""",
                frameUri).Parse();
            iframe.AppendChild(frameDocument);
            var scriptEngine = Assert.IsType<FenJsBrowserScriptEngine>(host.Engine.ScriptEngine);
            await scriptEngine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);

            Assert.Equal("true", scriptEngine.Evaluate(
                "String(document.getElementById('challenge').contentDocument===null)")?.ToString());
            Assert.Equal("undefined", scriptEngine.Evaluate(
                "typeof document.getElementById('challenge').contentWindow.document")?.ToString());

            RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
            var anchor = Assert.IsType<Element>(frameDocument.GetElementById("anchor"));
            ClickCenter(host, renderer, anchor);
            Assert.Equal("yes", frameDocument.Body?.GetAttribute("data-anchor"));
            await WaitForAsync(
                () =>
                {
                    scriptEngine.Evaluate("void 0");
                    return string.Equals(iframe.GetAttribute("height"), "300", StringComparison.Ordinal);
                },
                "challenge expansion message");
            await host.FlushPendingLayoutAsync();
            RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);

            Assert.True(renderer.LastLayout.TryGetElementRect(iframe, out var expandedFrame));
            Assert.InRange(expandedFrame.Width, 419f, 421f);
            Assert.InRange(expandedFrame.Height, 299f, 301f);

            var imageUrls = frameDocument.QuerySelectorAll("img")
                .OfType<Element>()
                .Select(image => image.GetAttribute("src"))
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .ToArray();
            Assert.Equal(2, imageUrls.Length);
            using (ImageLoader.EnterRequestContext(new ImageLoader.ImageLoaderRequestContext
            {
                OwnerId = "synthetic-captcha",
                FetchBytesAsync = uri => resources.FetchBytesAsync(new FetchContext
                {
                    RequestUri = uri,
                    InitiatorUri = frameUri,
                    FrameDocumentUri = frameUri,
                    TopLevelDocumentUri = parentUri,
                    Destination = "image",
                    Mode = "no-cors",
                    CredentialsMode = "include"
                }, BrowserNetworkCapabilities.ImageAcceptHeader)
            }))
            {
                foreach (var imageUrl in imageUrls)
                {
                    ImageLoader.GetImage(imageUrl);
                }

                await WaitForAsync(
                    () => imageUrls.All(ImageLoader.ContainsCachedImage),
                    "redirected challenge images to decode");
            }

            Assert.Equal(2, redirectedCookies.Count);
            Assert.All(redirectedCookies, cookie => Assert.Contains("challenge_session=opaque", cookie));
            Assert.All(redirectedSites, site => Assert.Equal("same-site", site));
            Assert.All(redirectedDestinations, destination => Assert.Equal("image", destination));

            var tile = Assert.IsType<Element>(frameDocument.GetElementById("tile"));
            var verify = Assert.IsType<Element>(frameDocument.GetElementById("verify"));
            RenderFrame(renderer, root, host.ComputedStyles, viewportWidth, viewportHeight);
            Assert.True(renderer.LastLayout.TryGetElementRect(verify, out var verifyRect));
            Assert.True(verifyRect.Top > expandedFrame.Top + 78f);
            Assert.True(verifyRect.Bottom <= expandedFrame.Bottom);

            ClickCenter(host, renderer, tile);
            Assert.Equal("yes", frameDocument.Body?.GetAttribute("data-selected"));
            ClickCenter(host, renderer, verify);
            Assert.Equal("yes", frameDocument.Body?.GetAttribute("data-verify-clicked"));

            Assert.Equal("yes", frameDocument.Body?.GetAttribute("data-selected"));
            Assert.Equal("yes", frameDocument.Body?.GetAttribute("data-verified"));
        }
        finally
        {
            BrowserSettings.Instance.BlockThirdPartyCookies = previousThirdPartySetting;
            ImageLoader.ClearCache();
        }
    }

    private static void ClickCenter(BrowserHost host, SkiaDomRenderer renderer, Element element)
    {
        Assert.True(renderer.LastLayout.TryGetElementRect(element, out var rect), $"Missing layout rect for #{element.Id}.");
        var x = rect.Left + (rect.Width / 2f);
        var y = rect.Top + (rect.Height / 2f);
        Assert.Same(element, host.HitTestElementAtViewportPoint(x, y));
        host.OnClick(x, y, button: 0);
    }

    private static void RenderFrame(
        SkiaDomRenderer renderer,
        Element root,
        Dictionary<Node, CssComputed> styles,
        int viewportWidth,
        int viewportHeight)
    {
        using var bitmap = new SKBitmap(viewportWidth, viewportHeight);
        using var canvas = new SKCanvas(bitmap);
        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = styles,
            Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
            BaseUrl = "https://parent.test/challenge",
            InvalidationReason = RenderFrameInvalidationReason.Navigation,
            RequestedBy = nameof(SyntheticCaptchaFlowTests),
            EmitVerificationReport = false
        });
        canvas.Flush();
    }

    private static async Task WaitForAsync(Func<bool> predicate, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(predicate(), $"Timed out waiting for {description}.");
    }

    private static byte[] CreatePngBytes(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_handler(request));
    }
}
