using Xunit;
using FenBrowser.FenEngine.WebAPIs;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using System.Net.Http;
using System.Threading.Tasks;
using System;
using FenBrowser.FenEngine.Core.Types;
using FenBrowser.FenEngine.Security;

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

            Assert.Equal("https://example.com/", request.Url);
            Assert.Equal("POST", request.Method);
            Assert.Equal("test-body", request.Body);
            Assert.Equal("application/json", request.Headers.GetHeader("content-type"));
        }

        [Fact]
        public void FetchApi_Register_PreservesUrlConstructorAndAddsBlobStatics()
        {
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(new PermissionManager(JsPermissions.StandardWeb))
            {
                Environment = new FenEnvironment()
            };
            var runtime = new FenRuntime(context);

            FetchApi.Register(runtime.Context, _ => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));

            var url = runtime.GlobalEnv.Get("URL");
            Assert.True(url.IsFunction);
            Assert.True(url.AsObject().Has("createObjectURL"));
            Assert.True(url.AsObject().Has("revokeObjectURL"));

            var blob = runtime.GlobalEnv.Get("Blob");
            Assert.True(blob.IsFunction);
        }

        [Fact]
        public void JsRequest_Constructor_ResolvesRelativeUrlAgainstExecutionContext_AndPreservesPatchCasing()
        {
            var context = new FenBrowser.FenEngine.Core.ExecutionContext();
            context.CurrentUrl = new Uri(@"file:///C:/wpt/fetch/api/request/url-encoding.html").AbsoluteUri;

            var options = new FenObject();
            options.Set("method", FenValue.FromString("patch"));

            var request = new JsRequest("?\u00DF", FenValue.FromObject(options), context);

            Assert.Equal(new Uri(new Uri(context.CurrentUrl), "?\u00DF").AbsoluteUri, request.Url);
            Assert.Equal("patch", request.Method);
        }

        [Fact]
        public void JsResponse_Redirect_ResolvesRelativeLocationAgainstExecutionContext()
        {
            var context = new FenBrowser.FenEngine.Core.ExecutionContext();
            context.CurrentUrl = new Uri(@"file:///C:/wpt/fetch/api/basic/url-parsing.sub.html").AbsoluteUri;

            var redirected = JsResponse.Redirect(new[] { FenValue.FromString("?\u00FF") }, context);
            var response = Assert.IsType<JsResponse>(redirected.AsObject());
            var headers = response.Get("headers").AsObject();
            var location = headers.Get("get").AsFunction().Invoke(new[] { FenValue.FromString("location") }, context);

            Assert.Equal(new Uri(new Uri(context.CurrentUrl), "?\u00FF").AbsoluteUri, location.ToString());
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
