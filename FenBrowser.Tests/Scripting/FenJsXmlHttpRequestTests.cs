using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    public sealed class FenJsXmlHttpRequestTests
    {
        [Fact]
        public async Task ObjectLiteral_HexNumericKey_IsVisibleThroughNumericIndex()
        {
            var baseUri = new Uri("https://www.amazon.in/");
            var document = new HtmlParser(
                """
                <html><body><script>
                var modules = { 0x1c6: function () { return 'module-ok'; } };
                globalThis.__hexModuleResult = modules[454]();
                </script></body></html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("module-ok", engine.Evaluate("globalThis.__hexModuleResult")?.ToString());
        }

        [Fact]
        public async Task PromiseThen_RunsInHostedBrowserEngine()
        {
            var baseUri = new Uri("https://www.amazon.in/");
            var document = new HtmlParser(
                """
                <html><body><script>
                Promise.resolve('token-ready').then(function (value) {
                    globalThis.__promiseThenRan = true;
                    globalThis.__promiseThenValue = value;
                });
                </script></body></html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__promiseThenRan"));
            Assert.Equal("token-ready", engine.Evaluate("globalThis.__promiseThenValue")?.ToString());
        }

        [Fact]
        public async Task PromiseConstructorThen_RunsInHostedBrowserEngine()
        {
            var baseUri = new Uri("https://www.amazon.in/");
            var document = new HtmlParser(
                """
                <html><body><script>
                function checkForceRefresh() {
                    return new Promise(function (resolve) {
                        resolve(false);
                    });
                }
                checkForceRefresh().then(function (value) {
                    globalThis.__constructorThenRan = true;
                    globalThis.__constructorThenValue = value;
                });
                </script></body></html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__constructorThenRan"));
            Assert.Equal("false", engine.Evaluate("String(globalThis.__constructorThenValue)")?.ToString());
        }

        [Fact]
        public async Task MissingHostProperty_RecordsUnsupportedJsCapability()
        {
            var baseUri = new Uri("https://www.amazon.in/");
            var document = new HtmlParser(
                """
                <html><body><div id="app">ok</div></body></html>
                """,
                baseUri).Parse();

            EngineCapabilities.Reset();
            try
            {
                var engine = new FenJsBrowserScriptEngine(CreateHost())
                {
                    Sandbox = SandboxPolicy.AllowAll
                };

                await engine.SetDomAsync(document.DocumentElement, baseUri);

                Assert.Equal("undefined", engine.Evaluate("typeof document.fenMissingApiProbe")?.ToString());
                var record = Assert.Single(
                    EngineCapabilities.GetUnsupportedJsSnapshot(),
                    feature => feature.Name == "Document.fenMissingApiProbe");
                Assert.Equal("missing host property", record.Reason);
                Assert.Equal(1, record.EncounterCount);
            }
            finally
            {
                EngineCapabilities.Reset();
            }
        }

        [Fact]
        public async Task MissingGlobalReference_RecordsUnsupportedJsCapability()
        {
            var baseUri = new Uri("https://www.amazon.in/");
            var document = new HtmlParser(
                "<html>\n<body>\n<script>FenMissingGlobalProbe();</script>\n</body></html>",
                baseUri).Parse();

            EngineCapabilities.Reset();
            try
            {
                var engine = new FenJsBrowserScriptEngine(CreateHost())
                {
                    Sandbox = SandboxPolicy.AllowAll
                };

                await engine.SetDomAsync(document.DocumentElement, baseUri);

                var snapshot = engine.GetScriptLoadingSnapshot();
                Assert.Equal(1, snapshot.ExecutionFailed);
                var script = Assert.Single(snapshot.Scripts);
                Assert.Equal("script-1", script.ScriptId);
                Assert.Equal("inline#1", script.SourceLabel);
                Assert.Equal(14, script.SourceOffset);
                Assert.Equal(3, script.SourceLine);
                Assert.Equal(1, script.SourceColumn);
                var record = Assert.Single(
                    EngineCapabilities.GetUnsupportedJsSnapshot(),
                    feature => feature.Name == "globalThis.FenMissingGlobalProbe");
                Assert.Equal("missing global reference", record.Reason);
                Assert.Equal(1, record.EncounterCount);
            }
            finally
            {
                EngineCapabilities.Reset();
            }
        }

        [Fact]
        public async Task IndirectEval_CreatesGlobalBindingsInHostedBrowserEngine()
        {
            var baseUri = new Uri("https://www.google.com/search?q=test");
            var document = new HtmlParser(
                """
                <html><body><script>
                (0, eval)("var __fenIndirectEvalVar = 42; function __fenIndirectEvalFn(){ return 'fn-ok'; } globalThis.__fenIndirectEvalThis = this === globalThis;");
                </script></body></html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("42", engine.Evaluate("String(globalThis.__fenIndirectEvalVar)")?.ToString());
            Assert.Equal("fn-ok", engine.Evaluate("globalThis.__fenIndirectEvalFn()")?.ToString());
            Assert.Equal(true, engine.Evaluate("globalThis.__fenIndirectEvalThis"));
        }

        [Fact]
        public async Task ArrowPromiseChain_RunsAmazonWafBodyShape()
        {
            var baseUri = new Uri("https://www.amazon.in/");
            var document = new HtmlParser(
                """
                <html><body><script>
                var AwsWafIntegration = {
                    checkForceRefresh: function () { return Promise.resolve(false); },
                    forceRefreshToken: function () {
                        globalThis.__forceRefreshCalled = true;
                        return Promise.resolve();
                    },
                    getToken: function () {
                        globalThis.__getTokenCalled = true;
                        return Promise.resolve();
                    }
                };
                AwsWafIntegration.checkForceRefresh().then((forceRefresh) => {
                    if (forceRefresh) {
                        AwsWafIntegration.forceRefreshToken().then(() => {
                            globalThis.__reloadRequested = true;
                        });
                    } else {
                        AwsWafIntegration.getToken().then(() => {
                            globalThis.__reloadRequested = true;
                        });
                    }
                });
                </script></body></html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__getTokenCalled"));
            Assert.Equal(true, engine.Evaluate("globalThis.__reloadRequested"));
            Assert.Null(engine.Evaluate("globalThis.__forceRefreshCalled"));
        }

        [Fact]
        public async Task HostedBrowserEngine_AllowsDeepGeneratedCallChainsOnLargeStack()
        {
            var baseUri = new Uri("https://www.amazon.in/");
            var document = new HtmlParser(
                """
                <html><body><script>
                function depth(n) {
                    if (n === 0) return 'ok';
                    return depth(n - 1);
                }
                globalThis.__deepCallResult = depth(80);
                </script></body></html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("ok", engine.Evaluate("globalThis.__deepCallResult")?.ToString());
        }

        [Fact]
        public async Task CryptoGetRandomValues_FillsUint8Array()
        {
            var baseUri = new Uri("https://www.amazon.in/");
            var document = new HtmlParser(
                """
                <html><body><script>
                var bytes = new Uint8Array(16);
                var returned = crypto.getRandomValues(bytes);
                globalThis.__cryptoReturnedSameArray = returned === bytes;
                globalThis.__cryptoLength = bytes.length;
                globalThis.__cryptoFirstByteType = typeof bytes[0];
                </script></body></html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__cryptoReturnedSameArray"));
            Assert.Equal("16", engine.Evaluate("String(globalThis.__cryptoLength)")?.ToString());
            Assert.Equal("number", engine.Evaluate("globalThis.__cryptoFirstByteType")?.ToString());
        }

        [Fact]
        public async Task Fetch_PostsFormDataAndExposesJsonResponseHeaders()
        {
            var baseUri = new Uri("https://www.amazon.in/");
            var document = new HtmlParser(
                """
                <html><body><script>
                var body = new FormData();
                body.append('payload', JSON.stringify({ proof: 42 }));
                fetch('/_sec/challenge', { method: 'POST', body: body }).then(function (response) {
                    globalThis.__fetchOk = response.ok;
                    globalThis.__fetchStatus = response.status;
                    globalThis.__fetchAction = response.headers.get('x-amzn-waf-action');
                    return response.json();
                }).then(function (json) {
                    globalThis.__fetchToken = json.token;
                });
                </script></body></html>
                """,
                baseUri).Parse();
            HttpRequestMessage capturedRequest = null;
            string capturedBody = null;

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                FetchHandler = async request =>
                {
                    capturedRequest = request;
                    capturedBody = request.Content == null
                        ? string.Empty
                        : await request.Content.ReadAsStringAsync();
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        ReasonPhrase = "OK",
                        Content = new StringContent("{\"token\":\"abc123\"}", Encoding.UTF8, "application/json")
                    };
                    response.Headers.TryAddWithoutValidation("x-amzn-waf-action", "ok");
                    return response;
                }
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__fetchOk"));
            Assert.Equal("200", engine.Evaluate("String(globalThis.__fetchStatus)")?.ToString());
            Assert.Equal("ok", engine.Evaluate("globalThis.__fetchAction")?.ToString());
            Assert.Equal("abc123", engine.Evaluate("globalThis.__fetchToken")?.ToString());
            Assert.NotNull(capturedRequest);
            Assert.Equal(HttpMethod.Post, capturedRequest.Method);
            Assert.Equal("https://www.amazon.in/_sec/challenge", capturedRequest.RequestUri?.ToString());
            Assert.Equal("multipart/form-data", capturedRequest.Content?.Headers.ContentType?.MediaType);
            Assert.Contains("Content-Disposition: form-data; name=\"payload\"", capturedBody);
            Assert.Contains("\"proof\":42", capturedBody);
        }

        [Fact]
        public async Task Send_RoutesSynchronousPostThroughFetchHandlerAndDispatchesLoadEnd()
        {
            var baseUri = new Uri("https://www.amazon.in/");
            var document = new HtmlParser(
                """
                <html><body><script>
                try {
                    var req = new XMLHttpRequest();
                    req.addEventListener('loadend', function () {
                        globalThis.__xhrLoadEnd = true;
                        globalThis.__xhrStatus = req.status;
                        globalThis.__xhrLocation = JSON.parse(req.responseText).location;
                    });
                    req.open('POST', '/_sec/verify?provider=interstitial', false);
                    req.setRequestHeader('Content-Type', 'application/json');
                    req.send(JSON.stringify({ "pow": 42 }));
                    globalThis.__xhrSendStatus = 'ok';
                } catch (e) {
                    globalThis.__xhrSendStatus = String(e && e.message ? e.message : e);
                }
                </script></body></html>
                """,
                baseUri).Parse();
            HttpRequestMessage capturedRequest = null;
            string capturedBody = null;

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                FetchHandler = async request =>
                {
                    capturedRequest = request;
                    capturedBody = request.Content == null
                        ? string.Empty
                        : await request.Content.ReadAsStringAsync();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        ReasonPhrase = "OK",
                        Content = new StringContent("{\"location\":\"/home\"}", Encoding.UTF8, "application/json")
                    };
                }
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("ok", engine.Evaluate("globalThis.__xhrSendStatus")?.ToString());
            Assert.Equal(true, engine.Evaluate("globalThis.__xhrLoadEnd"));
            Assert.Equal("200", engine.Evaluate("String(globalThis.__xhrStatus)")?.ToString());
            Assert.Equal("/home", engine.Evaluate("globalThis.__xhrLocation")?.ToString());
            Assert.NotNull(capturedRequest);
            Assert.Equal(HttpMethod.Post, capturedRequest.Method);
            Assert.Equal("https://www.amazon.in/_sec/verify?provider=interstitial", capturedRequest.RequestUri?.ToString());
            Assert.Equal("application/json", capturedRequest.Content?.Headers.ContentType?.MediaType);
            Assert.Contains("\"pow\":42", capturedBody);
        }

        [Fact]
        public async Task TimerAndRafCallbacks_DoNotOverwriteLargeStackWorkerDispatch()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                """
                <html><body><script>
                setTimeout(function () {
                    globalThis.__timeoutCount = (globalThis.__timeoutCount || 0) + 1;
                }, 20);
                requestAnimationFrame(function () {
                    globalThis.__rafCount = (globalThis.__rafCount || 0) + 1;
                });
                </script></body></html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            await Task.Delay(150);

            var eventLoop = engine.GetEventLoopSnapshot();

            Assert.Equal("1", engine.Evaluate("String(globalThis.__timeoutCount || 0)")?.ToString());
            Assert.Equal("1", engine.Evaluate("String(globalThis.__rafCount || 0)")?.ToString());
            Assert.Equal(1, eventLoop.TimersExecuted);
            Assert.Equal(1, eventLoop.AnimationFramesExecuted);
            Assert.Equal(1, eventLoop.Events.Count(e =>
                e.EventName == "CallbackCompleted" &&
                e.Detail == "setTimeout"));
            Assert.Equal(1, eventLoop.Events.Count(e =>
                e.EventName == "CallbackCompleted" &&
                e.Detail == "requestAnimationFrame"));
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
