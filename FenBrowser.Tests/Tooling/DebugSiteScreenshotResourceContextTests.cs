using System.Net;
using System.Net.Sockets;
using System.Text;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Tooling;
using SkiaSharp;

namespace FenBrowser.Tests.Tooling;

/// <summary>
/// debug-site renders its screenshot with its own renderer. The page's images were fetched
/// and cached under the browser's resource-loader context, so a capture taken outside that
/// context looked each one up under a different cache key and painted the placeholder -
/// bing.com's homepage photo was missing from every capture while the Host showed it.
/// </summary>
[Collection("Engine Tests")]
public sealed class DebugSiteScreenshotResourceContextTests
{
    private const string PageHtml = """
<!doctype html>
<html>
<head>
<style>
  html, body { margin: 0; background: #ffffff; }
  #bg { width: 200px; height: 200px; background-image: url(/bg.svg); }
</style>
</head>
<body><div id="bg"></div></body>
</html>
""";

    private const string RedSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">" +
        "<rect width=\"200\" height=\"200\" fill=\"#ff0000\"/></svg>";

    [Fact]
    public async Task Screenshot_PaintsBackgroundImagesTheBrowserAlreadyLoaded()
    {
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = ServeAsync(listener, stop.Token);
        try
        {
            using var browser = Program.CreateDebugSiteBrowserHost();
            Assert.True(await browser.NavigateAsync($"http://127.0.0.1:{port}/page.html"));

            var imageUrl = $"http://127.0.0.1:{port}/bg.svg";
            var deadline = DateTime.UtcNow.AddSeconds(10);
            bool loaded;
            do
            {
                using (browser.EnterImageLoaderContext())
                {
                    loaded = ImageLoader.GetImage(imageUrl) != null;
                }

                if (!loaded)
                {
                    await Task.Delay(50);
                }
            }
            while (!loaded && DateTime.UtcNow < deadline);

            Assert.True(loaded, "the page never loaded its background image");

            var capture = Program.CaptureDebugSiteScreenshot(
                browser.GetDomRoot(),
                browser.ComputedStyles,
                browser.CurrentUri.AbsoluteUri,
                browser.EnterImageLoaderContext);

            Assert.True(capture.Captured, capture.Error);
            using var png = SKBitmap.Decode(capture.Path);
            var pixel = png.GetPixel(100, 100);
            Assert.True(
                pixel.Red > 200 && pixel.Green < 60 && pixel.Blue < 60,
                $"expected the red background image at (100,100), got {pixel}");
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try
            {
                await server;
            }
            catch (Exception)
            {
                // The accept loop ends by cancellation or by the stopped listener.
            }

            BrowserScriptEngineRuntime.Reset();
            ElementStateManager.Reset();
        }
    }

    private static async Task ServeAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => RespondAsync(client, token), token);
        }
    }

    private static async Task RespondAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, token);
                if (read == 0)
                {
                    return;
                }

                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            var requestLine = request.ToString().Split("\r\n")[0];
            var path = requestLine.Split(' ').Skip(1).FirstOrDefault() ?? "/";
            var isImage = path.StartsWith("/bg.svg", StringComparison.Ordinal);
            var body = Encoding.UTF8.GetBytes(isImage ? RedSvg : PageHtml);
            var header =
                "HTTP/1.1 200 OK\r\n" +
                $"Content-Type: {(isImage ? "image/svg+xml" : "text/html; charset=utf-8")}\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
            await stream.WriteAsync(body, token);
        }
    }
}
