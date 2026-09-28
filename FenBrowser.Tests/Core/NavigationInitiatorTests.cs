using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// HTML 7.4.2.2 / Fetch Metadata 2.1: a navigation the page starts has the current document
/// as its initiator, so it is sent with that document's Referer and a Sec-Fetch-Site worked
/// out from it. Only a navigation the user starts (a typed URL) is Sec-Fetch-Site: none -
/// the value Fetch Metadata CSRF defences let through. BrowserHost passed no initiator for
/// any navigation, so every page-started one claimed to be typed by the user.
/// </summary>
[Collection("Engine Tests")]
public sealed class NavigationInitiatorTests
{
    internal static readonly ConcurrentQueue<string> Log = new();

    private const string PageHtml = "<!doctype html><html><body><p>page</p></body></html>";

    [Fact]
    public async Task PageStartedNavigation_CarriesTheDocumentAsItsInitiator()
    {
        ElementStateManager.Reset();
        BrowserScriptEngineRuntime.Reset();
        var seen = new ConcurrentDictionary<string, (string Site, string Referer)>();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = ServeAsync(listener, seen, stop.Token);
        try
        {
            using var browser = new BrowserHost();
            var start = $"http://127.0.0.1:{port}/start";
            Assert.True(await browser.NavigateUserInputAsync(start));
            Assert.True(await browser.NavigateAsync($"http://127.0.0.1:{port}/next"));

            Assert.Equal("none", seen["/start"].Site);
            Assert.Equal(string.Empty, seen["/start"].Referer);

            Assert.True(seen["/next"].Site == "same-origin", string.Join(" || ", Log));
            Assert.Equal(start, seen["/next"].Referer);
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

    private static async Task ServeAsync(
        TcpListener listener,
        ConcurrentDictionary<string, (string Site, string Referer)> seen,
        CancellationToken token)
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

            _ = Task.Run(() => RespondAsync(client, seen, token), token);
        }
    }

    private static async Task RespondAsync(
        TcpClient client,
        ConcurrentDictionary<string, (string Site, string Referer)> seen,
        CancellationToken token)
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

            var lines = request.ToString().Split("\r\n");
            var path = lines[0].Split(' ').Skip(1).FirstOrDefault() ?? "/";
            string Header(string name) => lines
                .Where(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                .Select(l => l[(name.Length + 1)..].Trim())
                .FirstOrDefault() ?? string.Empty;
            if (path is "/start" or "/next")
            {
                seen[path] = (Header("Sec-Fetch-Site"), Header("Referer"));
            }

            Log.Enqueue($"{path} site={Header("Sec-Fetch-Site")} ref={Header("Referer")} mode={Header("Sec-Fetch-Mode")} dest={Header("Sec-Fetch-Dest")}");

            var body = Encoding.UTF8.GetBytes(PageHtml);
            var header =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token);
            await stream.WriteAsync(body, token);
        }
    }
}
