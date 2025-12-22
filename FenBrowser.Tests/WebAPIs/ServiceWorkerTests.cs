using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.WebAPIs;
using Xunit;

namespace FenBrowser.Tests.WebAPIs
{
    public class ServiceWorkerTests
    {
        public ServiceWorkerTests()
        {
            ServiceWorkerInterceptor.ClearScopes();
        }

        [Fact]
        public void FetchEvent_ShouldHaveRequestProperty()
        {
            var evt = new FetchEvent("http://example.com/api/data");

            var request = evt.Get("request");
            Assert.True(request.IsObject);

            var url = request.AsObject().Get("url");
            Assert.Equal("http://example.com/api/data", url.ToString());
        }

        [Fact]
        public void FetchEvent_RespondWith_ShouldSetResponse()
        {
            var evt = new FetchEvent("http://example.com/test");

            // Call respondWith
            var respondWith = evt.Get("respondWith").AsFunction();
            var mockResponse = new FenObject();
            mockResponse.Set("status", FenValue.FromNumber(200));

            respondWith.Invoke(new IValue[] { FenValue.FromObject(mockResponse) }, null);

            Assert.True(evt.DefaultPrevented);
            Assert.NotNull(evt.ResponseValue);
        }

        [Fact]
        public void ServiceWorkerGlobalScope_ShouldRegisterFetchListener()
        {
            var registration = new ServiceWorkerRegistration("/sw.js", "/");
            var scope = new ServiceWorkerGlobalScope(registration);

            bool listenerCalled = false;

            // Register fetch listener via self.addEventListener
            var addEventListener = scope.Self.Get("addEventListener").AsFunction();
            addEventListener.Invoke(new IValue[] 
            {
                FenValue.FromString("fetch"),
                FenValue.FromFunction(new FenFunction("handler", (args, thisVal) =>
                {
                    listenerCalled = true;
                    return FenValue.Undefined;
                }))
            }, null);

            // Dispatch a fetch event
            var evt = new FetchEvent("http://example.com/page");
            _ = scope.DispatchFetchEventAsync(evt);

            // Give async a moment
            Task.Delay(50).Wait();
            Assert.True(listenerCalled);
        }

        [Fact]
        public async Task Interceptor_ShouldDispatchToRegisteredScope()
        {
            var registration = new ServiceWorkerRegistration("/sw.js", "/");
            var scope = new ServiceWorkerGlobalScope(registration);

            bool intercepted = false;

            // Register listener that responds
            var addEventListener = scope.Self.Get("addEventListener").AsFunction();
            addEventListener.Invoke(new IValue[]
            {
                FenValue.FromString("fetch"),
                FenValue.FromFunction(new FenFunction("handler", (args, thisVal) =>
                {
                    intercepted = true;
                    if (args.Length > 0 && args[0].IsObject)
                    {
                        var fetchEvt = args[0].AsObject();
                        var respondWith = fetchEvt.Get("respondWith").AsFunction();

                        var response = new FenObject();
                        response.Set("body", FenValue.FromString("Intercepted!"));
                        respondWith.Invoke(new IValue[] { FenValue.FromObject(response) }, null);
                    }
                    return FenValue.Undefined;
                }))
            }, null);

            // Register scope
            ServiceWorkerInterceptor.RegisterScope("/", scope);

            // Intercept a request
            var result = await ServiceWorkerInterceptor.InterceptAsync("http://example.com/data");

            Assert.True(intercepted);
            Assert.NotNull(result);
            Assert.True(result.IsObject);
        }

        [Fact]
        public async Task Interceptor_ShouldReturnNullIfNoHandler()
        {
            // No scopes registered
            ServiceWorkerInterceptor.ClearScopes();

            var result = await ServiceWorkerInterceptor.InterceptAsync("http://example.com/data");

            Assert.Null(result);
        }
    }
}
