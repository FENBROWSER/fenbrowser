using Xunit;
using FenBrowser.FenEngine.WebAPIs;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using System.Net.Http;
using System.Threading.Tasks;
using System;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.Tests.WebAPIs
{
    public class FetchHardeningTests
    {
        [Fact]
        public void JsRequest_Constructor_ParsesOptions()
        {
            var context = new FenBrowser.FenEngine.Core.ExecutionContext();
            var options = new FenObject();
            options.Set("method", FenValue.FromString("POST"));
            options.Set("body", FenValue.FromString("test-body"));
            
            var headers = new FenObject();
            headers.Set("Content-Type", FenValue.FromString("application/json"));
            options.Set("headers", FenValue.FromObject(headers));

            var request = new JsRequest("https://example.com", FenValue.FromObject(options));

            Assert.Equal("https://example.com", request.Url);
            Assert.Equal("POST", request.Method);
            Assert.Equal("test-body", request.Body);
            Assert.Equal("application/json", request.Headers.GetHeader("content-type"));
        }

        [Fact]
        public async Task JsResponse_Json_ParsesCorrectly()
        {
            var context = new FenBrowser.FenEngine.Core.ExecutionContext();
            var httpResponse = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"key\": \"value\", \"num\": 123}")
            };
            
            var jsResponse = new JsResponse(httpResponse, context);
            var jsonPromiseValue = jsResponse.Get("json", context);
            Assert.True(jsonPromiseValue.IsFunction);
            
            var promise = jsonPromiseValue.AsFunction().Invoke(new FenValue[0], null).AsObject() as JsPromise;
            Assert.NotNull(promise);

            IValue result = null;
            var resolve = new FenFunction("resolve", (args, thisVal) => {
                result = args[0];
                return FenValue.Undefined;
            });
            
            // We need to wait for the task to complete
            promise.Then(FenValue.FromFunction(resolve), FenValue.Undefined);
            
            // Poll for result (simulating event loop)
            int retries = 0;
            while (result == null && retries < 100)
            {
                await Task.Delay(10);
                retries++;
            }

            Assert.NotNull(result);
            Assert.True(result.IsObject);
            var obj = result.AsObject();
            Assert.Equal("value", obj.Get("key").ToString());
            Assert.Equal(123.0, obj.Get("num").ToNumber());
        }
    }
}
