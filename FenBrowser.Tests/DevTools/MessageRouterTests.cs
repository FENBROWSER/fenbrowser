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

        // DEVTOOLS-001: dispatch ordering is per domain. Same-domain requests must
        // stay serialized; unrelated domains must not block each other.
        [Fact]
        public async Task SameDomain_DispatchesRemainSerialized()
        {
            var router = new MessageRouter();
            var entered = new List<int>();
            var firstEntered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            router.RegisterHandler(new ScriptedHandler("DOM", id =>
            {
                lock (entered)
                {
                    entered.Add(id);
                }

                if (id == 1)
                {
                    firstEntered.TrySetResult();
                    return release.Task;
                }

                return Task.CompletedTask;
            }));

            var first = router.DispatchAsync(new ProtocolRequest { Id = 1, Method = "DOM.getDocument" });
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var second = router.DispatchAsync(new ProtocolRequest { Id = 2, Method = "DOM.getDocument" });
            await Task.Delay(200);
            lock (entered)
            {
                Assert.Equal(new[] { 1 }, entered);
            }

            release.TrySetResult();
            var responses = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            lock (entered)
            {
                Assert.Equal(new[] { 1, 2 }, entered);
            }

            Assert.All(responses, response => Assert.True(response.IsSuccess));
        }

        [Fact]
        public async Task DistinctDomains_CanDispatchConcurrently()
        {
            var router = new MessageRouter();
            var domEntered = new TaskCompletionSource();
            var releaseDom = new TaskCompletionSource();
            router.RegisterHandler(new ScriptedHandler("DOM", _ =>
            {
                domEntered.TrySetResult();
                return releaseDom.Task;
            }));
            router.RegisterHandler(new StubHandler("CSS"));

            var domDispatch = router.DispatchAsync(new ProtocolRequest { Id = 1, Method = "DOM.getDocument" });
            await domEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // CSS must complete while the DOM handler is still blocked.
            var cssResponse = await router
                .DispatchAsync(new ProtocolRequest { Id = 2, Method = "CSS.getMatchedStylesForNode" })
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(cssResponse.IsSuccess);

            releaseDom.TrySetResult();
            var domResponse = await domDispatch.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(domResponse.IsSuccess);
        }

        [Fact]
        public async Task HandlerException_ReleasesDomainGate()
        {
            var router = new MessageRouter();
            router.RegisterHandler(new FaultingHandler("Runtime"));

            var failed = await router.DispatchAsync(new ProtocolRequest { Id = 1, Method = "Runtime.evaluate" });
            Assert.False(failed.IsSuccess);
            Assert.Equal(-32603, failed.Error.Code);

            // The failed dispatch must have released the domain gate.
            var next = await router.DispatchAsync(new ProtocolRequest { Id = 2, Method = "Runtime.evaluate" })
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(next.IsSuccess);
            Assert.Equal(-32603, next.Error.Code);
        }

        [Fact]
        public async Task DomainGates_AreBoundedByRegisteredDomains()
        {
            var router = new MessageRouter();
            router.RegisterHandler(new StubHandler("DOM"));
            router.RegisterHandler(new StubHandler("CSS"));

            // An unresolvable domain must not allocate a gate at all.
            var unknown = await router.DispatchAsync(new ProtocolRequest { Id = 0, Method = "Nope.method" });
            Assert.False(unknown.IsSuccess);
            Assert.Equal(0, router.DomainGateCount);

            for (var i = 1; i <= 50; i++)
            {
                await router.DispatchAsync(new ProtocolRequest { Id = i, Method = "DOM.getDocument" });
                await router.DispatchAsync(new ProtocolRequest { Id = i, Method = "CSS.getMatchedStylesForNode" });
            }

            // Repeated dispatches reuse the same per-domain gate; nothing leaks per request.
            Assert.Equal(2, router.DomainGateCount);
        }

        private sealed class ScriptedHandler : IProtocolHandler
        {
            private readonly Func<int, Task> _script;

            public ScriptedHandler(string domain, Func<int, Task> script)
            {
                Domain = domain;
                _script = script;
            }

            public string Domain { get; }

            public async Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
            {
                await _script(request.Id);
                return ProtocolResponse.Success(request.Id, new { ok = true });
            }
        }

        private sealed class FaultingHandler : IProtocolHandler
        {
            public FaultingHandler(string domain)
            {
                Domain = domain;
            }

            public string Domain { get; }

            public Task<ProtocolResponse> HandleAsync(string method, ProtocolRequest request)
            {
                throw new System.InvalidOperationException("boom");
            }
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
