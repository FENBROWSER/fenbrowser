using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class JavaScriptEngineModuleLoadingTests
    {
        [Fact]
        public async Task SetDom_DeprecatedSyncWrapper_DoesNotBlockOnAsyncModuleFetch()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser("<html><body><script type=\"module\" src=\"main.js\"></script></body></html>", baseUri);
            var doc = parser.Parse();
            var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var engine = new JavaScriptEngine(CreateHost())
            {
                FetchOverride = _ => gate.Task
            };

            var sw = Stopwatch.StartNew();
            engine.SetDom(doc.DocumentElement, baseUri);
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250), $"Expected non-blocking SetDom wrapper. Elapsed={sw.ElapsedMilliseconds}ms");

            gate.TrySetResult("globalThis.__bridgeProbe = 1;");
            await Task.Delay(25);
        }

        [Fact]
        public async Task SetDom_DeprecatedSyncWrapper_DoesNotThrowOnAsyncFetchFailure()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser("<html><body><script type=\"module\" src=\"main.js\"></script></body></html>", baseUri);
            var doc = parser.Parse();
            var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var engine = new JavaScriptEngine(CreateHost())
            {
                FetchOverride = _ => gate.Task
            };

            var sw = Stopwatch.StartNew();
            engine.SetDom(doc.DocumentElement, baseUri);
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250), $"Expected non-blocking SetDom wrapper. Elapsed={sw.ElapsedMilliseconds}ms");

            gate.TrySetException(new InvalidOperationException("module-fetch-failed"));
            await Task.Delay(25);
        }

        [Fact]
        public async Task SetDomAsync_ModuleGraphPrefetch_LoadsStaticDependenciesWithoutSyncBridge()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser("<html><body><script type=\"module\" src=\"main.js\"></script></body></html>", baseUri);
            var doc = parser.Parse();
            var fetchCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var engine = new JavaScriptEngine(CreateHost())
            {
                FetchOverride = uri =>
                {
                    var key = uri.AbsoluteUri;
                    fetchCount[key] = fetchCount.TryGetValue(key, out var count) ? count + 1 : 1;

                    if (key.EndsWith("/main.js", StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult("import './dep.js'; globalThis.__moduleValue = (globalThis.__depValue || 0) + 1;");
                    }

                    if (key.EndsWith("/dep.js", StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.FromResult("globalThis.__depValue = 41;");
                    }

                    return Task.FromResult(string.Empty);
                }
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            var result = engine.Evaluate("globalThis.__moduleValue");
            Assert.NotNull(result);
            Assert.Equal(42d, Convert.ToDouble(result, CultureInfo.InvariantCulture));
            Assert.True(fetchCount.ContainsKey("https://example.com/main.js"));
            Assert.True(fetchCount.ContainsKey("https://example.com/dep.js"));
            Assert.Equal(1, fetchCount["https://example.com/main.js"]);
            Assert.Equal(1, fetchCount["https://example.com/dep.js"]);
        }

        private static JsHostAdapter CreateHost()
        {
            return new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: _ => { });
        }
    }
}
