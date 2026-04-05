using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
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
