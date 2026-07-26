using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
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
        public async Task Fetch_PostsUint8ArrayAsRawBytes()
        {
            var baseUri = new Uri("https://www.google.com/");
            var document = new HtmlParser(
                """
                <html><body><script>
                fetch('/recaptcha/enterprise/reload', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/octet-stream' },
                    body: new Uint8Array([10, 24, 65, 55, 75])
                }).then(function (response) {
                    globalThis.__binaryFetchStatus = response.status;
                });
                </script></body></html>
                """,
                baseUri).Parse();
            byte[] capturedBody = null;

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                FetchHandler = async request =>
                {
                    capturedBody = request.Content == null
                        ? Array.Empty<byte>()
                        : await request.Content.ReadAsByteArrayAsync();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        ReasonPhrase = "OK",
                        Content = new StringContent("ok")
                    };
                }
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(new byte[] { 10, 24, 65, 55, 75 }, capturedBody);
            Assert.Equal("200", engine.Evaluate("String(globalThis.__binaryFetchStatus)")?.ToString());
        }

        [Fact]
        public async Task DynamicIframe_ExposesInlineDimensionsBeforeAsyncLayout()
        {
            var baseUri = new Uri("https://www.google.com/");
            var document = new HtmlParser(
                """
                <html><body><script>
                var container = document.createElement('div');
                var frame = document.createElement('iframe');
                frame.style.width = '300px';
                frame.style.height = '480px';
                container.appendChild(frame);
                document.body.appendChild(container);
                globalThis.__frameComputedWidth = getComputedStyle(frame).width;
                globalThis.__frameOffsetWidth = frame.offsetWidth;
                globalThis.__frameRectHeight = frame.getBoundingClientRect().height;
                globalThis.__frameInnerWidth = frame.contentWindow.innerWidth;
                globalThis.__frameInnerHeight = frame.contentWindow.innerHeight;
                globalThis.__containerClientWidth = container.clientWidth;
                globalThis.__containerClientHeight = container.clientHeight;
                globalThis.__rootClientHeight = document.documentElement.clientHeight;
                </script></body></html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                WindowWidth = 1280,
                WindowHeight = 800,
                LayoutBoxResolver = _ => new BoxModel
                {
                    BorderBox = new SKRect(0, 0, 300, 78),
                    PaddingBox = new SKRect(0, 0, 300, 78),
                    ContentBox = new SKRect(0, 0, 300, 78)
                }
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("300px", engine.Evaluate("globalThis.__frameComputedWidth")?.ToString());
            Assert.Equal("300", engine.Evaluate("String(globalThis.__frameOffsetWidth)")?.ToString());
            Assert.Equal("480", engine.Evaluate("String(globalThis.__frameRectHeight)")?.ToString());
            Assert.Equal("300", engine.Evaluate("String(globalThis.__frameInnerWidth)")?.ToString());
            Assert.Equal("480", engine.Evaluate("String(globalThis.__frameInnerHeight)")?.ToString());
            Assert.Equal("300", engine.Evaluate("String(globalThis.__containerClientWidth)")?.ToString());
            Assert.Equal("480", engine.Evaluate("String(globalThis.__containerClientHeight)")?.ToString());
            Assert.Equal("800", engine.Evaluate("String(globalThis.__rootClientHeight)")?.ToString());
        }

        [Fact]
        public async Task ReadableStream_GlobalProvidesQueuedDefaultReader()
        {
            var baseUri = new Uri("https://x.com/");
            var document = new HtmlParser(
                """
                <html><body><script>
                try {
                    globalThis.__streamType = typeof ReadableStream;
                    var stream = new ReadableStream({
                        start: function (controller) {
                            controller.enqueue('chunk');
                            controller.close();
                        }
                    });
                    globalThis.__streamCtor = stream instanceof ReadableStream;
                    globalThis.__streamLockedBefore = stream.locked;
                    var reader = stream.getReader();
                    globalThis.__streamLockedDuring = stream.locked;
                    reader.read()
                        .then(function (first) {
                            globalThis.__streamFirst = String(first.value) + ':' + String(first.done);
                            return reader.read();
                        })
                        .then(function (second) {
                            globalThis.__streamSecond = String(second.value) + ':' + String(second.done);
                            reader.releaseLock();
                            globalThis.__streamLockedAfter = stream.locked;
                        })
                        .catch(function (error) {
                            globalThis.__streamError = String(error && error.message ? error.message : error);
                        });
                } catch (error) {
                    globalThis.__streamError = String(error && error.message ? error.message : error);
                }
                </script></body></html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            for (var i = 0; i < 50 && string.IsNullOrEmpty(engine.Evaluate("String(globalThis.__streamSecond || '')")?.ToString()); i++)
            {
                await Task.Delay(10);
            }

            Assert.Equal("function", engine.Evaluate("globalThis.__streamType")?.ToString());
            Assert.Equal(true, engine.Evaluate("globalThis.__streamCtor"));
            Assert.Equal(false, engine.Evaluate("globalThis.__streamLockedBefore"));
            Assert.Equal(true, engine.Evaluate("globalThis.__streamLockedDuring"));
            Assert.Equal("chunk:false", engine.Evaluate("globalThis.__streamFirst")?.ToString());
            Assert.Equal("undefined:true", engine.Evaluate("globalThis.__streamSecond")?.ToString());
            Assert.Equal(false, engine.Evaluate("globalThis.__streamLockedAfter"));
            Assert.Equal(string.Empty, engine.Evaluate("String(globalThis.__streamError || '')")?.ToString());
        }

        [Fact]
        public async Task BrowserSurfaceCompatibility_ExposesGoogleUsedDocumentImageAndUserAgentData()
        {
            var baseUri = new Uri("https://www.google.com/");
            var expectedSurface = BrowserSettings.GetBrowserSurface(BrowserSettings.Instance.SelectedUserAgent);
            var expectedUa = expectedSurface.UserAgentData;
            var document = new HtmlParser(
                """
                <html><body>
                <img id="logo" src="/logo.png">
                <script>
                globalThis.__docVisibility = document.visibilityState;
                globalThis.__docHidden = document.hidden;
                globalThis.__docContentType = document.contentType;
                globalThis.__imageComplete = document.getElementById('logo').complete;
                globalThis.__uaType = typeof navigator.userAgentData;
                globalThis.__uaPlatform = navigator.userAgentData.platform;
                globalThis.__uaMobile = navigator.userAgentData.mobile;
                globalThis.__uaBrandsLength = navigator.userAgentData.brands.length;
                globalThis.__uaJsonPlatformVersionType = typeof navigator.userAgentData.toJSON().platformVersion;
                navigator.userAgentData
                    .getHighEntropyValues(['architecture','bitness','platformVersion','uaFullVersion','fullVersionList','wow64'])
                    .then(function (values) {
                        globalThis.__uaArchitecture = values.architecture;
                        globalThis.__uaBitness = values.bitness;
                        globalThis.__uaPlatformVersion = values.platformVersion;
                        globalThis.__uaFullVersion = values.uaFullVersion;
                        globalThis.__uaFullVersionListLength = values.fullVersionList.length;
                        globalThis.__uaWow64 = values.wow64;
                    });
                </script>
                </body></html>
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

                for (var i = 0; i < 50 && string.IsNullOrEmpty(engine.Evaluate("String(globalThis.__uaFullVersion || '')")?.ToString()); i++)
                {
                    await Task.Delay(10);
                }

                Assert.Equal("visible", engine.Evaluate("globalThis.__docVisibility")?.ToString());
                Assert.Equal(false, engine.Evaluate("globalThis.__docHidden"));
                Assert.Equal("text/html", engine.Evaluate("globalThis.__docContentType")?.ToString());
                Assert.Equal(true, engine.Evaluate("globalThis.__imageComplete"));
                Assert.Equal("object", engine.Evaluate("globalThis.__uaType")?.ToString());
                Assert.Equal(expectedUa.Platform, engine.Evaluate("globalThis.__uaPlatform")?.ToString());
                Assert.Equal(expectedUa.Mobile, engine.Evaluate("globalThis.__uaMobile"));
                Assert.Equal(expectedUa.Brands.Count.ToString(), engine.Evaluate("String(globalThis.__uaBrandsLength)")?.ToString());
                Assert.Equal("undefined", engine.Evaluate("globalThis.__uaJsonPlatformVersionType")?.ToString());
                Assert.Equal(expectedUa.Architecture, engine.Evaluate("globalThis.__uaArchitecture")?.ToString());
                Assert.Equal(expectedUa.Bitness, engine.Evaluate("globalThis.__uaBitness")?.ToString());
                Assert.Equal(expectedUa.PlatformVersion, engine.Evaluate("globalThis.__uaPlatformVersion")?.ToString());
                Assert.Equal(GetExpectedPrimaryUserAgentFullVersion(expectedUa), engine.Evaluate("globalThis.__uaFullVersion")?.ToString());
                Assert.Equal(expectedUa.FullVersionList.Count.ToString(), engine.Evaluate("String(globalThis.__uaFullVersionListLength)")?.ToString());
                Assert.Equal(expectedUa.Wow64, engine.Evaluate("globalThis.__uaWow64"));
                Assert.DoesNotContain(
                    EngineCapabilities.GetUnsupportedJsSnapshot(),
                    feature => feature.Name is
                        "Document.visibilityState" or
                        "Document.hidden" or
                        "Document.contentType" or
                        "Element.complete" or
                        "Navigator.userAgentData");
            }
            finally
            {
                EngineCapabilities.Reset();
            }
        }

        private static string GetExpectedPrimaryUserAgentFullVersion(BrowserUserAgentDataProfile userAgentData)
        {
            var preferredBrand = userAgentData.FullVersionList.FirstOrDefault(brand =>
                !string.Equals(brand?.Brand, " Not;A Brand", StringComparison.Ordinal) &&
                !string.Equals(brand?.Brand, "Chromium", StringComparison.OrdinalIgnoreCase));
            if (preferredBrand != null)
            {
                return preferredBrand.Version ?? string.Empty;
            }

            var nonGreaseBrand = userAgentData.FullVersionList.FirstOrDefault(
                brand => !string.Equals(brand?.Brand, " Not;A Brand", StringComparison.Ordinal));
            return nonGreaseBrand?.Version ?? string.Empty;
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
