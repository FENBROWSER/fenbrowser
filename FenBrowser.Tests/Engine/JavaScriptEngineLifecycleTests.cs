using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class JavaScriptEngineLifecycleTests
    {
        [Fact]
        public async Task SetDomAsync_FiresDocumentDOMContentLoadedListenersRegisteredByScripts()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><script>document.addEventListener('DOMContentLoaded', function () { globalThis.__domReady = document.readyState; });</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("interactive", engine.Evaluate("globalThis.__domReady")?.ToString());
        }

        [Fact]
        public void WindowMetrics_AreExposedAsNumericProperties()
        {
            var engine = new JavaScriptEngine(CreateHost());

            Assert.Equal("number", engine.Evaluate("typeof window.innerWidth")?.ToString());
            Assert.Equal("number", engine.Evaluate("typeof window.innerHeight")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_GlobalBootstrapProbe_SeesMathOnWindowAndGlobalThis()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><script>var x=function(a){var c=[typeof globalThis==='object'&&globalThis,a,typeof window==='object'&&window,typeof self==='object'&&self];for(var i=0;i<c.length;++i){var e=c[i];if(e&&e.Math===Math){globalThis.__bootstrapProbeWindow=e===window;globalThis.__bootstrapProbeGlobalThis=e===globalThis;return e;}}throw Error('b');};x(this);</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__bootstrapProbeWindow"));
            Assert.Equal(true, engine.Evaluate("globalThis.__bootstrapProbeGlobalThis"));
            Assert.Equal(true, engine.Evaluate("window.Math === Math"));
        }

        [Fact]
        public async Task SetDomAsync_ImageConstructorAndImgSrcReflection_AreAvailable()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><img id='hero'><script>var probe=document.getElementById('hero');globalThis.__initialImgSrcType=typeof probe.src;globalThis.__initialImgSrcValue=probe.src;var beacon=new Image(16,8);beacon.src='https://example.com/pixel.png';globalThis.__imageCtorType=typeof Image;globalThis.__imageTagName=beacon.tagName;globalThis.__imageWidth=beacon.width;globalThis.__imageHeight=beacon.height;globalThis.__imageSrc=beacon.src;</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("function", engine.Evaluate("globalThis.__imageCtorType")?.ToString());
            Assert.Equal("string", engine.Evaluate("globalThis.__initialImgSrcType")?.ToString());
            Assert.Equal(string.Empty, engine.Evaluate("globalThis.__initialImgSrcValue")?.ToString());
            Assert.Equal("IMG", engine.Evaluate("globalThis.__imageTagName")?.ToString());
            Assert.Equal("16", engine.Evaluate("String(globalThis.__imageWidth)")?.ToString());
            Assert.Equal("8", engine.Evaluate("String(globalThis.__imageHeight)")?.ToString());
            Assert.Equal("https://example.com/pixel.png", engine.Evaluate("globalThis.__imageSrc")?.ToString());
        }

        [Fact]
        public void WimbCapabilities_GlobalProvidesEncodedCapabilityPayload()
        {
            var engine = new JavaScriptEngine(CreateHost());

            Assert.Equal("object", engine.Evaluate("typeof WIMB_CAPABILITIES")?.ToString());
            Assert.Equal("1", engine.Evaluate("WIMB_CAPABILITIES.capabilities.javascript")?.ToString());

            var encoded = engine.Evaluate("WIMB_CAPABILITIES.get_as_json_string()")?.ToString();

            Assert.False(string.IsNullOrWhiteSpace(encoded));
            Assert.Contains("%22javascript%22%3A%221%22", encoded);
        }

        [Fact]
        public void ClipboardJsStub_IsAvailableAndReportsUnsupported()
        {
            var engine = new JavaScriptEngine(CreateHost());

            Assert.Equal("function", engine.Evaluate("typeof ClipboardJS")?.ToString());
            Assert.Equal(false, engine.Evaluate("ClipboardJS.isSupported()"));
        }

        [Fact]
        public async Task NavigatorUserAgentData_ExposesLowAndHighEntropyValues()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><script>(async function(){ var values = await navigator.userAgentData.getHighEntropyValues(['platformVersion','uaFullVersion']); globalThis.__uaPlatformVersion = values.platformVersion; globalThis.__uaFullVersion = values.uaFullVersion; })();</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("object", engine.Evaluate("typeof navigator.userAgentData")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof navigator.userAgentData.toJSON")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof navigator.userAgentData.getHighEntropyValues")?.ToString());
            Assert.Equal("Windows", engine.Evaluate("navigator.userAgentData.platform")?.ToString());
            Assert.Equal("3", engine.Evaluate("String(navigator.userAgentData.brands.length)")?.ToString());
            Assert.Equal("undefined", engine.Evaluate("typeof navigator.userAgentData.toJSON().platformVersion")?.ToString());
            Assert.Equal("undefined", engine.Evaluate("typeof navigator.userAgentData.toJSON().uaFullVersion")?.ToString());
            Assert.Equal("15.0.0", engine.Evaluate("globalThis.__uaPlatformVersion")?.ToString());
            Assert.Equal("146.0.7800.12", engine.Evaluate("globalThis.__uaFullVersion")?.ToString());
        }

        [Fact]
        public void MatchMedia_TracksThemeAndViewportSurfaceChanges()
        {
            var previousTheme = BrowserSettings.Instance.Theme;

            try
            {
                BrowserSettings.Instance.Theme = ThemePreference.Dark;
                var engine = new JavaScriptEngine(CreateHost());

                Assert.Equal(true, engine.Evaluate("window.matchMedia('(prefers-color-scheme: dark)').matches"));
                Assert.Equal(false, engine.Evaluate("window.matchMedia('(prefers-color-scheme: light)').matches"));
                Assert.Equal(false, engine.Evaluate("window.matchMedia('(max-width: 600px)').matches"));

                engine.Evaluate("var __mql = window.matchMedia('(max-width: 600px)'); __mql.addEventListener('change', function(e){ globalThis.__mqlChange = String(e.matches); });");
                engine.WindowWidth = 500;

                Assert.Equal(true, engine.Evaluate("window.matchMedia('(max-width: 600px)').matches"));
                Assert.Equal("true", engine.Evaluate("globalThis.__mqlChange")?.ToString());
            }
            finally
            {
                BrowserSettings.Instance.Theme = previousTheme;
            }
        }

        [Fact]
        public async Task VisualViewport_TracksWindowMetrics_AndDispatchesResize()
        {
            var engine = new JavaScriptEngine(CreateHost());

            Assert.Equal("object", engine.Evaluate("typeof window.visualViewport")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof window.visualViewport.addEventListener")?.ToString());
            Assert.Equal(
                engine.Evaluate("String(window.innerWidth)")?.ToString(),
                engine.Evaluate("String(window.visualViewport.width)")?.ToString());
            Assert.Equal(
                engine.Evaluate("String(window.innerHeight)")?.ToString(),
                engine.Evaluate("String(window.visualViewport.height)")?.ToString());
            Assert.Equal("1", engine.Evaluate("String(window.visualViewport.scale)")?.ToString());

            engine.Evaluate(@"
                globalThis.__visualViewportResizeCount = 0;
                window.visualViewport.addEventListener('resize', function () {
                    globalThis.__visualViewportResizeCount++;
                    globalThis.__visualViewportResizeWidth = String(window.visualViewport.width);
                    globalThis.__visualViewportResizeHeight = String(window.visualViewport.height);
                });
            ");

            engine.WindowWidth = 640;
            engine.WindowHeight = 480;
            await Task.Delay(25);

            Assert.Equal("640", engine.Evaluate("String(window.visualViewport.width)")?.ToString());
            Assert.Equal("480", engine.Evaluate("String(window.visualViewport.height)")?.ToString());
            Assert.Equal("2", engine.Evaluate("String(globalThis.__visualViewportResizeCount)")?.ToString());
            Assert.Equal("640", engine.Evaluate("globalThis.__visualViewportResizeWidth")?.ToString());
            Assert.Equal("480", engine.Evaluate("globalThis.__visualViewportResizeHeight")?.ToString());
        }

        [Fact]
        public void IntlLocale_FallbackExposesCanonicalLocaleMetadata_AndTextDirection()
        {
            var engine = new JavaScriptEngine(CreateHost());

            Assert.Equal("function", engine.Evaluate("typeof Intl.Locale")?.ToString());
            Assert.Equal("en-US", engine.Evaluate("new Intl.Locale('en-us').baseName")?.ToString());
            Assert.Equal("en", engine.Evaluate("new Intl.Locale('en-us').language")?.ToString());
            Assert.Equal("US", engine.Evaluate("new Intl.Locale('en-us').region")?.ToString());
            Assert.Equal("zh-Hant-TW", engine.Evaluate("new Intl.Locale('zh-hant-tw').toString()")?.ToString());
            Assert.Equal("ltr", engine.Evaluate("new Intl.Locale('en-us').getTextInfo().direction")?.ToString());
            Assert.Equal("rtl", engine.Evaluate("new Intl.Locale('ar').getTextInfo().direction")?.ToString());
            Assert.Equal("fa", engine.Evaluate("new Intl.Locale('fa').maximize().toString()")?.ToString());
        }

        [Fact]
        public async Task ExternalScriptExecution_ExposesDocumentCurrentScript()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><script src=\"/assets/app.js\"></script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost())
            {
                ExternalScriptFetcher = (uri, _) => Task.FromResult(
                    "globalThis.__currentScriptSrc = document.currentScript && document.currentScript.getAttribute('src');")
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("/assets/app.js", engine.Evaluate("globalThis.__currentScriptSrc")?.ToString());
            Assert.Equal(true, engine.Evaluate("document.currentScript === null"));
        }

        [Fact]
        public async Task ExternalScriptBootstrap_CanUseWindowGlobalAndCurrentScriptInSameDeclaration()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><script src=\"/assets/bootstrap.js\"></script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost())
            {
                ExternalScriptFetcher = (uri, _) => Task.FromResult(
                    "var WIMB = WIMB || { init:function(){ globalThis.__wimbInit = true; }, meta:{} }; " +
                    "var WIMB_UTIL = (window.WIMB.init(), window.WIMB.meta.js_src_url = document.currentScript.src, WIMB_UTIL || {}); " +
                    "var do_capabilities_detection = function(){};")
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__wimbInit"));
            Assert.Equal("object", engine.Evaluate("typeof window.WIMB")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof do_capabilities_detection")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_DocumentWrite_InsertsMarkupAtCurrentScriptPosition()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><div id='before'></div><script>document.write('<span id=\"written\">ok</span>'); globalThis.__writtenNextSibling = document.currentScript.nextSibling && document.currentScript.nextSibling.id;</script><p id='after'></p></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("written", engine.Evaluate("globalThis.__writtenNextSibling")?.ToString());
            Assert.Equal("ok", engine.Evaluate("document.getElementById('written').textContent")?.ToString());
            Assert.Equal("after", engine.Evaluate("document.getElementById('written').nextSibling.id")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_BodyOnloadAttribute_ExecutesDuringDocumentLoad()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body onload=\"globalThis.__bodyLoaded = true; document.getElementById('state').textContent = 'loaded';\"><div id='state'>pending</div></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__bodyLoaded"));
            Assert.Equal("loaded", engine.Evaluate("document.getElementById('state').textContent")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_IframeContentDocument_IsIsolatedFromMainDocument()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body onload=\"var frame = document.getElementById('frame'); var doc = frame.contentDocument; globalThis.__iframeHasIsolatedDoc = doc !== document; var p = doc.createElement('p'); p.textContent = 'frame'; doc.body.appendChild(p); globalThis.__iframeText = doc.body.textContent; globalThis.__mainText = document.getElementById('state').textContent;\"><iframe id='frame'></iframe><div id='state'>main</div></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__iframeHasIsolatedDoc"));
            Assert.Equal("frame", engine.Evaluate("globalThis.__iframeText")?.ToString());
            Assert.Equal("main", engine.Evaluate("globalThis.__mainText")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_IframeInlineOnloadAttribute_ExecutesDuringInitialDocumentLoad()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><a id='linktest' class='pending'>pending</a><iframe id='frame' onload=\"document.getElementById('linktest').removeAttribute('class'); globalThis.__iframeInlineLoadTag = this.tagName; globalThis.__iframeInlineLoadTarget = event && event.target && event.target.id;\"></iframe></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal(string.Empty, engine.Evaluate("document.getElementById('linktest').className")?.ToString());
            Assert.Equal("IFRAME", engine.Evaluate("globalThis.__iframeInlineLoadTag")?.ToString());
            Assert.Equal("frame", engine.Evaluate("globalThis.__iframeInlineLoadTarget")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_IframeSrcMutation_DispatchesInlineLoadHandler()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body onload=\"document.getElementById('frame').src = 'empty.html?probe';\"><a id='linktest' class='pending'>pending</a><iframe id='frame' onload=\"document.getElementById('linktest').removeAttribute('class'); globalThis.__iframeSrcLoadTarget = event && event.target && event.target.id;\"></iframe></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);
            await Task.Delay(50);

            Assert.Equal(string.Empty, engine.Evaluate("document.getElementById('linktest').className")?.ToString());
            Assert.Equal("frame", engine.Evaluate("globalThis.__iframeSrcLoadTarget")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_IframeContentDocument_DefaultViewTracksContentWindow()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body onload=\"var frame = document.getElementById('frame'); var doc = frame.contentDocument; globalThis.__sameDefaultView = doc.defaultView === frame.contentWindow; globalThis.__hasComputedStyle = typeof doc.defaultView.getComputedStyle;\"><iframe id='frame'></iframe></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__sameDefaultView"));
            Assert.Equal("function", engine.Evaluate("globalThis.__hasComputedStyle")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_IframeContentDocument_ComputedStyleReflectsInjectedStyles()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body onload=\"var frame = document.getElementById('frame'); var doc = frame.contentDocument; var style = doc.createElement('style'); style.textContent = '#probe { text-transform: uppercase; }'; doc.head.appendChild(style); var p = doc.createElement('p'); p.id = 'probe'; doc.body.appendChild(p); globalThis.__iframeTextTransform = doc.defaultView.getComputedStyle(p, '').textTransform;\"><iframe id='frame'></iframe></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("uppercase", engine.Evaluate("globalThis.__iframeTextTransform")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_IframeContentDocument_ComputedStyleExposesInitialValues()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body onload=\"var frame = document.getElementById('frame'); var doc = frame.contentDocument; var p = doc.createElement('p'); doc.body.appendChild(p); var style = doc.defaultView.getComputedStyle(p, ''); globalThis.__iframeDefaultTextTransform = style.textTransform; globalThis.__iframeDefaultCursor = style.cursor;\"><iframe id='frame'></iframe></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("none", engine.Evaluate("globalThis.__iframeDefaultTextTransform")?.ToString());
            Assert.Equal("auto", engine.Evaluate("globalThis.__iframeDefaultCursor")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_IframeContentDocument_ComputedStyleRejectsBogusMediaAndCursorValues()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body onload=\"var frame = document.getElementById('frame'); var doc = frame.contentDocument; var style = doc.createElement('style'); style.textContent = '@media (bogus) { #probe { text-transform: uppercase; } } @media all and color { #probe { text-transform: uppercase; } } #probe { cursor: bogus; }'; doc.head.appendChild(style); var p = doc.createElement('p'); p.id = 'probe'; doc.body.appendChild(p); var computed = doc.defaultView.getComputedStyle(p, ''); globalThis.__iframeBogusMediaTextTransform = computed.textTransform; globalThis.__iframeBogusCursor = computed.cursor;\"><iframe id='frame'></iframe></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("none", engine.Evaluate("globalThis.__iframeBogusMediaTextTransform")?.ToString());
            Assert.Equal("auto", engine.Evaluate("globalThis.__iframeBogusCursor")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_GetComputedStyle_ExposesTextDecorationLineWithoutThrowing()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><head><style>#probe { text-decoration: underline; }</style></head><body><span id='probe'>x</span><script>var s = getComputedStyle(document.getElementById('probe')); globalThis.__textDecorationLine = s.textDecorationLine; globalThis.__textDecoration = s.textDecoration; globalThis.__textDecorationCheck = s.textDecorationLine.indexOf('underline') !== -1 || s.textDecoration.indexOf('underline') !== -1;</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Contains("underline", engine.Evaluate("globalThis.__textDecorationLine")?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("underline", engine.Evaluate("globalThis.__textDecoration")?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(true, engine.Evaluate("globalThis.__textDecorationCheck"));
        }

        [Fact]
        public async Task SetDomAsync_RadioCheckedProperty_IsIndependentFromCheckedAttribute()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><script>var a = document.createElement('input'); a.type = 'radio'; a.name = 'g'; document.body.appendChild(a); var b = document.createElement('input'); b.type = 'radio'; b.name = 'g'; document.body.appendChild(b); a.checked = true; b.setAttribute('checked', 'checked'); globalThis.__radioA = a.checked; globalThis.__radioB = b.checked; globalThis.__radioAMatches = window.getComputedStyle(a, '').zIndex; globalThis.__radioSelectorA = a.matches(':checked'); globalThis.__radioSelectorB = b.matches(':checked');</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__radioA"));
            Assert.Equal(false, engine.Evaluate("globalThis.__radioB"));
            Assert.Equal(true, engine.Evaluate("globalThis.__radioSelectorA"));
            Assert.Equal(false, engine.Evaluate("globalThis.__radioSelectorB"));
        }

        [Fact]
        public async Task SetDomAsync_TableDomSurface_ExposesCaptionSectionsRowsAndInsertion()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><table id='t'><tr id='r0'><td id='c0'>a</td></tr></table><script>var table = document.getElementById('t'); var caption = table.createCaption(); caption.id = 'cap'; var thead = table.createTHead(); var headRow = thead.insertRow(0); headRow.id = 'rh'; var bodyRow = table.insertRow(-1); bodyRow.id = 'rb'; var bodyCell = document.createElement('td'); bodyCell.id = 'cb'; bodyRow.appendChild(bodyCell); var tfoot = table.createTFoot(); var footRow = tfoot.insertRow(0); footRow.id = 'rf'; globalThis.__tableCaptionId = table.caption && table.caption.id; globalThis.__tableTHeadTag = table.tHead && table.tHead.tagName; globalThis.__tableTFootTag = table.tFoot && table.tFoot.tagName; globalThis.__tableTBodiesLength = String(table.tBodies.length); globalThis.__tableRowsLength = String(table.rows.length); globalThis.__theadRowsLength = String(table.tHead.rows.length); globalThis.__tbodyRowsLength = String(table.tBodies[0].rows.length); globalThis.__tfootRowsLength = String(table.tFoot.rows.length); globalThis.__bodyCellsLength = String(bodyRow.cells.length); globalThis.__bodyRowIndex = String(bodyRow.rowIndex); globalThis.__bodySectionRowIndex = String(bodyRow.sectionRowIndex); table.deleteCaption(); table.deleteTHead(); table.deleteTFoot(); globalThis.__afterDeleteCaption = table.caption === null; globalThis.__afterDeleteTHead = table.tHead === null; globalThis.__afterDeleteTFoot = table.tFoot === null;</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("cap", engine.Evaluate("globalThis.__tableCaptionId")?.ToString());
            Assert.Equal("THEAD", engine.Evaluate("globalThis.__tableTHeadTag")?.ToString());
            Assert.Equal("TFOOT", engine.Evaluate("globalThis.__tableTFootTag")?.ToString());
            Assert.Equal("1", engine.Evaluate("globalThis.__tableTBodiesLength")?.ToString());
            Assert.Equal("4", engine.Evaluate("globalThis.__tableRowsLength")?.ToString());
            Assert.Equal("1", engine.Evaluate("globalThis.__theadRowsLength")?.ToString());
            Assert.Equal("2", engine.Evaluate("globalThis.__tbodyRowsLength")?.ToString());
            Assert.Equal("1", engine.Evaluate("globalThis.__tfootRowsLength")?.ToString());
            Assert.Equal("1", engine.Evaluate("globalThis.__bodyCellsLength")?.ToString());
            Assert.Equal("2", engine.Evaluate("globalThis.__bodyRowIndex")?.ToString());
            Assert.Equal("1", engine.Evaluate("globalThis.__bodySectionRowIndex")?.ToString());
            Assert.Equal(true, engine.Evaluate("globalThis.__afterDeleteCaption"));
            Assert.Equal(true, engine.Evaluate("globalThis.__afterDeleteTHead"));
            Assert.Equal(true, engine.Evaluate("globalThis.__afterDeleteTFoot"));
        }

        [Fact]
        public async Task SetDomAsync_FormElements_ReflectDynamicNameTypeAndValueChanges()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><script>var f = document.createElement('form'); var i = document.createElement('input'); i.name = 'first'; i.type = 'text'; i.value = 'test'; f.appendChild(i); globalThis.__formName1 = i.name; globalThis.__formType1 = i.type; globalThis.__formValue1 = i.value; globalThis.__formValueAttr1 = i.hasAttribute('value'); globalThis.__formLen1 = String(f.elements.length); globalThis.__formNamed1 = f.elements.first === i; i.name = 'second'; i.type = 'password'; i.value = 'TEST'; globalThis.__formName2 = i.name; globalThis.__formType2 = i.type; globalThis.__formValue2 = i.value; globalThis.__formValueAttr2 = i.hasAttribute('value'); globalThis.__formLen2 = String(f.length); globalThis.__formNamed2 = f.elements.second === i; globalThis.__formNamed1Cleared = f.elements.first === null;</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("first", engine.Evaluate("globalThis.__formName1")?.ToString());
            Assert.Equal("text", engine.Evaluate("globalThis.__formType1")?.ToString());
            Assert.Equal("test", engine.Evaluate("globalThis.__formValue1")?.ToString());
            Assert.Equal(false, engine.Evaluate("globalThis.__formValueAttr1"));
            Assert.Equal("1", engine.Evaluate("globalThis.__formLen1")?.ToString());
            Assert.Equal(true, engine.Evaluate("globalThis.__formNamed1"));
            Assert.Equal("second", engine.Evaluate("globalThis.__formName2")?.ToString());
            Assert.Equal("password", engine.Evaluate("globalThis.__formType2")?.ToString());
            Assert.Equal("TEST", engine.Evaluate("globalThis.__formValue2")?.ToString());
            Assert.Equal(false, engine.Evaluate("globalThis.__formValueAttr2"));
            Assert.Equal("1", engine.Evaluate("globalThis.__formLen2")?.ToString());
            Assert.Equal(true, engine.Evaluate("globalThis.__formNamed2"));
            Assert.Equal(true, engine.Evaluate("globalThis.__formNamed1Cleared"));
        }

        [Fact]
        public async Task SetDomAsync_SelectAndButtonSurfaces_ExposeAcid3CompatibilitySemantics()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><script>var s = document.createElement('select'); var o = document.createElement('option'); s.add(o, null); var o2 = document.createElement('option'); o2.defaultSelected = true; s.appendChild(o2); var button = document.createElement('button'); globalThis.__selectChild = s.firstChild === o; globalThis.__selectLength = String(s.options.length); globalThis.__selectDefaultIndex = String(s.selectedIndex); globalThis.__buttonType1 = button.type; button.setAttribute('type', 'button'); globalThis.__buttonType2 = button.type; button.removeAttribute('type'); globalThis.__buttonType3 = button.type; button.setAttribute('value', 'apple'); globalThis.__buttonValue = button.value;</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__selectChild"));
            Assert.Equal("2", engine.Evaluate("globalThis.__selectLength")?.ToString());
            Assert.Equal("1", engine.Evaluate("globalThis.__selectDefaultIndex")?.ToString());
            Assert.Equal("submit", engine.Evaluate("globalThis.__buttonType1")?.ToString());
            Assert.Equal("button", engine.Evaluate("globalThis.__buttonType2")?.ToString());
            Assert.Equal("submit", engine.Evaluate("globalThis.__buttonType3")?.ToString());
            Assert.Equal("apple", engine.Evaluate("globalThis.__buttonValue")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_ObjectData_ResolvesRelativeUrlsAgainstDocumentUrl()
        {
            var baseUri = new Uri("http://acid3.acidtests.org/");
            var parser = new HtmlParser(
                "<html><body><script>var p = document.createElement('p'); globalThis.__titleDefault = p.title; p.title = 'ready'; globalThis.__titleAfterSet = p.title; var obj1 = document.createElement('object'); obj1.setAttribute('data', 'test.html'); var obj2 = document.createElement('object'); obj2.setAttribute('data', './test.html'); globalThis.__documentUrl = document.URL; globalThis.__documentBaseUri = document.baseURI; globalThis.__objectData1 = obj1.data; globalThis.__objectData2 = obj2.data;</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("http://acid3.acidtests.org/", engine.Evaluate("globalThis.__documentUrl")?.ToString());
            Assert.Equal("http://acid3.acidtests.org/", engine.Evaluate("globalThis.__documentBaseUri")?.ToString());
            Assert.Equal(string.Empty, engine.Evaluate("globalThis.__titleDefault")?.ToString());
            Assert.Equal("ready", engine.Evaluate("globalThis.__titleAfterSet")?.ToString());
            Assert.Equal("http://acid3.acidtests.org/test.html", engine.Evaluate("globalThis.__objectData1")?.ToString());
            Assert.Equal("http://acid3.acidtests.org/test.html", engine.Evaluate("globalThis.__objectData2")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_DocumentQuerySelectorSupportsDescendantSelectors()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><div id='javascript-detection'><span class='detection-message no-javascript'>No</span></div>" +
                "<script>document.addEventListener('DOMContentLoaded', function () { var target = document.querySelector('#javascript-detection .detection-message'); globalThis.__selectorFound = !!target; if (target) target.innerHTML = 'Yes'; });</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__selectorFound"));
            Assert.Equal("Yes", engine.Evaluate("document.querySelector('#javascript-detection .detection-message').textContent")?.ToString());

            var updatedSpan = doc
                .Descendants()
                .OfType<Element>()
                .First(e => string.Equals(e.TagName, "SPAN", StringComparison.OrdinalIgnoreCase) &&
                            e.ClassList.Contains("detection-message"));
            Assert.Equal(1, updatedSpan.ChildNodes.Length);
            Assert.IsType<Text>(updatedSpan.FirstChild);
            Assert.DoesNotContain(
                updatedSpan.Descendants().OfType<Element>(),
                e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(e.TagName, "BODY", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task SetDomAsync_SetIntervalFunctionCallbackRunsFromDOMContentLoadedHandler()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><div id='state'>pending</div>" +
                "<script>document.addEventListener('DOMContentLoaded', function () { var id = setInterval(function () { document.getElementById('state').innerHTML = 'updated'; clearInterval(id); }, 1); });</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);
            await Task.Delay(50);

            Assert.Equal("updated", engine.Evaluate("document.getElementById('state').textContent")?.ToString());
        }

        [Fact]
        public async Task RequestIdleCallback_CallbackRunsWithDeadlineObject()
        {
            var engine = new JavaScriptEngine(CreateHost());

            engine.Evaluate(@"
                globalThis.__idleHandleType = typeof requestIdleCallback(function (deadline) {
                    globalThis.__idleDidRun = true;
                    globalThis.__idleDeadlineShape =
                        typeof deadline + ':' +
                        typeof deadline.didTimeout + ':' +
                        typeof deadline.timeRemaining + ':' +
                        String(deadline.timeRemaining() >= 0);
                });
            ");

            await Task.Delay(50);

            Assert.Equal("number", engine.Evaluate("globalThis.__idleHandleType")?.ToString());
            Assert.Equal(true, engine.Evaluate("globalThis.__idleDidRun"));
            Assert.Equal("object:boolean:function:true", engine.Evaluate("globalThis.__idleDeadlineShape")?.ToString());
        }

        [Fact]
        public async Task CancelIdleCallback_PreventsPendingCallbackInvocation()
        {
            var engine = new JavaScriptEngine(CreateHost());

            engine.Evaluate(@"
                globalThis.__idleCancelled = false;
                var handle = requestIdleCallback(function () {
                    globalThis.__idleCancelled = true;
                });
                cancelIdleCallback(handle);
            ");

            await Task.Delay(50);

            Assert.Equal(false, engine.Evaluate("globalThis.__idleCancelled"));
        }

        [Fact]
        public async Task SetDomAsync_DynamicExternalScriptInsertedViaAppendChildExecutesAndUpdatesState()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><div id='state'>pending</div>" +
                "<script>document.addEventListener('DOMContentLoaded', function () { window.__mutationObserved = false; new MutationObserver(function () { window.__mutationObserved = true; }).observe(document.body, { childList: true }); try { var s = document.createElement('script'); s.setAttribute('src', '/assets/dynamic.js'); document.body.appendChild(s); window.__appendChildStatus = 'ok'; } catch (e) { window.__appendChildStatus = String(e && e.message ? e.message : e); } });</script>" +
                "</body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost())
            {
                ExternalScriptFetcher = (uri, _) => Task.FromResult(
                    "window.__dynamicScriptLoaded = true; document.getElementById('state').innerHTML = 'updated';")
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);
            await Task.Delay(75);

            var appendChildStatus = engine.Evaluate("window.__appendChildStatus")?.ToString();
            var scriptCount = engine.Evaluate("String(document.getElementsByTagName('script').length)")?.ToString();
            var mutationObserved = engine.Evaluate("window.__mutationObserved");
            var dynamicScriptLoaded = engine.Evaluate("window.__dynamicScriptLoaded");
            var stateText = engine.Evaluate("document.getElementById('state').textContent")?.ToString();
            var mutationObservedBool = string.Equals(mutationObserved?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
            var dynamicScriptLoadedBool = string.Equals(dynamicScriptLoaded?.ToString(), "true", StringComparison.OrdinalIgnoreCase);

            Assert.Equal("ok", appendChildStatus);
            Assert.Equal("2", scriptCount);
            Assert.True(
                mutationObservedBool,
                $"mutationObserved={mutationObserved}, dynamicScriptLoaded={dynamicScriptLoaded}, stateText={stateText}, appendChildStatus={appendChildStatus}, scriptCount={scriptCount}");
            Assert.True(
                dynamicScriptLoadedBool,
                $"dynamicScriptLoaded={dynamicScriptLoaded}, mutationObserved={mutationObserved}, stateText={stateText}, appendChildStatus={appendChildStatus}, scriptCount={scriptCount}");
            Assert.Equal("updated", stateText);
        }

        [Fact]
        public async Task SetDomAsync_DynamicExternalScriptAssignedOnloadFiresAfterExecution()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><div id='state'>pending</div>" +
                "<script>document.addEventListener('DOMContentLoaded', function () { var s = document.createElement('script'); s.src = '/assets/chunk.js'; s.onload = function (event) { window.__dynamicOnload = true; window.__dynamicOnloadType = event && event.type ? String(event.type) : 'missing'; document.getElementById('state').textContent = 'loaded'; }; s.onerror = function () { window.__dynamicOnerror = true; }; document.body.appendChild(s); });</script>" +
                "</body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost())
            {
                ExternalScriptFetcher = (uri, _) => Task.FromResult(
                    "window.__dynamicChunkExecuted = true;")
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);
            await Task.Delay(75);

            Assert.Equal("true", engine.Evaluate("String(window.__dynamicChunkExecuted)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(window.__dynamicOnload)")?.ToString());
            Assert.Equal("load", engine.Evaluate("String(window.__dynamicOnloadType)")?.ToString());
            Assert.Equal("undefined", engine.Evaluate("typeof window.__dynamicOnerror")?.ToString());
            Assert.Equal("loaded", engine.Evaluate("document.getElementById('state').textContent")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_DynamicExternalScriptAssignedOnerrorFiresOnFetchFailure()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><div id='state'>pending</div>" +
                "<script>document.addEventListener('DOMContentLoaded', function () { var s = document.createElement('script'); s.src = '/assets/missing.js'; s.onload = function () { window.__dynamicOnload = true; }; s.onerror = function (event) { window.__dynamicOnerror = true; window.__dynamicOnerrorType = event && event.type ? String(event.type) : 'missing'; document.getElementById('state').textContent = 'error'; }; document.body.appendChild(s); });</script>" +
                "</body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost())
            {
                ExternalScriptFetcher = (_, _) => Task.FromException<string>(new InvalidOperationException("simulated fetch failure"))
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);
            await Task.Delay(75);

            Assert.Equal("undefined", engine.Evaluate("typeof window.__dynamicOnload")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(window.__dynamicOnerror)")?.ToString());
            Assert.Equal("error", engine.Evaluate("String(window.__dynamicOnerrorType)")?.ToString());
            Assert.Equal("error", engine.Evaluate("document.getElementById('state').textContent")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_WindowAssignedGlobalsAreVisibleToLaterInlineScripts()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><div id='javascript-detection'><span class='detection-message'>pending</span></div>" +
                "<script src='/assets/bootstrap.js'></script>" +
                "<script>document.addEventListener('DOMContentLoaded', function () { document.querySelector('#javascript-detection .detection-message').innerHTML = detect_yes; globalThis.__detectYesType = typeof detect_yes; });</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost())
            {
                ExternalScriptFetcher = (uri, _) => Task.FromResult("window.detect_yes = 'Yes';")
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("string", engine.Evaluate("globalThis.__detectYesType")?.ToString());
            Assert.Equal("Yes", engine.Evaluate("document.querySelector('#javascript-detection .detection-message').textContent")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_WindowBracketGlobalsSeedLaterBareIdentifierReads()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><div id='javascript-detection'><span class='detection-message'>pending</span></div>" +
                "<script src='/assets/bootstrap.js'></script>" +
                "<script>document.addEventListener('DOMContentLoaded', function () { document.querySelector('#javascript-detection .detection-message').innerHTML = detect_yes + ' - ' + detect_javascript_is_enabled; globalThis.__detectSeedType = typeof detect_yes; });</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost())
            {
                ExternalScriptFetcher = (uri, _) => Task.FromResult(
                    "for (var key in { detect_yes: 'Yes', detect_javascript_is_enabled: 'JavaScript is enabled' }) {" +
                    "  if (!(key in window)) {" +
                    "    window[key] = ({ detect_yes: 'Yes', detect_javascript_is_enabled: 'JavaScript is enabled' })[key];" +
                    "  }" +
                    "}")
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("string", engine.Evaluate("globalThis.__detectSeedType")?.ToString());
            Assert.Equal("Yes - JavaScript is enabled", engine.Evaluate("document.querySelector('#javascript-detection .detection-message').textContent")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_UnqualifiedTopLevelAssignmentsRemainCallableFromLaterInlineHandlers()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><div id='state'>pending</div>" +
                "<script src='/assets/bootstrap.js'></script>" +
                "<script>document.addEventListener('DOMContentLoaded', function () { do_capabilities_detection(); });</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost())
            {
                ExternalScriptFetcher = (uri, _) => Task.FromResult(
                    "do_capabilities_detection = function(){ document.getElementById('state').innerHTML = 'updated'; globalThis.__detectFnType = typeof do_capabilities_detection; };")
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("function", engine.Evaluate("globalThis.__detectFnType")?.ToString());
            Assert.Equal("updated", engine.Evaluate("document.getElementById('state').textContent")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_WhatIsMyBrowserExactDoCapabilitiesDetectionUpdatesFallbackRows()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body>" +
                "<ul id='your-browsers-settings' class='no-javascript google-anno-skip'>" +
                "  <li id='your-browsers-settings-javascript'><a id='javascript-detection'><span class='detection-message no-javascript'>No - JavaScript is not enabled</span></a></li>" +
                "  <li id='your-browsers-settings-cookies'><a id='cookies-detection'><span class='detection-message'>pending</span></a></li>" +
                "  <li id='your-browsers-settings-cookies-third-party'><a id='third-party-cookies-detection'><span class='detection-message'>pending</span></a></li>" +
                "</ul>" +
                "<div id='local-ip-address'><div class='detected-column'><div class='detected-column-text'></div><div id='local-ip-address-detection-blocked' style='display:none'></div></div></div>" +
                "<div id='computer-screen-detection'></div>" +
                "<div id='browser-window-size'><span id='browser-window-size-detection'></span></div>" +
                "<div id='detected-addons' style='display:none'><ul id='detected-addons-ul'></ul></div>" +
                "<ul id='technical-details'></ul>" +
                "<div id='primary-browser-detection'></div>" +
                "<div id='primary-browser-detection-backend'></div>" +
                "<div id='readout-primary'></div>" +
                "<script src='/assets/bootstrap.js'></script>" +
                "<script>window.third_party_domain = 'webbrowsertests.com';</script>" +
                "<script>document.addEventListener('DOMContentLoaded', function(event) { globalThis.__domLoadedHookRan = true; do_capabilities_detection(); globalThis.__afterHookJavascriptText = document.querySelector('#javascript-detection .detection-message').textContent; globalThis.__afterHookClassName = document.getElementById('your-browsers-settings').className; });</script>" +
                "</body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost())
            {
                ExternalScriptFetcher = (uri, _) => Task.FromResult(BuildWhatIsMyBrowserBootstrapScript())
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);
            await Task.Delay(75);

            Assert.Equal("function", engine.Evaluate("typeof do_capabilities_detection")?.ToString());
            Assert.Equal(true, engine.Evaluate("globalThis.__domLoadedHookRan"));
            Assert.Equal("Yes - JavaScript is enabled", engine.Evaluate("globalThis.__afterHookJavascriptText")?.ToString());
            Assert.DoesNotContain(
                "no-javascript",
                engine.Evaluate("globalThis.__afterHookClassName")?.ToString() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Yes - Third-Party Cookies are enabled", engine.Evaluate("document.querySelector('#third-party-cookies-detection .detection-message').textContent")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_WhatIsMyBrowserFullBootstrapExposesDetectionGlobals()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body>" +
                "<ul id='your-browsers-settings' class='no-javascript google-anno-skip'>" +
                "  <li id='your-browsers-settings-javascript'><a id='javascript-detection'><span class='detection-message no-javascript'>No - JavaScript is not enabled</span></a></li>" +
                "  <li id='your-browsers-settings-cookies'><a id='cookies-detection'><span class='detection-message'>pending</span></a></li>" +
                "  <li id='your-browsers-settings-cookies-third-party'><a id='third-party-cookies-detection'><span class='detection-message'>pending</span></a></li>" +
                "</ul>" +
                "<div id='local-ip-address'><div class='detected-column'><div class='detected-column-text'></div><div id='local-ip-address-detection-blocked' style='display:none'></div></div></div>" +
                "<div id='computer-screen-detection'></div>" +
                "<div id='browser-window-size'><span id='browser-window-size-detection'></span></div>" +
                "<div id='detected-addons' style='display:none'><ul id='detected-addons-ul'></ul></div>" +
                "<ul id='technical-details'></ul>" +
                "<div id='primary-browser-detection'></div>" +
                "<div id='primary-browser-detection-backend'></div>" +
                "<div id='readout-primary'></div>" +
                "<script src='/assets/site.min.js'></script>" +
                "<script>window.third_party_domain = 'webbrowsertests.com';</script>" +
                "<script>document.addEventListener('DOMContentLoaded', function(event) { globalThis.__domLoadedHookRan = true; globalThis.__preCallDoCapabilitiesType = typeof do_capabilities_detection; globalThis.__preCallDetectYesType = typeof detect_yes; globalThis.__stageProbeError = 'ok'; function __probe(name, fn) { if (globalThis.__stageProbeError !== 'ok') { return; } try { fn(); globalThis.__stageProbeLast = name; } catch (e) { globalThis.__stageProbeError = name + ': ' + String(e&&e.message?e.message:e); } } __probe('cookies.enabled', function() { WIMB.detect.cookies.enabled(); }); __probe('cookies.third_party.trigger_set_cookie', function() { WIMB.detect.cookies_third_party.trigger_set_cookie('https://example.com'); }); __probe('local_ipv4_addresses.trigger_detection', function() { WIMB.detect.local_ipv4_addresses.trigger_detection(); }); __probe('computer_screen', function() { WIMB.detect.computer_screen.width(); WIMB.detect.computer_screen.height(); }); __probe('browser_window_size', function() { WIMB.detect.browser_window_size.width(); WIMB.detect.browser_window_size.height(); }); __probe('add_row_to_tech_details', function() { add_row_to_tech_details({key:'probe',value:'probe'}); }); __probe('gmt_offset', function() { WIMB.detect.gmt_offset(); }); __probe('navigator.platform', function() { WIMB.detect.navigator.platform.value(); WIMB.detect.navigator.platform.value_human_readable(); }); __probe('navigator.misc', function() { WIMB.detect.navigator.oscpu.value(); WIMB.detect.navigator.vendor.value(); WIMB.detect.navigator.hardware_concurrency.value(); WIMB.detect.navigator.mime_types.all(); WIMB.detect.navigator.navigator_ram.gigabytes(); WIMB.detect.navigator.max_touch_points.value(); }); __probe('webgl', function() { WIMB.detect.webgl.vendor(); WIMB.detect.webgl.renderer(); }); __probe('client_hints', function() { WIMB.detect.client_hints.frontend.available(); }); __probe('looks_like', function() { WIMB.detect.looks_like.software.detection_tests(); WIMB.detect.looks_like.operating_system.detection_tests(); }); __probe('ecma', function() { WIMB.detect.ecma(); }); try { do_capabilities_detection(); globalThis.__doCapabilitiesError = 'ok'; } catch (e) { globalThis.__doCapabilitiesError = String(e&&e.message?e.message:e); } var detectionMessage = document.querySelector('#javascript-detection .detection-message'); globalThis.__afterHookJavascriptText = detectionMessage ? detectionMessage.textContent : null; });</script>" +
                "</body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost())
            {
                ExternalScriptFetcher = (uri, _) => Task.FromResult(LoadWhatIsMyBrowserSiteMinScript())
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);
            await Task.Delay(75);

            Assert.Equal(true, engine.Evaluate("globalThis.__domLoadedHookRan"));
            Assert.Equal("string", engine.Evaluate("globalThis.__preCallDetectYesType")?.ToString());
            Assert.Equal("function", engine.Evaluate("globalThis.__preCallDoCapabilitiesType")?.ToString());
            Assert.Equal("ok", engine.Evaluate("globalThis.__stageProbeError")?.ToString());
            Assert.Equal("ok", engine.Evaluate("globalThis.__doCapabilitiesError")?.ToString());
            Assert.Equal("Yes - JavaScript is enabled", engine.Evaluate("globalThis.__afterHookJavascriptText")?.ToString());
            Assert.Equal(true, engine.Evaluate("document.getElementById('computer-screen-detection').textContent.indexOf('Pixels') >= 0"));
            Assert.Equal(true, engine.Evaluate("document.getElementById('browser-window-size-detection').textContent.indexOf('Pixels') >= 0"));
        }

        [Fact]
        public async Task ExternalScriptBootstrap_TopLevelThisResolvesToWindowForUmdWrappers()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><script src='/assets/umd.js'></script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost())
            {
                ExternalScriptFetcher = (uri, _) => Task.FromResult(
                    "((t,e)=>{globalThis.__umdThisIsWindow=(t===window);t.__umdBootstrap=e();})(this,function(){return {ok:true};});")
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate("globalThis.__umdThisIsWindow"));
            Assert.Equal(true, engine.Evaluate("window.__umdBootstrap && window.__umdBootstrap.ok"));
        }

        [Fact]
        public async Task SetDomAsync_WhatIsMyBrowserTranslationSeedLoopPublishesWindowGlobals()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><script>for(var TRANSLATION_STRING in TRANSLATION_STRINGS={detect_yes:'Yes',detect_javascript_is_enabled:'JavaScript is enabled'})TRANSLATION_STRING in window==!1&&(window[TRANSLATION_STRING]=TRANSLATION_STRINGS[TRANSLATION_STRING]);globalThis.__detectYesType=typeof detect_yes;globalThis.__detectEnabledType=typeof detect_javascript_is_enabled;</script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("string", engine.Evaluate("globalThis.__detectYesType")?.ToString());
            Assert.Equal("string", engine.Evaluate("globalThis.__detectEnabledType")?.ToString());
            Assert.Equal("Yes", engine.Evaluate("detect_yes")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_ElementConstructorPublishesPrototypeMatchesForBundlePolyfills()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser("<html><body><div id='root'></div></body></html>", baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("function", engine.Evaluate("typeof Element.prototype.matches")?.ToString());
            Assert.Equal("function", engine.Evaluate("typeof HTMLElement.prototype.matches")?.ToString());
            Assert.Equal("object", engine.Evaluate("typeof Document.prototype")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_DomWrappersParticipateInInstanceofChecksUsedByBootstrapBundles()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser("<html><head></head><body><div id='root'></div></body></html>", baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            engine.Evaluate(@"
                var body = document.body;
                var root = document.getElementById('root');
                var text = document.createTextNode('hello');
                var comment = document.createComment('note');
                globalThis.__instanceofDocument = String(document instanceof Document);
                globalThis.__instanceofDocumentNode = String(document instanceof Node);
                globalThis.__instanceofBodyNode = String(body instanceof Node);
                globalThis.__instanceofBodyElement = String(body instanceof Element);
                globalThis.__instanceofBodyHtmlElement = String(body instanceof HTMLElement);
                globalThis.__instanceofRootElement = String(root instanceof Element);
                globalThis.__instanceofTextNode = String(text instanceof Node);
                globalThis.__instanceofTextText = String(text instanceof Text);
                globalThis.__instanceofCommentNode = String(comment instanceof Node);
                globalThis.__instanceofCommentComment = String(comment instanceof Comment);
            ");

            Assert.Equal("true", engine.Evaluate("globalThis.__instanceofDocument")?.ToString());
            Assert.Equal("true", engine.Evaluate("globalThis.__instanceofDocumentNode")?.ToString());
            Assert.Equal("true", engine.Evaluate("globalThis.__instanceofBodyNode")?.ToString());
            Assert.Equal("true", engine.Evaluate("globalThis.__instanceofBodyElement")?.ToString());
            Assert.Equal("true", engine.Evaluate("globalThis.__instanceofBodyHtmlElement")?.ToString());
            Assert.Equal("true", engine.Evaluate("globalThis.__instanceofRootElement")?.ToString());
            Assert.Equal("true", engine.Evaluate("globalThis.__instanceofTextNode")?.ToString());
            Assert.Equal("true", engine.Evaluate("globalThis.__instanceofTextText")?.ToString());
            Assert.Equal("true", engine.Evaluate("globalThis.__instanceofCommentNode")?.ToString());
            Assert.Equal("true", engine.Evaluate("globalThis.__instanceofCommentComment")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_NodeWrapper_ExposesRootNodeAndConnectivityForReactNativeWebBootstrap()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser("<html><head></head><body><div id='react-root'></div></body></html>", baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            engine.Evaluate(@"
                var rootTag = document.getElementById('react-root');
                var rootNode = rootTag.getRootNode();
                var composedRootNode = rootTag.getRootNode({ composed: true });
                var style = document.createElement('style');
                style.setAttribute('data-rnw-probe', '1');
                style.textContent = '.rnw-probe{display:block;}';
                rootNode.head.appendChild(style);
                globalThis.__rnwRootNodeType = String(rootNode.nodeType);
                globalThis.__rnwComposedRootNodeType = String(composedRootNode.nodeType);
                globalThis.__rnwRootIsConnected = String(rootTag.isConnected);
                globalThis.__rnwRootLookupId = rootNode.getElementById('react-root').id;
                globalThis.__rnwStyleIsConnected = String(style.isConnected);
                globalThis.__rnwHeadStyleCount = String(rootNode.head.getElementsByTagName('style').length);
                globalThis.__rnwInsertedStyleText = rootNode.head.lastChild && rootNode.head.lastChild.textContent;
            ");

            Assert.Equal("function", engine.Evaluate("typeof document.getElementById('react-root').getRootNode")?.ToString());
            Assert.Equal("9", engine.Evaluate("globalThis.__rnwRootNodeType")?.ToString());
            Assert.Equal("9", engine.Evaluate("globalThis.__rnwComposedRootNodeType")?.ToString());
            Assert.Equal("true", engine.Evaluate("globalThis.__rnwRootIsConnected")?.ToString());
            Assert.Equal("react-root", engine.Evaluate("globalThis.__rnwRootLookupId")?.ToString());
            Assert.Equal("true", engine.Evaluate("globalThis.__rnwStyleIsConnected")?.ToString());
            Assert.Equal("1", engine.Evaluate("globalThis.__rnwHeadStyleCount")?.ToString());
            Assert.Equal(".rnw-probe{display:block;}", engine.Evaluate("globalThis.__rnwInsertedStyleText")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_DatePrototypePublishesGetTimezoneOffsetForCapabilityScripts()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser("<html><body></body></html>", baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("function", engine.Evaluate("typeof Date.prototype.getTimezoneOffset")?.ToString());
            Assert.NotEqual("NaN", engine.Evaluate("String((new Date()).getTimezoneOffset())")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_WhatIsMyBrowserFullBootstrapCapturesFirstBundleError()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><body><script src='/assets/site.min.js'></script></body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost())
            {
                ExternalScriptFetcher = (uri, _) => Task.FromResult(
                    "try{" + LoadWhatIsMyBrowserSiteMinScript() + ";globalThis.__bundleEvalStatus='ok';}" +
                    "catch(e){globalThis.__bundleEvalStatus=String(e&&e.message?e.message:e);}")
            };

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("ok", engine.Evaluate("globalThis.__bundleEvalStatus")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_GoogleBootstrapCleanup_IteratesNodeListAndRemovesBlockingLinks()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><head>" +
                "<link id='a' blocking='render' rel='stylesheet' href='/a.css'>" +
                "<link id='b' blocking='render' rel='stylesheet' href='/b.css'>" +
                "</head><body>" +
                "<script>(function(){function cleanup(){const links=document.querySelectorAll('link[blocking=render]');for(const link of links)link.remove();globalThis.__remainingBlockingLinks=document.querySelectorAll('link[blocking=render]').length;}cleanup();})();</script>" +
                "</body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("0", engine.Evaluate("String(globalThis.__remainingBlockingLinks)")?.ToString());
            Assert.Equal("0", engine.Evaluate("String(document.querySelectorAll('link[blocking=render]').length)")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_DocumentQuerySelectorAll_WithoutSelector_ReturnsEmptyNodeList()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser("<html><body><div></div></body></html>", baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("0", engine.Evaluate("String(document.querySelectorAll().length)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(document.querySelectorAll().item(0) === null)")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_GoogleViewTransitionBootstrap_AppendsStyleIntoHead()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><head></head><body>" +
                "<script>var __styleNode;function ensureStyle(){__styleNode||(__styleNode=document.createElement('style'),document.head.append(__styleNode));return __styleNode;}ensureStyle().textContent='@view-transition{navigation:auto;}';globalThis.__headStyleCount=document.head.querySelectorAll('style').length;globalThis.__headStyleText=document.head.querySelector('style').textContent;</script>" +
                "</body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("1", engine.Evaluate("String(globalThis.__headStyleCount)")?.ToString());
            Assert.Equal("@view-transition{navigation:auto;}", engine.Evaluate("globalThis.__headStyleText")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_GoogleViewTransitionBootstrap_SeesWindowAndDocumentEventTargets()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><head></head><body>" +
                "<script>globalThis.__windowAddEventListenerType=typeof window.addEventListener;globalThis.__documentAddEventListenerType=typeof document.addEventListener;window.addEventListener('pageswap',function(){});window.addEventListener('pagereveal',async function(){});document.addEventListener('click',function(){});</script>" +
                "</body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("function", engine.Evaluate("globalThis.__windowAddEventListenerType")?.ToString());
            Assert.Equal("function", engine.Evaluate("globalThis.__documentAddEventListenerType")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_GoogleViewTransitionBootstrap_NarrowStartupPathRunsWithoutThrow()
        {
            var baseUri = new Uri("https://example.com/index.html");
            var parser = new HtmlParser(
                "<html><head>" +
                "<link id='render-blocker' blocking='render' rel='stylesheet' href='/a.css'>" +
                "</head><body>" +
                "<form><input name='q' value='fen'></form>" +
                "<script>try{const links=document.querySelectorAll('link[blocking=render]');for(const link of links)link.remove();let styleNode;function ensureStyle(){styleNode||(styleNode=document.createElement('style'),document.head.append(styleNode));return styleNode;}window.addEventListener('pageswap',function(){},{once:true});window.addEventListener('pagereveal',function(){ensureStyle().textContent='@view-transition{navigation:none;}';},{once:true});document.addEventListener('click',function(event){for(const binding of [{i:'data-vt-d',types:[]}]){if(event.target&&event.target.closest&&event.target.closest('['+binding.i+']')){globalThis.__vtClickMatched=true;}}},{capture:true,passive:true});ensureStyle().textContent='@view-transition{navigation:auto;}';globalThis.__googleStartupStatus='ok';}catch(e){globalThis.__googleStartupStatus=String(e&&e.message?e.message:e);}</script>" +
                "</body></html>",
                baseUri);
            var doc = parser.Parse();

            var engine = new JavaScriptEngine(CreateHost());

            await engine.SetDomAsync(doc.DocumentElement, baseUri);

            Assert.Equal("ok", engine.Evaluate("globalThis.__googleStartupStatus")?.ToString());
            Assert.Equal("@view-transition{navigation:auto;}", engine.Evaluate("document.head.querySelector('style').textContent")?.ToString());
        }

        [Fact]
        public async Task SetDomAsync_DocumentWithoutDocumentElement_DoesNotThrow()
        {
            var baseUri = new Uri("https://example.com/empty.html");
            var engine = new JavaScriptEngine(CreateHost());
            var doc = new FenBrowser.Core.Dom.V2.Document();

            await engine.SetDomAsync(doc, baseUri);

            Assert.Equal("object", engine.Evaluate("typeof document")?.ToString());
            Assert.Equal("object", engine.Evaluate("typeof window")?.ToString());
        }

        private static JsHostAdapter CreateHost()
        {
            ElementStateManager.Reset();
            return new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: _ => { });
        }

        private static string BuildWhatIsMyBrowserBootstrapScript()
        {
            return string.Concat(
                "window.detect_yes='Yes';",
                "window.detect_no='No';",
                "window.detect_javascript_is_enabled='JavaScript is enabled';",
                "window.detect_cookies_are_enabled='Cookies are enabled';",
                "window.detect_cookies_not_enabled='Cookies are not enabled';",
                "window.detect_third_party_cookies_are_enabled='Third-Party Cookies are enabled';",
                "window.detect_third_party_cookies_not_enabled='Third-Party Cookies are not enabled';",
                "window.detect_try_reloading='Error. Try reloading.';",
                "window.please_wait='Please wait...';",
                "window.detect_pixels='Pixels';",
                "window.detect_gmt_offset='Browser GMT Offset';",
                "window.detect_pixel_ratio='Device Pixel Ratio';",
                "window.detect_platform='Navigator Platform';",
                "window.detect_oscpu='OS / CPU';",
                "window.detect_vendor='Vendor';",
                "window.detect_hardware_concurrency='No. of logical CPU cores';",
                "window.detect_ram_gb='RAM';",
                "window.detect_max_touch_points='Maximum Touch Points';",
                "window.detect_webgl_vendor='WebGL Vendor';",
                "window.detect_webgl_renderer='WebGL Renderer';",
                "window.detect_ecma_version='ECMA Version';",
                "window.detect_looks_like='Your web browser looks like:';",
                "window.WIMB_CAPABILITIES={capabilities:{},add:function(key,value,group){if(group){this.capabilities[group]=this.capabilities[group]||{};this.capabilities[group][key]=value;}else{this.capabilities[key]=value;}},add_update:function(key,value){this.add(key,value);},get_as_json_string:function(){return '{}';}};",
                "window.add_row_to_tech_details=function(row){window.__techDetailsCount=(window.__techDetailsCount||0)+1;};",
                "window.WIMB={detect:{",
                "cookies:{enabled:function(){return true;}},",
                "cookies_third_party:{trigger_set_cookie:function(){return true;},enabled:function(){return 1;}},",
                "local_ipv4_addresses:{trigger_detection:function(){},retrieve:function(){return []; }},",
                "computer_screen:{width:function(){return 0;},height:function(){return 0;},color_depth:function(){return 0;},device_pixel_ratio:function(){return 0;}},",
                "browser_window_size:{width:function(){return 1024;},height:function(){return 768;}},",
                "addons:{get_all_names:function(){return []; }},",
                "gmt_offset:function(){return '+00:00';},",
                "navigator:{",
                "platform:{value:function(){return null;},value_human_readable:function(){return null;}},",
                "oscpu:{value:function(){return null;}},",
                "vendor:{value:function(){return null;}},",
                "hardware_concurrency:{value:function(){return null;}},",
                "mime_types:{all:function(){return null;}},",
                "navigator_ram:{gigabytes:function(){return null;}},",
                "max_touch_points:{value:function(){return null;}}",
                "},",
                "webgl:{vendor:function(){return null;},renderer:function(){return null;}},",
                "client_hints:{frontend:{available:function(){return false;},architecture:function(){return null;},bitness:function(){return null;},brands_string:function(){return null;},model:function(){return null;},platform:function(){return null;},platform_version:function(){return null;},ua_full_version:function(){return null;}}},",
                "looks_like:{",
                "software:{detection_tests:function(){return {};},detection_result:function(){return null;}},",
                "operating_system:{detection_tests:function(){return {};},detection_result:function(){return null;}},",
                "simple_software_string:function(){return 'FenBrowser';}",
                "},",
                "ecma:function(){return null;}",
                "}};",
                LoadWhatIsMyBrowserDoCapabilitiesDetectionScript());
        }

        private static string LoadWhatIsMyBrowserDoCapabilitiesDetectionScript()
            => LoadFixtureFromRepoRoot("tmp_do_capabilities_detection.js");

        private static string LoadWhatIsMyBrowserSiteMinScript()
            => LoadFixtureFromRepoRoot("tmp_whatismybrowser_site.min.js");

        private static string LoadFixtureFromRepoRoot(string fileName)
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }

                current = current.Parent;
            }

            throw new FileNotFoundException($"Could not locate {fileName} from the test runtime directory.");
        }
    }
}
