using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Storage;
using FenBrowser.FenEngine.Workers;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;
using FenExecutionContext = FenBrowser.FenEngine.Core.ExecutionContext;

namespace FenBrowser.Tests.Workers
{
    public class ServiceWorkerLifecycleTests
    {
        private readonly ServiceWorkerManager _manager;

        public ServiceWorkerLifecycleTests()
        {
            _manager = ServiceWorkerManager.Instance;
            // Reset manager state if possible? 
            // Singleton makes this hard, but for now we use unique scopes.
        }

        [Fact]
        public async Task Register_CreatesRegistrationAndInstalls()
        {
            var scope = "https://example.com/pwa/";
            var script = "sw.js";

            var reg = await _manager.Register(script, scope);

            Assert.NotNull(reg);
            Assert.Equal(scope, reg.Scope);
            
            // Should transition to active (mock logic auto-activates)
            Assert.NotNull(reg.Get("active").AsObject() as ServiceWorker);
            Assert.Equal("activated", ((ServiceWorker)reg.Get("active").AsObject()).State);
        }

        [Fact]
        public async Task Registration_GetController_ReturnsActiveWorker()
        {
            var scope = "https://example.com/blog/";
            var script = "sw-blog.js";
            await _manager.Register(script, scope);

            var controller = _manager.GetController("https://example.com/blog/post-1");
            Assert.NotNull(controller);
            Assert.Equal("https://example.com/blog/sw-blog.js", controller.ScriptURL);

            var noController = _manager.GetController("https://example.com/shop/");
            Assert.Null(noController);
        }

        [Fact]
        public async Task GetRegistration_UsesLongestNestedScopeMatch()
        {
            var baseScope = "https://example.com/app/";
            var nestedScope = "https://example.com/app/blog/";

            await _manager.Register("sw-root.js", baseScope);
            await _manager.Register("sw-blog.js", nestedScope);

            var match = _manager.GetRegistration("https://example.com/app/blog/post-1");
            Assert.NotNull(match);
            Assert.Equal(nestedScope, match.Scope);
        }

        [Fact]
        public async Task Unregister_RemovesRegistrationAndController()
        {
            var scope = "https://example.com/unreg/";
            await _manager.Register("sw-unreg.js", scope);

            var controller = _manager.GetController("https://example.com/unreg/data");
            Assert.NotNull(controller);

            var removed = await _manager.UnregisterAsync(scope);
            Assert.True(removed);
            Assert.Null(_manager.GetRegistration(scope));
            Assert.Null(_manager.GetController("https://example.com/unreg/data"));
        }

        [Fact]
        public async Task Fetch_InterceptsAndReturnsResponse()
        {
            // 1. Register SW
            var scope = "https://example.com/api/";
            await _manager.Register("sw-api.js", scope);
            var sw = _manager.GetController("https://example.com/api/data");
            Assert.NotNull(sw);

            // 2. Dispatch Fetch Event
            var mockContext = new FenExecutionContext(null); // Mock context
            var req = new FenObject();
            req.Set("url", FenValue.FromString("https://example.com/api/data"));
            var evt = new FetchEvent("fetch", req, mockContext);

            // Without a script-provided onfetch/respondWith path, dispatch falls back to network.
            
            var handled = await _manager.DispatchFetchEvent(sw, evt);
            Assert.False(handled);
            
            // Ideally we'd test true handling, but that requires full runtime injection which is mocking heavy.
            // We can manually set the respondWith promise on the event to verify the event structure works.
            var response = new FenObject();
            response.Set("status", FenValue.FromNumber(200));
            var promise = CreatePromise(FenValue.FromObject(response));
            var respondWithFunc = evt.Get("respondWith").AsFunction();
            respondWithFunc.Invoke(new FenValue[] { promise }, null);
            
            Assert.NotNull(evt.RespondWithPromise);

            var settled = await evt.WaitForRespondWithSettlementAsync(TimeSpan.FromMilliseconds(200));
            Assert.True(settled.IsFulfilled);
            Assert.Equal(200, settled.Value.AsObject().Get("status").ToNumber());
        }

        [Fact]
        public async Task FetchEvent_WaitsForRespondWithRegistration()
        {
            var mockContext = new FenExecutionContext(null);
            var req = new FenObject();
            req.Set("url", FenValue.FromString("https://example.com/api/data"));
            var evt = new FetchEvent("fetch", req, mockContext);

            var registeredBefore = await evt.WaitForRespondWithRegistrationAsync(TimeSpan.FromMilliseconds(20));
            Assert.False(registeredBefore);

            var response = new FenObject();
            response.Set("status", FenValue.FromNumber(204));
            var promise = CreatePromise(FenValue.FromObject(response));
            evt.Get("respondWith").AsFunction().Invoke(new FenValue[] { promise }, null);

            var registeredAfter = await evt.WaitForRespondWithRegistrationAsync(TimeSpan.FromMilliseconds(20));
            Assert.True(registeredAfter);
        }

        [Fact]
        public void ServiceWorkerContainer_WithoutContext_UsesRealReadyPromise()
        {
            var container = new ServiceWorkerContainer("https://example.com");
            var ready = container.Get("ready");

            Assert.True(ready.IsObject);
            Assert.IsType<JsPromise>(ready.AsObject());
        }

        [Fact]
        public void ServiceWorkerRegistration_WithoutContext_ReturnsRealPromises()
        {
            var registration = new ServiceWorkerRegistration("https://example.com/app/");

            var updateResult = registration.Get("update").AsFunction().Invoke(Array.Empty<FenValue>(), null);
            var unregisterResult = registration.Get("unregister").AsFunction().Invoke(Array.Empty<FenValue>(), null);

            Assert.IsType<JsPromise>(updateResult.AsObject());
            Assert.IsType<JsPromise>(unregisterResult.AsObject());
        }

        [Fact]
        public void ServiceWorkerClients_WithoutContext_ReturnsRealPromises()
        {
            var clients = new ServiceWorkerClients("https://example.com/");

            var claimResult = clients.Get("claim").AsFunction().Invoke(Array.Empty<FenValue>(), null);
            var matchAllResult = clients.Get("matchAll").AsFunction().Invoke(Array.Empty<FenValue>(), null);
            var openWindowResult = clients.Get("openWindow").AsFunction().Invoke(
                new[] { FenValue.FromString("/dashboard") }, null);

            Assert.IsType<JsPromise>(claimResult.AsObject());
            Assert.IsType<JsPromise>(matchAllResult.AsObject());
            Assert.IsType<JsPromise>(openWindowResult.AsObject());
        }

        [Fact]
        public void ServiceWorkerGlobalScope_SkipWaiting_ReturnsRealPromise()
        {
            using var runtime = new WorkerRuntime(
                "https://example.com/sw.js",
                "https://example.com/",
                new InMemoryStorageBackend(),
                scriptFetcher: _ => Task.FromResult(string.Empty),
                isServiceWorker: true);

            var scope = new ServiceWorkerGlobalScope(runtime, "https://example.com/", string.Empty, new InMemoryStorageBackend());
            var result = scope.Get("skipWaiting").AsFunction().Invoke(Array.Empty<FenValue>(), null);

            Assert.IsType<JsPromise>(result.AsObject());

            runtime.Terminate();
        }

        [Fact]
        public async Task FetchEvent_DoesNotTreat_LegacyStateBag_AsSettledPromise()
        {
            var mockContext = new FenExecutionContext(null);
            var req = new FenObject();
            req.Set("url", FenValue.FromString("https://example.com/api/data"));
            var evt = new FetchEvent("fetch", req, mockContext);

            var legacyBag = new FenObject();
            legacyBag.Set("__state", FenValue.FromString("fulfilled"));
            legacyBag.Set("__result", FenValue.FromString("legacy"));

            evt.Get("respondWith").AsFunction().Invoke(new[] { FenValue.FromObject(legacyBag) }, null);
            var settled = await evt.WaitForRespondWithSettlementAsync(TimeSpan.FromMilliseconds(50));

            Assert.False(settled.IsHandled);
        }
        
        private FenValue CreatePromise(FenValue result, IExecutionContext context = null)
        {
            FenValue capturedResolve = FenValue.Undefined;
            var executor = new FenFunction("executor", (args, thisVal) =>
            {
                capturedResolve = args.Length > 0 ? args[0] : FenValue.Undefined;
                return FenValue.Undefined;
            });

            var promise = new JsPromise(FenValue.FromFunction(executor), context);
            _ = Task.Run(async () =>
            {
                await Task.Delay(10);
                capturedResolve.AsFunction().Invoke(new[] { result }, context);
            });

            return FenValue.FromObject(promise);
        }
    }
}
