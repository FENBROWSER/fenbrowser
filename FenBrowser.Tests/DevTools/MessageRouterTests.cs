using System.Threading.Tasks;
using FenBrowser.DevTools.Core.Protocol;
using Xunit;

namespace FenBrowser.Tests.DevTools
{
    public class MessageRouterTests
    {
        [Fact]
        public async Task DispatchJsonAsync_ReturnsParseError_ForMalformedJson()
        {
            var router = new MessageRouter();

            var responseJson = await router.DispatchJsonAsync("{not-json");
            var response = ProtocolJson.Deserialize<ProtocolResponse>(responseJson);

            Assert.NotNull(response);
            Assert.False(response.IsSuccess);
            Assert.Equal(-32700, response.Error.Code);
        }

        [Fact]
        public void RegisterHandler_RejectsDuplicateDomains()
        {
            var router = new MessageRouter();
            router.RegisterHandler(new StubHandler("DOM"));

            var ex = Assert.Throws<System.InvalidOperationException>(() => router.RegisterHandler(new StubHandler("DOM")));

            Assert.Contains("DOM", ex.Message);
        }

        private sealed class StubHandler : IProtocolHandler
        {
            public StubHandler(string domain)
            {
                Domain = domain;
            }

            public string Domain { get; }

            public Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
            {
                return Task.FromResult(ProtocolResponse.Success(request.Id, new { ok = true }));
            }
        }
    }
}
