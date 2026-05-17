using System.Net.Http;
using FenBrowser.Core.Network.Handlers;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class CorsHandlerTests
    {
        [Fact]
        public void GetCorsUnsafeRequestHeaderNames_UsesAuthorHeaderList_WhenPresent()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/data");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Cookie", "session=abc");
            request.Headers.TryAddWithoutValidation("X-Custom-Trace", "1");

            CorsHandler.SetAuthorRequestHeaders(request, new[] { "X-Custom-Trace" });
            var unsafeHeaders = CorsHandler.GetCorsUnsafeRequestHeaderNames(request);

            Assert.Single(unsafeHeaders);
            Assert.Equal("x-custom-trace", unsafeHeaders[0]);
        }

        [Fact]
        public void GetCorsUnsafeRequestHeaderNames_DoesNotTreatSyntheticBrowserHeadersAsUnsafe()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/data");
            request.Headers.TryAddWithoutValidation("Cookie", "session=abc");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
            request.Headers.TryAddWithoutValidation("Origin", "https://app.example.test");

            var unsafeHeaders = CorsHandler.GetCorsUnsafeRequestHeaderNames(request);

            Assert.Empty(unsafeHeaders);
        }
    }
}
