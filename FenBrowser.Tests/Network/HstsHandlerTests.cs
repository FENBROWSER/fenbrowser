using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.Core.Network.Handlers;

namespace FenBrowser.Tests.Network
{
    public class HstsHandlerTests
    {
        [Fact]
        public async Task HandleAsync_MaxAgeZero_RemovesStoredPolicy()
        {
            var cacheRoot = Path.Combine(Path.GetTempPath(), "fen-hsts-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cacheRoot);

            try
            {
                var handler = new HstsHandler(cacheRoot);

                // Seed HSTS with includeSubDomains.
                var seedRequest = new HttpRequestMessage(HttpMethod.Get, "http://example.com/start");
                var seedContext = new NetworkContext(seedRequest);
                await handler.HandleAsync(seedContext, () =>
                {
                    seedContext.Response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.com/start")
                    };
                    seedContext.Response.Headers.TryAddWithoutValidation("Strict-Transport-Security", "max-age=3600; includeSubDomains");
                    return Task.CompletedTask;
                }, default);

                // Confirm upgrade applies to subdomain.
                var upgradedRequest = new HttpRequestMessage(HttpMethod.Get, "http://sub.example.com/data");
                var upgradedContext = new NetworkContext(upgradedRequest);
                await handler.HandleAsync(upgradedContext, () => Task.CompletedTask, default);
                Assert.Equal("https", upgradedContext.Request.RequestUri!.Scheme);

                // Clear HSTS with max-age=0.
                var clearRequest = new HttpRequestMessage(HttpMethod.Get, "http://example.com/clear");
                var clearContext = new NetworkContext(clearRequest);
                await handler.HandleAsync(clearContext, () =>
                {
                    clearContext.Response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.com/clear")
                    };
                    clearContext.Response.Headers.TryAddWithoutValidation("Strict-Transport-Security", "max-age=0");
                    return Task.CompletedTask;
                }, default);

                // Upgrade should no longer happen.
                var plainRequest = new HttpRequestMessage(HttpMethod.Get, "http://sub.example.com/after-clear");
                var plainContext = new NetworkContext(plainRequest);
                await handler.HandleAsync(plainContext, () => Task.CompletedTask, default);
                Assert.Equal("http", plainContext.Request.RequestUri!.Scheme);
            }
            finally
            {
                if (Directory.Exists(cacheRoot))
                {
                    Directory.Delete(cacheRoot, recursive: true);
                }
            }
        }

        [Fact]
        public async Task HandleAsync_DoesNotUpgradeNonHttpSchemes()
        {
            var cacheRoot = Path.Combine(Path.GetTempPath(), "fen-hsts-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cacheRoot);

            try
            {
                var handler = new HstsHandler(cacheRoot);

                // Seed HSTS policy.
                var seedRequest = new HttpRequestMessage(HttpMethod.Get, "http://example.com/start");
                var seedContext = new NetworkContext(seedRequest);
                await handler.HandleAsync(seedContext, () =>
                {
                    seedContext.Response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.com/start")
                    };
                    seedContext.Response.Headers.TryAddWithoutValidation("Strict-Transport-Security", "max-age=3600");
                    return Task.CompletedTask;
                }, default);

                // HSTS must not rewrite non-http schemes.
                var wsRequest = new HttpRequestMessage(HttpMethod.Get, "ws://example.com/socket");
                var wsContext = new NetworkContext(wsRequest);
                await handler.HandleAsync(wsContext, () => Task.CompletedTask, default);

                Assert.Equal("ws", wsContext.Request.RequestUri!.Scheme);
            }
            finally
            {
                if (Directory.Exists(cacheRoot))
                {
                    Directory.Delete(cacheRoot, recursive: true);
                }
            }
        }
    }
}
