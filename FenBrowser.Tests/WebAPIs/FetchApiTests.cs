using System;
using System.Net.Http;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using FenBrowser.FenEngine.WebAPIs;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using System.Collections.Generic;

namespace FenBrowser.Tests.WebAPIs
{
    // Minimal mock for HttpClientHandler to intercept requests
    public class MockHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Handler { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Handler(request);
        }
    }

    public class FetchApiTests
    {
        private readonly IExecutionContext _context;

        public FetchApiTests()
        {
             // Setup minimal context
             var perm = new PermissionManager(JsPermissions.StandardWeb);
             _context = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
             var env = new FenEnvironment();
             _context.Environment = env;
             
             // Register API
             FetchApi.Register(_context);
        }

        [Fact]
        public void Fetch_ShouldReturnThenable()
        {
            var fetch = _context.Environment.Get("fetch").AsFunction();
            var promise = fetch.Invoke(new IValue[] { FenValue.FromString("http://example.com") }, _context);

            Assert.NotNull(promise);
            Assert.True(promise.IsObject);
            Assert.True(promise.AsObject().Has("then"));
        }

        [Fact]
        public async Task Fetch_ShouldResolveWithResponse()
        {
             // Setup mock
             var handler = new MockHttpHandler();
             handler.Handler = async req => 
             {
                 await Task.Delay(10); // Simulate network latency
                 return new HttpResponseMessage(HttpStatusCode.OK) 
                 { 
                     Content = new StringContent("Hello Fetch") 
                 };
             };
             
             FetchApi.ClientFactory = () => new HttpClient(handler);

             var fetch = _context.Environment.Get("fetch").AsFunction();
             var promise = fetch.Invoke(new IValue[] { FenValue.FromString("http://test.com") }, _context);
             
             // Wait for async task to complete (simple poll for verification)
             var pObj = promise.AsObject();
             for (int i=0; i<100; i++)
             {
                 if (pObj.Has("__state")) break;
                 await Task.Delay(10);
             }

             Assert.Equal("fulfilled", pObj.Get("__state")?.ToString());
             var result = pObj.Get("__result");
             Assert.NotNull(result);
             Assert.True(result.IsObject); // Response object
             
             var response = result.AsObject();
             Assert.True(response.Has("text"));
             Assert.True(response.Has("ok"));
             Assert.Equal(true, response.Get("ok").ToBoolean());
             Assert.Equal(200, response.Get("status").ToNumber());
        }

        [Fact]
        public async Task Response_Text_ShouldReturnContent()
        {
             // Setup mock
             var handler = new MockHttpHandler();
             handler.Handler = req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) 
             { 
                 Content = new StringContent("API Data") 
             });
             FetchApi.ClientFactory = () => new HttpClient(handler);

             var fetch = _context.Environment.Get("fetch").AsFunction();
             var promise = fetch.Invoke(new IValue[] { FenValue.FromString("http://api.com") }, _context);
             
             // Wait for fetch
             while (!promise.AsObject().Has("__state")) await Task.Delay(10);

             var response = promise.AsObject().Get("__result").AsObject();
             var textFunc = response.Get("text").AsFunction();
             var textPromise = textFunc.Invoke(new IValue[]{}, _context).AsObject();

             // Wait for text()
             while (!textPromise.Has("__state")) await Task.Delay(10);
             
             Assert.Equal("fulfilled", textPromise.Get("__state")?.ToString());
             Assert.Equal("API Data", textPromise.Get("__result")?.ToString());
        }
    }
}
