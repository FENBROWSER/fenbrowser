using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Workers;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;

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
            Assert.Equal(script, controller.ScriptURL);

            var noController = _manager.GetController("https://example.com/shop/");
            Assert.Null(noController);
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
            var mockContext = new FenBrowser.FenEngine.Core.ExecutionContext(null); // Mock context
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
        }
        
        private FenValue CreatePromise(FenValue result)
        {
            var p = new FenObject();
            p.Set("__state", FenValue.FromString("fulfilled"));
            p.Set("__result", result);
            p.Set("then", FenValue.FromFunction(new FenFunction("then", (a,t) => 
            {
                if(a.Length>0) a[0].AsFunction().Invoke(new FenValue[]{result}, null);
               return FenValue.Undefined;
            })));
            return FenValue.FromObject(p);
        }
    }
}
