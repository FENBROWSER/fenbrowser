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
    public class FetchApiTests
    {
        private readonly IExecutionContext _context;
        private Func<HttpRequestMessage, Task<HttpResponseMessage>> _mockHandler;

        public FetchApiTests()
        {
             // Setup minimal context
             var perm = new PermissionManager(JsPermissions.StandardWeb);
             _context = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
             var env = new FenEnvironment();
             _context.Environment = env;
             
             // Default handler
             _mockHandler = req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

             // Register API with the mock handler delegate
             FetchApi.Register(_context, req => _mockHandler(req));
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
             // Setup mock behavior
             _mockHandler = async req => 
             {
                 await Task.Delay(10); // Simulate network latency
                 return new HttpResponseMessage(HttpStatusCode.OK) 
                 { 
                     Content = new StringContent("Hello Fetch") 
                 };
             };
             
             var fetch = _context.Environment.Get("fetch").AsFunction();
             var promise = fetch.Invoke(new IValue[] { FenValue.FromString("http://test.com") }, _context);
             
             // Verify resolution using .then()
             var tcs = new TaskCompletionSource<IValue>();
             var onFulfilled = new FenFunction("onFulfilled", (args, thisVal) => 
             {
                 tcs.SetResult(args[0]);
                 return FenValue.Undefined;
             });
             var onRejected = new FenFunction("onRejected", (args, thisVal) => 
             {
                 tcs.SetException(new Exception("Promise rejected: " + (args.Length > 0 ? args[0].ToString() : "unknown")));
                 return FenValue.Undefined;
             });

             // promise.then(onFulfilled, onRejected)
             promise.AsObject().Get("then").AsFunction().Invoke(
                 new IValue[] { FenValue.FromFunction(onFulfilled), FenValue.FromFunction(onRejected) },
                 _context
             );

             // Wait for tcs (with timeout)
             if (await Task.WhenAny(tcs.Task, Task.Delay(5000)) != tcs.Task)
             {
                 throw new TimeoutException("Fetch promise timed out");
             }

             var result = await tcs.Task;
             Assert.NotNull(result);
             Assert.True(result.IsObject); // Response object
             
             var response = result.AsObject();
             Assert.True(response.Has("text"));
             Assert.True(response.Has("ok"));
             Assert.True(response.Get("ok").ToBoolean()); 
             Assert.Equal(200, response.Get("status").ToNumber());
        }

        [Fact]
        public async Task Response_Text_ShouldReturnContent()
        {
             // Setup mock behavior
             _mockHandler = req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) 
             { 
                 Content = new StringContent("API Data") 
             });

             var fetch = _context.Environment.Get("fetch").AsFunction();
             var promise = fetch.Invoke(new IValue[] { FenValue.FromString("http://api.com") }, _context);
             
             // Chain fetch().then(res => res.text()).then(txt => ...)
             var tcs = new TaskCompletionSource<string>();
             
             var onFetchFulfilled = new FenFunction("onFetchFulfilled", (args, thisVal) => 
             {
                 var resp = args[0].AsObject();
                 var textFunc = resp.Get("text").AsFunction();
                 // Return the promise from text()
                 // But wait, FenFunction returns IValue. If we return a Promise here, the chaining should wait for it if properly implemented.
                 // However, we are manually hooking 'then'.
                 // Let's just invoke text().then(final) inside here.
                 
                 var textPromise = textFunc.Invoke(new IValue[]{}, _context);
                 
                 var onTextFulfilled = new FenFunction("onTextFulfilled", (tArgs, tThis) => {
                     tcs.SetResult(tArgs[0].ToString());
                     return FenValue.Undefined;
                 });
                 
                 textPromise.AsObject().Get("then").AsFunction().Invoke(
                     new IValue[] { FenValue.FromFunction(onTextFulfilled) }, _context
                 );
                 
                 return FenValue.Undefined;
             });

             promise.AsObject().Get("then").AsFunction().Invoke(
                 new IValue[] { FenValue.FromFunction(onFetchFulfilled) }, _context
             );
             
             if (await Task.WhenAny(tcs.Task, Task.Delay(5000)) != tcs.Task)
             {
                 throw new TimeoutException("Text promise timed out");
             }
             
             Assert.Equal("API Data", await tcs.Task);
        }
    }
}
