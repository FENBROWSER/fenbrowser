using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public sealed class BrowserScriptEngineRuntimeTests : IDisposable
    {
        private const string RuntimeSelectorVariable = "FEN_BROWSER_SCRIPT_ENGINE";
        private readonly string _originalMode = Environment.GetEnvironmentVariable(RuntimeSelectorVariable);

        public BrowserScriptEngineRuntimeTests()
        {
            BrowserScriptEngineRuntime.Reset();
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, null);
        }

        [Fact]
        public void Create_DefaultsToFenJsRuntime()
        {
            var engine = BrowserScriptEngineRuntime.Create(CreateHost());

            Assert.IsType<FenJsBrowserScriptEngine>(engine);
        }

        [Fact]
        public void Create_FenJsMode_UsesFenJsRuntime()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var engine = BrowserScriptEngineRuntime.Create(CreateHost());

            Assert.IsType<FenJsBrowserScriptEngine>(engine);
        }

        [Theory]
        [InlineData("legacy")]
        [InlineData("fenengine")]
        public void Create_LegacyEscapeHatch_UsesLegacyRuntime(string mode)
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, mode);
            BrowserScriptEngineRuntime.Reset();

            var engine = BrowserScriptEngineRuntime.Create(CreateHost());

            Assert.IsType<LegacyBrowserScriptEngineAdapter>(engine);
        }

        [Fact]
        public void FenJsMode_Evaluate_UsesFenJsForPreDomScripts()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var engine = BrowserScriptEngineRuntime.Create(CreateHost());

            Assert.Equal("42", Convert.ToString(engine.Evaluate("40 + 2"), CultureInfo.InvariantCulture));
        }

        [Fact]
        public async Task FenJsMode_PostDomEvaluation_FallsBackToLegacyBrowserRuntime()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser("<html><body><div id='app'></div></body></html>", baseUri).Parse();
            var engine = BrowserScriptEngineRuntime.Create(CreateHost());

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("object", engine.Evaluate("typeof document")?.ToString());
        }

        [Fact]
        public async Task FenJsMode_PostDomSimpleGlobals_ExecuteThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/app/index.html?x=1#frag");
            var document = new HtmlParser("<html><head><title>Fen</title></head><body><div id='app'>ok</div></body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            var legacy = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(document.DocumentElement, baseUri);
            await legacy.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("window === globalThis"));
            Assert.Equal(true, engine.Evaluate("self === globalThis"));
            Assert.Equal(legacy.Evaluate("document.readyState")?.ToString(), engine.Evaluate("document.readyState")?.ToString());
            Assert.Equal("/app/index.html", engine.Evaluate("location.pathname")?.ToString());
            Assert.Equal(true, engine.Evaluate("navigator.cookieEnabled"));
            Assert.True(engine.FenJsEvaluationCount > 0);
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_HistoryStateAndLocation_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/app/index.html");
            var document = new HtmlParser("<html><body><div id='app'>ok</div></body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            var bridge = new TestHistoryBridge(baseUri);
            engine.SetHistoryBridge(bridge);

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(@"
                (function () {
                    history.pushState({ step: 1 }, 'Title', '/watch?v=abc#pane');
                    var afterPush = location.href + ':' + history.state.step + ':' + history.length;
                    history.replaceState('next', '', '/next');
                    return [
                        typeof history.pushState,
                        typeof history.replaceState,
                        typeof history.back,
                        afterPush,
                        location.href,
                        history.state,
                        String(history.length)
                    ].join('|');
                })();
            ");

            Assert.Equal("function|function|function|https://example.com/watch?v=abc#pane:1:2|https://example.com/next|next|2", result?.ToString());
            Assert.True(bridge.PushStateCalled);
            Assert.True(bridge.ReplaceStateCalled);
            Assert.Equal("/next", bridge.LastUrl);
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_PostDomDocumentLookupAndMutation_StayOnFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/app/index.html");
            var document = new HtmlParser("<html><body class='boot'><div id='app'>ok</div></body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("ok", engine.Evaluate("document.getElementById('app').textContent")?.ToString());
            Assert.Equal("BODY", engine.Evaluate("document.querySelector('body').tagName")?.ToString());
            Assert.Equal("ready", engine.Evaluate("document.body.className = 'ready'; document.body.className")?.ToString());
            Assert.Equal("x", engine.Evaluate("document.getElementById('app').setAttribute('data-x', 'x'); document.getElementById('app').getAttribute('data-x')")?.ToString());
            Assert.Equal("DIV", engine.Evaluate("document.createElement('div').tagName")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_SetDomAsync_ExecutesInlinePageScriptThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/app/index.html");
            var document = new HtmlParser(
                "<html><body><div id='app'>ok</div><script>document.body.className='ready';document.getElementById('app').setAttribute('data-boot','done');globalThis.__bootResult=document.getElementById('app').getAttribute('data-boot');</script></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("document.body.className.split(' ').indexOf('ready') >= 0"));
            Assert.Equal(true, engine.Evaluate("document.body.className.split(' ').indexOf('js') >= 0"));
            Assert.Equal("done", engine.Evaluate("document.getElementById('app').getAttribute('data-boot')")?.ToString());
            Assert.Equal("done", engine.Evaluate("globalThis.__bootResult")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_SetDomAsync_ExecutesExternalBootstrapWithCurrentScriptThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/app/index.html");
            var document = new HtmlParser(
                "<html><body><script src=\"/assets/bootstrap.js\"></script></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.AllowExternalScripts = true;
            engine.ExternalScriptFetcher = static (_, _) => Task.FromResult(
                "var WIMB = WIMB || { init:function(){ globalThis.__wimbInit = true; }, meta:{} }; " +
                "var WIMB_UTIL = (window.WIMB.init(), window.WIMB.meta.js_src_url = document.currentScript.src, WIMB_UTIL || {});");

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__wimbInit"));
            Assert.Equal("https://example.com/assets/bootstrap.js", engine.Evaluate("window.WIMB.meta.js_src_url")?.ToString());
            Assert.Equal(true, engine.Evaluate("document.currentScript === null"));
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_SetDomAsync_FetchesExternalScriptsThroughBrowserFetchHandler()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/app/index.html");
            var document = new HtmlParser(
                "<html><body><div id='state'>pending</div><script src=\"/assets/bootstrap.js\"></script></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.AllowExternalScripts = true;
            engine.FetchHandler = request =>
            {
                Assert.Equal("https://example.com/assets/bootstrap.js", request.RequestUri?.AbsoluteUri);
                Assert.Equal(baseUri, request.Headers.Referrer);

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "document.getElementById('state').textContent = 'loaded'; globalThis.__fetchHandlerBootstrap = document.currentScript.src;")
                };
                return Task.FromResult(response);
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("loaded", engine.Evaluate("document.getElementById('state').textContent")?.ToString());
            Assert.Equal("https://example.com/assets/bootstrap.js", engine.Evaluate("globalThis.__fetchHandlerBootstrap")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_SetDomAsync_FiresStartupLifecycleHandlersThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/app/index.html");
            var document = new HtmlParser(
                "<html><body onload=\"globalThis.__bodyLoadReady = document.readyState; globalThis.__bodyLoadType = event && event.type;\"><div id='state'>pending</div><script>document.addEventListener('DOMContentLoaded', function (event) { globalThis.__domReady = document.readyState; globalThis.__domType = event && event.type; }); window.onload = function (event) { globalThis.__windowLoadReady = document.readyState; globalThis.__windowLoadType = event && event.type; document.getElementById('state').textContent = 'loaded'; };</script></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("interactive", engine.Evaluate("globalThis.__domReady")?.ToString());
            Assert.Equal("DOMContentLoaded", engine.Evaluate("globalThis.__domType")?.ToString());
            Assert.Equal("complete", engine.Evaluate("globalThis.__bodyLoadReady")?.ToString());
            Assert.Equal("load", engine.Evaluate("globalThis.__bodyLoadType")?.ToString());
            Assert.Equal("complete", engine.Evaluate("globalThis.__windowLoadReady")?.ToString());
            Assert.Equal("load", engine.Evaluate("globalThis.__windowLoadType")?.ToString());
            Assert.Equal("loaded", engine.Evaluate("document.getElementById('state').textContent")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_DynamicExternalScriptAssignedOnload_ExecutesThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                "<html><body><div id='state'>pending</div><script>document.addEventListener('DOMContentLoaded', function () { var s = document.createElement('script'); s.src = '/assets/chunk.js'; s.onload = function (event) { globalThis.__dynamicOnload = true; globalThis.__dynamicOnloadType = event && event.type; document.getElementById('state').textContent = 'loaded'; }; s.onerror = function () { globalThis.__dynamicOnerror = true; }; document.body.appendChild(s); });</script></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.AllowExternalScripts = true;
            engine.ExternalScriptFetcher = static (_, _) => Task.FromResult("globalThis.__dynamicChunkExecuted = true;");

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__dynamicChunkExecuted"));
            Assert.Equal(true, engine.Evaluate("globalThis.__dynamicOnload"));
            Assert.Equal("load", engine.Evaluate("globalThis.__dynamicOnloadType")?.ToString());
            Assert.Equal("undefined", engine.Evaluate("typeof globalThis.__dynamicOnerror")?.ToString());
            Assert.Equal("loaded", engine.Evaluate("document.getElementById('state').textContent")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_DynamicExternalScriptInsertedBeforeExistingScript_ExecutesThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                "<html><body><div id='state'>pending</div><script>document.addEventListener('DOMContentLoaded', function () { var first = document.getElementsByTagName('script')[0]; var s = document.createElement('script'); s.src = '/assets/before.js'; first.parentNode.insertBefore(s, first); globalThis.__insertBeforeCount = String(document.getElementsByTagName('script').length); globalThis.__insertBeforeFirstSrc = document.getElementsByTagName('script')[0].src; });</script></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.AllowExternalScripts = true;
            engine.ExternalScriptFetcher = static (_, _) => Task.FromResult("globalThis.__insertBeforeChunkExecuted = true; document.getElementById('state').textContent = 'loaded';");

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__insertBeforeChunkExecuted"));
            Assert.Equal("2", engine.Evaluate("globalThis.__insertBeforeCount")?.ToString());
            Assert.Equal("https://example.com/assets/before.js", engine.Evaluate("globalThis.__insertBeforeFirstSrc")?.ToString());
            Assert.Equal("loaded", engine.Evaluate("document.getElementById('state').textContent")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_DynamicExternalScriptAssignedOnerror_FiresThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                "<html><body><div id='state'>pending</div><script>document.addEventListener('DOMContentLoaded', function () { var s = document.createElement('script'); s.src = '/assets/missing.js'; s.onload = function () { globalThis.__dynamicOnload = true; }; s.onerror = function (event) { globalThis.__dynamicOnerror = true; globalThis.__dynamicOnerrorType = event && event.type; document.getElementById('state').textContent = 'error'; }; document.body.appendChild(s); });</script></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.AllowExternalScripts = true;
            engine.ExternalScriptFetcher = static (_, _) => Task.FromException<string>(new InvalidOperationException("simulated fetch failure"));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("undefined", engine.Evaluate("typeof globalThis.__dynamicOnload")?.ToString());
            Assert.Equal(true, engine.Evaluate("globalThis.__dynamicOnerror"));
            Assert.Equal("error", engine.Evaluate("globalThis.__dynamicOnerrorType")?.ToString());
            Assert.Equal("error", engine.Evaluate("document.getElementById('state').textContent")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_InnerHtmlInlineScript_ExecutesThroughFenJsWhenEnabled()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                "<html><body><div id='host'></div><script>document.getElementById('host').innerHTML = '<span id=\"state\">pending</span>' + '<scr' + 'ipt>globalThis.__innerHtmlScriptRan = true; document.getElementById(\"state\").textContent = \"updated\";</scr' + 'ipt>';</script></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.ExecuteInlineScriptsOnInnerHTML = true;

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__innerHtmlScriptRan"));
            Assert.Equal("updated", engine.Evaluate("document.getElementById('state').textContent")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_InsertAdjacentHtmlInlineScript_ExecutesThroughFenJsWhenEnabled()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                "<html><body><div id='host'></div><script>globalThis.__insertAdjacentType = typeof document.getElementById('host').insertAdjacentHTML; document.getElementById('host').insertAdjacentHTML('beforeend', '<span id=\"state\">pending</span>' + '<scr' + 'ipt>globalThis.__insertAdjacentScriptRan = true; document.getElementById(\"state\").textContent = \"updated\";</scr' + 'ipt>');</script></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.ExecuteInlineScriptsOnInnerHTML = true;

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("function", engine.Evaluate("globalThis.__insertAdjacentType")?.ToString());
            Assert.Equal(true, engine.Evaluate("globalThis.__insertAdjacentScriptRan"));
            Assert.Equal("updated", engine.Evaluate("document.getElementById('state').textContent")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_DocumentWrite_InsertsMarkupAtCurrentScriptPositionThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                "<html><body><div id='before'></div><script>document.write('<span id=\"written\">ok</span>'); globalThis.__writtenNextSibling = document.currentScript.nextSibling && document.currentScript.nextSibling.id;</script><p id='after'></p></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("written", engine.Evaluate("globalThis.__writtenNextSibling")?.ToString());
            Assert.Equal("ok", engine.Evaluate("document.getElementById('written').textContent")?.ToString());
            Assert.Equal("after", engine.Evaluate("document.getElementById('written').nextSibling.id")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_GoogleBootstrapCleanupAndHeadAppend_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                "<html><head><link id='a' blocking='render' rel='stylesheet' href='/a.css'><link id='b' blocking='render' rel='stylesheet' href='/b.css'></head><body><script>try{const links=document.querySelectorAll('link[blocking=render]');for(const link of links)link.remove();let styleNode;const ensureStyle=function(){styleNode||(styleNode=document.createElement('style'),document.head.append(styleNode));return styleNode;};ensureStyle().textContent='@view-transition{navigation:auto;}';globalThis.__remainingBlockingLinks=String(document.querySelectorAll('link[blocking=render]').length);globalThis.__headStyleCount=String(document.head.querySelectorAll('style').length);globalThis.__headStyleText=document.head.querySelector('style').textContent;globalThis.__googleStartupStatus='ok';}catch(e){globalThis.__googleStartupStatus=String(e&&e.message?e.message:e);}</script></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("ok", engine.Evaluate("globalThis.__googleStartupStatus")?.ToString());
            Assert.Equal("0", engine.Evaluate("globalThis.__remainingBlockingLinks")?.ToString());
            Assert.Equal("1", engine.Evaluate("globalThis.__headStyleCount")?.ToString());
            Assert.Equal("@view-transition{navigation:auto;}", engine.Evaluate("globalThis.__headStyleText")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_DelegationHelpersAndDataset_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                "<html><body><div id='root' data-vt-d='1'><span id='child'></span></div></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("function", engine.Evaluate("typeof document.getElementById('child').closest")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof document.getElementById('root').matches")?.ToString());
            Assert.Equal("object", engine.Evaluate("typeof document.getElementById('root').dataset")?.ToString());
            Assert.Equal("true", engine.Evaluate(@"
                (function () {
                    var child = document.getElementById('child');
                    var host = child.closest('[data-vt-d]');
                    if (!host) return 'false';
                    return String(host.matches('#root') && host.dataset.vtD === '1');
                })();
            ")?.ToString());
            Assert.Equal("ok:true", engine.Evaluate(@"
                (function () {
                    var host = document.getElementById('root');
                    host.dataset.vtFlag = 'ok';
                    host.toggleAttribute('hidden', true);
                    return host.getAttribute('data-vt-flag') + ':' + String(host.hasAttribute('hidden'));
                })();
            ")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_CharacterDataAndDocumentFragment_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                "<html><body><div id='host'></div></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("function", engine.Evaluate("typeof document.createTextNode")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof document.createComment")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof document.createDocumentFragment")?.ToString());
            Assert.Equal("true", engine.Evaluate(@"
                (function () {
                    var text = document.createTextNode('te');
                    text.appendData('st');
                    text.replaceData(2, 2, 'xt');
                    return String(text.data === 'text' && text.substringData(1, 2) === 'ex');
                })();
            ")?.ToString());
            Assert.Equal("none:none!", engine.Evaluate(@"
                (function () {
                    var comment = document.createComment('note');
                    comment.replaceData(2, 2, 'ne');
                    var fragment = document.createDocumentFragment();
                    if (fragment.nodeType !== Node.DOCUMENT_FRAGMENT_NODE) return 'bad-fragment-nodeType';
                    var span = document.createElement('span');
                    span.id = 'from-fragment';
                    span.appendChild(document.createTextNode(comment.data));
                    fragment.appendChild(span);
                    fragment.append('!');
                    document.getElementById('host').appendChild(fragment);
                    return document.getElementById('from-fragment').textContent + ':' + document.getElementById('host').textContent;
                })();
            ")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_TemplateContent_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("true|true|function|function|first|second|first|true", engine.Evaluate(@"
                (function () {
                    var template = document.createElement('template');
                    var first = document.createElement('span');
                    var second = document.createElement('b');
                    first.setAttribute('id', 'first');
                    second.setAttribute('id', 'second');
                    template.appendChild(second);
                    var content = template.content;
                    var inserted = content.insertBefore(first, content.firstChild);
                    var cloned = content.cloneNode(true);
                    return [
                        String(content instanceof DocumentFragment),
                        String(content === template.content),
                        typeof content.insertBefore,
                        typeof content.cloneNode,
                        content.firstChild.id,
                        content.firstChild.nextSibling.id,
                        cloned.firstChild.id,
                        String(inserted === first && template.textContent === '')
                    ].join('|');
                })();
            ")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_RangeSurface_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser("<html><body><div id='host'>abc</div></body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("function|function|true|true|#text|bc|false|bc|bc|EM|0|0|true|0|abc", engine.Evaluate(@"
                (function () {
                    var host = document.getElementById('host');
                    var text = host.firstChild;
                    var range = document.createRange();
                    range.setStart(text, 1);
                    range.setEnd(text, 3);
                    var constructed = new Range();
                    constructed.selectNodeContents(host);
                    var clone = range.cloneRange();
                    var contents = range.cloneContents();
                    var contextual = range.createContextualFragment('<em id=""created"">x</em>');
                    var rect = range.getBoundingClientRect();
                    var rects = range.getClientRects();

                    return [
                        typeof Range,
                        typeof document.createRange,
                        String(range instanceof Range),
                        String(constructed instanceof Range),
                        range.startContainer.nodeName,
                        range.toString(),
                        String(range.collapsed),
                        clone.toString(),
                        contents.firstChild.data,
                        contextual.firstChild.tagName,
                        String(rect.width),
                        String(rects.length),
                        String(rects.item(0) === null),
                        String(Range.START_TO_START),
                        constructed.toString()
                    ].join('|');
                })();
            ")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_DomConstructorsAndInstanceOf_WorkThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser(
                "<html><body><div></div><div></div></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("function", engine.Evaluate("typeof NodeList")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof HTMLCollection")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof DocumentFragment")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof CharacterData")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.querySelectorAll('div') instanceof NodeList)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.getElementsByTagName('div') instanceof HTMLCollection)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.createDocumentFragment() instanceof DocumentFragment)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(DocumentFragment.prototype instanceof Node)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.createTextNode('x') instanceof CharacterData)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(Comment.prototype instanceof CharacterData)")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_AttrCreationAndHierarchyRequest_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("function", engine.Evaluate("typeof document.createAttribute")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof Attr")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.createAttribute('x') instanceof Node)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.createAttribute('x') instanceof Attr)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(Attr.prototype instanceof Node)")?.ToString());
            Assert.Equal("x:", engine.Evaluate("document.createAttribute('x').name + ':' + document.createAttribute('x').value")?.ToString());
            Assert.Equal("HierarchyRequestError", engine.Evaluate(@"
                (function () {
                    var parent = document.createElement('div');
                    var attribute = document.createAttribute('x');
                    try { parent.appendChild(attribute); return 'no-error'; }
                    catch (e) { return e && e.name ? e.name : String(e); }
                })();
            ")?.ToString());
            Assert.Equal("HierarchyRequestError: Child must be a DOM node.", engine.Evaluate(@"
                (function () {
                    var parent = document.createElement('div');
                    var attribute = document.createAttribute('x');
                    try { parent.appendChild(attribute); return 'no-error'; }
                    catch (e) { return String(e); }
                })();
            ")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_NamedNodeMapAndAttributeNodeApis_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser("<html><body><div id='root' data-custom='value123'></div></body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("function", engine.Evaluate("typeof NamedNodeMap")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.getElementById('root').attributes instanceof NamedNodeMap)")?.ToString());
            Assert.Equal("2", engine.Evaluate("String(document.getElementById('root').attributes.length)")?.ToString());
            Assert.Equal("value123", engine.Evaluate("document.getElementById('root').attributes.getNamedItem('data-custom').value")?.ToString());
            Assert.Equal("id", engine.Evaluate("document.getElementById('root').attributes.item(0).name")?.ToString());
            Assert.Equal("value123", engine.Evaluate("document.getElementById('root').getAttributeNode('data-custom').value")?.ToString());
            Assert.Equal("hover-text", engine.Evaluate(@"
                (function () {
                    var root = document.getElementById('root');
                    var attr = document.createAttribute('title');
                    attr.value = 'hover-text';
                    root.setAttributeNode(attr);
                    return root.attributes.getNamedItem('title').value;
                })();
            ")?.ToString());
            Assert.Equal("data-custom", engine.Evaluate(@"
                (function () {
                    var root = document.getElementById('root');
                    var attr = root.getAttributeNode('data-custom');
                    return root.removeAttributeNode(attr).name;
                })();
            ")?.ToString());
            Assert.Equal("false", engine.Evaluate("String(document.getElementById('root').hasAttribute('data-custom'))")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_DocumentAndElementConstructors_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser("<html><body><div id='root'></div></body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("function", engine.Evaluate("typeof Document")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof Element")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof HTMLElement")?.ToString());
            Assert.Equal("object", engine.Evaluate("typeof Document.prototype")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof Element.prototype.matches")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof HTMLElement.prototype.matches")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document instanceof Document)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document instanceof Node)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.body instanceof Node)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.body instanceof Element)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.body instanceof HTMLElement)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.getElementById('root') instanceof Element)")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_DocumentConstructionAndCloning_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser("<html><body><table id='table'><tbody><tr><td>x</td></tr></tbody></table></body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("ok", engine.Evaluate(@"
                (function () {
                    function inspectDocument(doc, label) {
                        if (!doc) return label + ':doc';
                        if (typeof doc.cloneNode !== 'function') return label + ':cloneNode';
                        if (typeof doc.appendChild !== 'function') return label + ':appendChild';
                        if (typeof doc.getElementsByTagName !== 'function') return label + ':getElementsByTagName';
                        return 'ok';
                    }

                    var clone = document.cloneNode(true);
                    var cloneStatus = inspectDocument(clone, 'clone');
                    if (cloneStatus !== 'ok') return cloneStatus;
                    if (!clone.documentElement || !clone.getElementsByTagName('body')[0]) return 'clone:structure';

                    var created = new Document();
                    var createdStatus = inspectDocument(created, 'new');
                    if (createdStatus !== 'ok') return createdStatus;
                    if (created.documentElement !== null) return 'new:documentElement';
                    created.appendChild(document.documentElement.cloneNode(true));
                    if (!created.documentElement || !created.getElementsByTagName('body')[0]) return 'new:append';

                    var htmlDocument = document.implementation.createHTMLDocument('x');
                    var htmlStatus = inspectDocument(htmlDocument, 'html');
                    if (htmlStatus !== 'ok') return htmlStatus;
                    if (!htmlDocument.documentElement || !htmlDocument.getElementsByTagName('body')[0]) return 'html:structure';

                    htmlDocument.body.appendChild(document.getElementById('table').cloneNode(true));
                    var byTag = htmlDocument.getElementsByTagName('table').length;
                    var byQuery = htmlDocument.querySelectorAll('table').length;
                    return String(byTag) === '1' && String(byQuery) === '1' ? 'ok' : ('html:append:' + byTag + ':' + byQuery);
                })();
            ")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.implementation && typeof document.implementation.createHTMLDocument === 'function')")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_ClassListAndDomTokenList_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser("<html><body><div id='host' class='a b'></div></body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("[object DOMTokenList]", engine.Evaluate("Object.prototype.toString.call(document.getElementById('host').classList)")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof DOMTokenList")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.getElementById('host').classList instanceof DOMTokenList)")?.ToString());
            Assert.Equal("2", engine.Evaluate("String(document.getElementById('host').classList.length)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.getElementById('host').classList.contains('a'))")?.ToString());
            Assert.Equal("ok", engine.Evaluate(@"
                (function () {
                    var host = document.getElementById('host');
                    host.classList.toggle('c', true);
                    host.classList.remove('b');
                    host.classList.add('d');
                    return host.className === 'a c d' ? 'ok' : host.className;
                })();
            ")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_YouTubeBootstrapDomSurface_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser(
                "<html><body><a id='lnk' href='/watch?v=abc'></a><div id='root'><span id='child'></span></div></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(@"
                (function () {
                    var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
                    var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_ELEMENT);
                    var names = [];
                    var node;
                    while ((node = walker.nextNode()) && names.length < 3) {
                        names.push(node.id || node.tagName);
                    }

                    var documentSeen = 0;
                    document.addEventListener('yt-ready', function (event) { documentSeen = event.detail; });
                    var documentEvent = document.createEvent('CustomEvent');
                    documentEvent.initCustomEvent('yt-ready', true, true, 7);
                    var documentDispatchResult = document.dispatchEvent(documentEvent);

                    var child = document.getElementById('child');
                    var elementSeen = 0;
                    child.addEventListener('probe', function () { elementSeen = 1; });
                    var elementDispatchResult = child.dispatchEvent(new Event('probe'));
                    var windowSeen = 0;
                    window.addEventListener('script-load-dpj', function (event) { windowSeen = event.detail; });
                    var windowDispatchResult = window.dispatchEvent(new CustomEvent('script-load-dpj', { detail: 13 }));
                    var animation = child.animate([{ opacity: 0 }, { opacity: 1 }], 100);
                    animation.pause();
                    animation.currentTime = 40;
                    animation.updatePlaybackRate(2);
                    Object.defineProperty(document, '_activeElement', {
                        get: function () { return child; },
                        configurable: true
                    });
                    Object.defineProperty(child, '__ytData', {
                        value: 11,
                        writable: true,
                        configurable: true
                    });
                    var activeElementDescriptor = Object.getOwnPropertyDescriptor(document, '_activeElement');
                    var windowAddEventListenerDescriptor = Object.getOwnPropertyDescriptor(Window.prototype, 'addEventListener');
                    Object.defineProperty(Window.prototype, '__fen_native_addEventListener', windowAddEventListenerDescriptor);
                    var mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
                    var wheel = new WheelEvent('wheel', {
                        deltaY: 12,
                        deltaMode: WheelEvent.DOM_DELTA_LINE
                    });

                    return [
                        typeof document.createElementNS,
                        String(svg.tagName).toLowerCase(),
                        typeof document.createTreeWalker,
                        String(NodeFilter.SHOW_ELEMENT),
                        String(Node.ELEMENT_NODE),
                        String(Node.TEXT_NODE),
                        typeof CDATASection,
                        String(CDATASection.prototype instanceof Text),
                        typeof ProcessingInstruction,
                        String(ProcessingInstruction.prototype instanceof CharacterData),
                        names.join(','),
                        String(document.contains(child)),
                        String(documentDispatchResult),
                        String(documentSeen),
                        String(elementDispatchResult),
                        String(elementSeen),
                        String(windowDispatchResult),
                        String(windowSeen),
                        document.getElementById('lnk').href,
                        typeof child.animate,
                        animation.playState,
                        String(animation.currentTime),
                        String(animation.playbackRate),
                        String(animation.effect.length),
                        typeof animation.finished.then,
                        String(document._activeElement === child),
                        typeof activeElementDescriptor.get,
                        String(child.__ytData),
                        typeof Window,
                        String(window instanceof Window),
                        String(Object.getPrototypeOf(window) === Window.prototype),
                        typeof Window.prototype.addEventListener,
                        typeof window.__fen_native_addEventListener,
                        typeof window.DocumentFragment,
                        typeof CustomElementRegistry,
                        String(customElements instanceof CustomElementRegistry),
                        typeof CustomElementRegistry.prototype.define,
                        typeof ShadowRoot,
                        String(ShadowRoot.prototype instanceof DocumentFragment),
                        typeof window.matchMedia,
                        mediaQuery.media,
                        String(mediaQuery.matches),
                        typeof mediaQuery.addEventListener,
                        String(mediaQuery.dispatchEvent(new Event('change'))),
                        typeof WheelEvent,
                        String(WheelEvent.prototype instanceof MouseEvent),
                        String(WheelEvent.DOM_DELTA_LINE),
                        String(wheel.deltaY),
                        String(wheel.deltaMode),
                        typeof wheel.initWheelEvent
                    ].join('|');
                })();
            ");

            Assert.Equal("function|svg|function|1|1|3|function|true|function|true|A,DIV,SPAN|true|true|7|true|1|true|13|https://www.youtube.com/watch?v=abc|function|paused|40|2|2|function|true|function|11|function|true|true|function|function|function|function|true|function|function|true|function|(prefers-color-scheme: dark)|false|function|true|function|true|1|12|1|function", result?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_YouTubeObservedHostSurface_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser(
                "<html><body><div id='host' nonce='abc'></div></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(@"
                (function () {
                    document.domain = 'youtube.com';
                    var transitionCalled = 0;
                    var transition = document.startViewTransition(function () {
                        transitionCalled = 1;
                    });
                    var host = document.getElementById('host');
                    host.setProperties({
                        id: 'ready',
                        className: 'alpha',
                        textContent: 'loaded',
                        nonce: 'xyz',
                        customValue: 42
                    });
                    var fullscreenPromise = host.requestFullscreen();
                    var connection = navigator.connection;
                    var frame = document.createElement('iframe');
                    frame.setAttribute('sandbox', 'allow-scripts allow-same-origin');

                    return [
                        document.baseURI,
                        document.compatMode,
                        String(document.prerendering),
                        document.domain,
                        document.location.href,
                        typeof connection,
                        connection.effectiveType,
                        String(connection.saveData),
                        String(connection.downlink),
                        typeof connection.addEventListener,
                        String(connection.dispatchEvent(new Event('change'))),
                        typeof document.startViewTransition,
                        typeof document.releaseCapture,
                        typeof transition.ready.then,
                        typeof transition.updateCallbackDone.then,
                        typeof transition.finished.then,
                        typeof transition.skipTransition,
                        String(transitionCalled),
                        typeof host.requestFullscreen,
                        typeof host.webkitRequestFullscreen,
                        typeof host.mozRequestFullScreen,
                        typeof host.msRequestFullscreen,
                        typeof host.setCapture,
                        typeof host.setProperties,
                        host.id,
                        host.className,
                        host.textContent,
                        host.nonce,
                        String(host.customValue),
                        typeof fullscreenPromise.then,
                        typeof frame.sandbox,
                        String(frame.sandbox instanceof DOMTokenList),
                        frame.sandbox.value,
                        String(frame.sandbox.contains('allow-scripts')),
                        String(frame.getAttribute('sandbox')),
                        String(document.releaseCapture()),
                        String(host.setCapture())
                    ].join('|');
                })();
            ");

            Assert.Equal("https://www.youtube.com/|CSS1Compat|false|youtube.com|https://www.youtube.com/|object|4g|false|10|function|true|function|function|function|function|function|function|1|function|function|function|function|function|function|ready|alpha|loaded|xyz|42|function|object|true|allow-scripts allow-same-origin|true|allow-scripts allow-same-origin|undefined|undefined", result?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_CustomElementsUpgradeExistingDom_RunThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser(
                "<html><body><x-probe id='a'></x-probe><div><x-probe id='b'></x-probe></div></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(@"
                (function () {
                    var calls = [];
                    function Probe() {
                        this.constructed = this.id;
                        calls.push('ctor:' + this.id);
                    }
                    Probe.prototype.connectedCallback = function () {
                        this.setAttribute('upgraded', 'yes');
                        this.textContent = 'ready-' + this.id;
                        calls.push('connected:' + this.id);
                    };
                    Probe.prototype.answer = function () {
                        return 'answer:' + this.id + ':' + this.constructed;
                    };

                    customElements.define('x-probe', Probe);

                    var created = document.createElement('x-probe');
                    var createdBeforeAppend = created.answer();
                    created.id = 'c';
                    document.body.appendChild(created);

                    class ClassProbe extends HTMLElement {
                        constructor() {
                            super();
                            this.constructed = this.readId();
                        }
                        connectedCallback() {
                            this.textContent = this.constructed + ':' + this.id;
                        }
                        readId() {
                            return this.id;
                        }
                        answer() {
                            return this.constructed + ':' + this.id;
                        }
                    }

                    var classElement = document.createElement('x-class-probe');
                    classElement.id = 'class-created';
                    customElements.define('x-class-probe', ClassProbe);
                    document.body.appendChild(classElement);

                    return [
                        String(document.nodeType),
                        String(document.nodeType === Node.DOCUMENT_NODE),
                        typeof customElements.get('x-probe'),
                        document.getElementById('a').getAttribute('upgraded'),
                        document.getElementById('a').answer(),
                        document.getElementById('b').textContent,
                        createdBeforeAppend,
                        created.answer(),
                        created.textContent,
                        classElement.answer(),
                        classElement.textContent,
                        calls.join(',')
                    ].join('|');
                })();
            ");

            Assert.Equal("9|true|function|yes|answer:a:a|ready-b|answer::|answer:c:|ready-c|class-created:class-created|class-created:class-created|ctor:a,connected:a,ctor:b,connected:b,ctor:,connected:c", result?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_CustomElementsRunMultiLevelClassConstructorsOnHostElement()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(@"
                (function () {
                    class BaseProbe extends HTMLElement {
                        constructor() {
                            super();
                            this.base = this.readId();
                            this.hasReady = this.hasAttribute('data-ready');
                            this.mark('base');
                        }
                        readId() {
                            return this.id;
                        }
                        mark(value) {
                            this.trace = (this.trace || []).concat(value);
                        }
                    }

                    class MidProbe extends BaseProbe {
                        constructor() {
                            super();
                            this.mid = this.readId();
                            this.mark('mid');
                        }
                    }

                    class LeafProbe extends MidProbe {
                        constructor() {
                            super();
                            this.leaf = this.readId();
                            this.mark('leaf');
                        }
                        connectedCallback() {
                            this.textContent = this.trace.join(',');
                        }
                        answer() {
                            return this.base + ':' + this.mid + ':' + this.leaf + ':' + this.hasReady + ':' + this.textContent;
                        }
                    }

                    Object.defineProperty(
                        LeafProbe.prototype,
                        'hasAttribute',
                        Object.getOwnPropertyDescriptor(Element.prototype, 'hasAttribute'));

                    var element = document.createElement('x-leaf-probe');
                    element.id = 'leaf';
                    element.setAttribute('data-ready', 'yes');
                    customElements.define('x-leaf-probe', LeafProbe);
                    document.body.appendChild(element);
                    return element.answer();
                })();
            ");

            Assert.Equal("leaf:leaf:leaf:true:base,mid,leaf", result?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_CustomElementsRunEs5AdapterConstructorsOnHostElement()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(@"
                (function () {
                    var originalHTMLElement = window.HTMLElement;
                    var nativeDefine = window.customElements.define;
                    var nativeGet = window.customElements.get;
                    var namesByConstructor = new Map();
                    var constructorsByName = new Map();
                    var constructingBase = false;
                    var alreadyConstructing = false;

                    window.HTMLElement = function () {
                        if (!constructingBase) {
                            var name = namesByConstructor.get(this.constructor);
                            var constructor = nativeGet.call(window.customElements, name);
                            alreadyConstructing = true;
                            return new constructor();
                        }

                        constructingBase = false;
                    };
                    window.HTMLElement.prototype = originalHTMLElement.prototype;
                    window.HTMLElement.es5Shimmed = true;

                    Object.defineProperty(window.customElements, 'define', {
                        value: function (name, constructor) {
                            var userPrototype = constructor.prototype;
                            var wrapper = class extends originalHTMLElement {
                                constructor() {
                                    super();
                                    Object.setPrototypeOf(this, userPrototype);
                                    if (!alreadyConstructing) {
                                        constructingBase = true;
                                        constructor.call(this);
                                    }
                                    alreadyConstructing = false;
                                }
                            };

                            wrapper.prototype.connectedCallback = userPrototype.connectedCallback;
                            namesByConstructor.set(constructor, name);
                            constructorsByName.set(name, constructor);
                            nativeDefine.call(window.customElements, name, wrapper);
                        },
                        configurable: true
                    });
                    Object.defineProperty(window.customElements, 'get', {
                        value: function (name) {
                            return constructorsByName.get(name);
                        },
                        configurable: true
                    });

                    function BaseProbe() {
                        return HTMLElement.apply(this, arguments) || this;
                    }
                    BaseProbe.prototype = Object.create(HTMLElement.prototype);
                    BaseProbe.prototype.constructor = BaseProbe;

                    function LeafProbe() {
                        var self = BaseProbe.call(this) || this;
                        self.constructed = self.id || 'pending';
                        self.hasReady = self.hasAttribute('data-ready');
                        return self;
                    }
                    LeafProbe.prototype = Object.create(BaseProbe.prototype);
                    LeafProbe.prototype.constructor = LeafProbe;
                    LeafProbe.prototype.connectedCallback = function () {
                        this.textContent = this.constructed + ':' + String(this.hasReady);
                    };

                    var element = document.createElement('x-es5-adapter-probe');
                    element.id = 'leaf';
                    element.setAttribute('data-ready', 'yes');
                    customElements.define('x-es5-adapter-probe', LeafProbe);
                    document.body.appendChild(element);

                    return [
                        element.constructed,
                        String(element.hasReady),
                        element.textContent,
                        String(element instanceof HTMLElement),
                        String(element instanceof originalHTMLElement),
                        String(Object.getPrototypeOf(element) === LeafProbe.prototype)
                    ].join('|');
                })();
            ");

            Assert.Equal("leaf|true|leaf:true|true|true|true", result?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public async Task FenJsMode_BrowserRuntimeAllowsFrameworkDepthAboveCoreDefault()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://www.youtube.com/");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var result = engine.Evaluate(@"
                (function () {
                    function descend(n) {
                        return n === 0 ? 0 : 1 + descend(n - 1);
                    }
                    return String(descend(300));
                })();
            ");

            Assert.Equal("300", result?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        [Fact]
        public void FenJsMode_BrowserPolicyProperties_StillReachTheBackstopRuntime()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var engine = BrowserScriptEngineRuntime.Create(CreateHost());
            var fenJs = Assert.IsType<FenJsBrowserScriptEngine>(engine);

            engine.Sandbox = SandboxPolicy.NoScripts;
            engine.AllowExternalScripts = true;
            engine.ExecuteInlineScriptsOnInnerHTML = true;
            engine.SubresourceAllowed = static (_, _) => false;

            Assert.Same(SandboxPolicy.NoScripts, fenJs.LegacyInner.Sandbox);
            Assert.False(fenJs.LegacyInner.AllowExternalScripts);
            Assert.False(fenJs.LegacyInner.ExecuteInlineScriptsOnInnerHTML);
            Assert.False(fenJs.LegacyInner.SubresourceAllowed(new Uri("https://example.com/a.js"), "script"));
        }

        [Fact]
        public async Task FenJsMode_SetDomAsync_ExecutesModuleScriptWithImportsExportsThroughFenJs()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, "fenjs");
            BrowserScriptEngineRuntime.Reset();

            var baseUri = new Uri("https://example.com/app/index.html");
            var document = new HtmlParser(
                "<html><body><script type=\"module\">import { x } from './mod.js'; globalThis.__moduleResult = x;</script></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.AllowExternalScripts = true;
            engine.ExternalScriptFetcher = static (u, _) => {
                if (u.AbsoluteUri.EndsWith("mod.js"))
                {
                    return Task.FromResult("export const x = 42;");
                }
                return Task.FromResult("");
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("42", engine.Evaluate("globalThis.__moduleResult")?.ToString());
            Assert.Equal(0, engine.LegacyFallbackCount);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, _originalMode);
            BrowserScriptEngineRuntime.Reset();
        }

        private sealed class TestHistoryBridge : IHistoryBridge
        {
            private Uri _currentUrl;

            public TestHistoryBridge(Uri initialUrl)
            {
                _currentUrl = initialUrl;
            }

            public bool PushStateCalled { get; private set; }
            public bool ReplaceStateCalled { get; private set; }
            public bool GoCalled { get; private set; }
            public object State { get; private set; }
            public int Length { get; private set; } = 1;
            public Uri CurrentUrl => _currentUrl;
            public string LastTitle { get; private set; }
            public string LastUrl { get; private set; }
            public int LastDelta { get; private set; }

            public void PushState(object state, string title, string url)
            {
                PushStateCalled = true;
                State = state;
                LastTitle = title;
                LastUrl = url;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    _currentUrl = new Uri(_currentUrl, url);
                }
                Length++;
            }

            public void ReplaceState(object state, string title, string url)
            {
                ReplaceStateCalled = true;
                State = state;
                LastTitle = title;
                LastUrl = url;
                if (!string.IsNullOrWhiteSpace(url))
                {
                    _currentUrl = new Uri(_currentUrl, url);
                }
            }

            public void Go(int delta)
            {
                GoCalled = true;
                LastDelta = delta;
            }
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
