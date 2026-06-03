using System;
using System.Globalization;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
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

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(RuntimeSelectorVariable, _originalMode);
            BrowserScriptEngineRuntime.Reset();
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
