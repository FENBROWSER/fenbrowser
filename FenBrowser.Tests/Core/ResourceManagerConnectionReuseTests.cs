using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Network;
using Xunit;

namespace FenBrowser.Tests.Core
{
    // Image fetches send with ResponseHeadersRead, so a response whose body is never
    // read (an HTTP error) holds its keep-alive connection until it is disposed. With
    // the per-server connection limit used up, every later request to that server
    // waited forever - a page with ten broken images stalled the next navigation.
    public class ResourceManagerConnectionReuseTests
    {
        [Fact]
        public async Task FailedImageFetches_ReleaseTheirConnections()
        {
            using var server = new KeepAliveNotFoundServer();
            using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 2 };
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            var page = new Uri($"http://127.0.0.1:{server.Port}/page");

            for (var i = 0; i < 6; i++)
            {
                var result = await manager.FetchBytesDetailedAsync(new FetchContext
                {
                    RequestUri = new Uri($"http://127.0.0.1:{server.Port}/missing-{i}.png"),
                    InitiatorUri = page,
                    FrameDocumentUri = page,
                    TopLevelDocumentUri = page,
                    Destination = "image",
                    Mode = "no-cors",
                    CredentialsMode = "include"
                }).WaitAsync(TimeSpan.FromSeconds(5));

                Assert.False(result.Succeeded);
                Assert.Equal(404, result.StatusCode);
            }
        }

        [Fact]
        public async Task FailedTextFetches_ReleaseTheirConnections()
        {
            using var server = new KeepAliveNotFoundServer();
            using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 2 };
            using var client = new HttpClient(handler);
            var manager = new ResourceManager(client, isPrivate: true);
            var page = new Uri($"http://127.0.0.1:{server.Port}/page");

            for (var i = 0; i < 6; i++)
            {
                var result = await manager.FetchTextDetailedAsync(
                    new Uri($"http://127.0.0.1:{server.Port}/missing-{i}.css"),
                    referer: page,
                    secFetchDest: "style").WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(404, result.StatusCode);
            }
        }

        // Answers every request on a connection with a 404 carrying a body, and keeps
        // the connection open for the next request.
        private sealed class KeepAliveNotFoundServer : IDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new();

            public KeepAliveNotFoundServer()
            {
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _ = AcceptLoopAsync();
            }

            public int Port { get; }

            private async Task AcceptLoopAsync()
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                        _ = ServeAsync(socket);
                    }
                }
                catch (Exception)
                {
                    // Listener stopped.
                }
            }

            private async Task ServeAsync(TcpClient socket)
            {
                using (socket)
                {
                    var stream = socket.GetStream();
                    var buffer = new byte[8192];
                    var pending = new StringBuilder();
                    var body = new string('x', 4096);
                    var response = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 404 Not Found\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: keep-alive\r\n\r\n{body}");
                    try
                    {
                        while (!_stop.IsCancellationRequested)
                        {
                            var read = await stream.ReadAsync(buffer, _stop.Token);
                            if (read == 0)
                            {
                                return;
                            }

                            pending.Append(Encoding.ASCII.GetString(buffer, 0, read));
                            string text;
                            int end;
                            while ((end = (text = pending.ToString()).IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                            {
                                pending.Remove(0, end + 4);
                                await stream.WriteAsync(response, _stop.Token);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Client went away or the server stopped.
                    }
                }
            }

            public void Dispose()
            {
                _stop.Cancel();
                _listener.Stop();
            }
        }
    }
}
