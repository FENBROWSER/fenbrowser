using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Types;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public sealed class ProductionHardeningBatch3Tests : IDisposable
    {
        public ProductionHardeningBatch3Tests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        public void Dispose()
        {
            EventLoopCoordinator.Instance.Clear();
            EngineContext.Reset();
        }

        [Fact]
        public void FenRuntime_Source_Removes_Legacy_Promise_And_Fetch_Helpers()
        {
            var source = File.ReadAllText(GetFenRuntimeSourcePath());

            Assert.DoesNotContain("private FenObject CreatePromiseConstructor()", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CreateExecutorPromise(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CreateRejectedPromiseValue(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CreateResolvedPromise(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CreateFetchPromise(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CreateRejectedPromise(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("private IValue CreatePromise(Action<Action<IValue>, Action<IValue>> executor)", source, StringComparison.Ordinal);
            Assert.Contains("CreatePromiseCapability()", source, StringComparison.Ordinal);
            Assert.Contains("FetchApi.Register(_context, request => SendNetworkRequestAsync(request));", source, StringComparison.Ordinal);
        }

        [Fact]
        public void Promise_WithResolvers_Uses_JsPromise_Capability()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple("""
                var capability = Promise.withResolvers();
                var promiseIsPromise = capability.promise instanceof Promise;
                var settledValue = -1;

                capability.promise.then(function(value) {
                  settledValue = value;
                });

                capability.resolve(123);
                """);

            EventLoopCoordinator.Instance.RunUntilEmpty();

            var capability = runtime.GetGlobal("capability").AsObject();
            Assert.NotNull(capability);
            Assert.IsType<JsPromise>(capability.Get("promise").AsObject());
            Assert.True(runtime.GetGlobal("promiseIsPromise").ToBoolean());
            Assert.Equal(123.0, runtime.GetGlobal("settledValue").ToNumber());
        }

        [Fact]
        public async Task CryptoSubtle_Digest_Returns_JsPromise_ArrayBuffer()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple("""
                var digestPromise = crypto.subtle.digest("SHA-256", "abc");
                var digestLength = -1;

                digestPromise.then(function(buffer) {
                  digestLength = buffer.byteLength;
                });
                """);

            var promise = Assert.IsType<JsPromise>(runtime.GetGlobal("digestPromise").AsObject());
            await PumpUntilAsync(runtime, () => runtime.GetGlobal("digestLength").ToNumber() > 0);

            Assert.True(promise.IsFulfilled);
            var buffer = Assert.IsType<JsArrayBuffer>(promise.Result.AsObject());
            Assert.Equal(32, buffer.Data.Length);
            Assert.Equal(32.0, runtime.GetGlobal("digestLength").ToNumber());
        }

        [Fact]
        public async Task RuntimeFetch_Uses_Canonical_JsPromise_Path()
        {
            var runtime = new FenRuntime();
            string requestedUrl = string.Empty;
            runtime.NetworkFetchHandler = request =>
            {
                requestedUrl = request.RequestUri?.ToString() ?? string.Empty;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("runtime-fetch")
                });
            };

            runtime.ExecuteSimple("""
                var fetchPromise = fetch("https://example.com/data");
                var fetchStatus = -1;
                var fetchText = "";
                var fetchError = "";

                fetchPromise
                  .then(function(response) {
                    fetchStatus = response.status;
                    return response.text();
                  })
                  .then(function(text) {
                    fetchText = text;
                  })
                  .catch(function(error) {
                    fetchError = String(error);
                  });
                """);

            Assert.IsType<JsPromise>(runtime.GetGlobal("fetchPromise").AsObject());
            await PumpUntilAsync(runtime, () =>
                runtime.GetGlobal("fetchText").ToString() == "runtime-fetch" ||
                !string.IsNullOrEmpty(runtime.GetGlobal("fetchError").ToString()));

            Assert.Equal("https://example.com/data", requestedUrl);
            Assert.Equal(200.0, runtime.GetGlobal("fetchStatus").ToNumber());
            Assert.Equal("runtime-fetch", runtime.GetGlobal("fetchText").ToString());
            Assert.Equal(string.Empty, runtime.GetGlobal("fetchError").ToString());
        }

        private static async Task PumpUntilAsync(FenRuntime runtime, Func<bool> predicate, int timeoutMs = 5000)
        {
            var timeout = Stopwatch.StartNew();
            while (!predicate())
            {
                if (timeout.ElapsedMilliseconds > timeoutMs)
                    throw new TimeoutException("Timed out waiting for promise settlement.");

                EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
                await Task.Delay(10);
            }

            EventLoopCoordinator.Instance.RunUntilEmpty();
        }

        private static string GetFenRuntimeSourcePath()
        {
            return Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "FenBrowser.FenEngine",
                "Core",
                "FenRuntime.cs"));
        }
    }
}
