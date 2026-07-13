using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    public sealed class FenJsAsyncScriptRenderTests
    {
        [Fact]
        public async Task ParserDiscoveredAsyncExternalScript_RequestsRenderAfterHydration()
        {
            var baseUri = new Uri("https://www.google.com/sorry/index");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <form id="captcha-form">
                      <div id="recaptcha" class="g-recaptcha"></div>
                      <script src="/recaptcha/enterprise.js" async defer></script>
                    </form>
                  </body>
                </html>
                """,
                baseUri).Parse();

            var repaintRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                AllowExternalScripts = true,
                RequestRender = () => repaintRequested.TrySetResult(true),
                ExternalScriptFetcher = static (_, _) => Task.FromResult(
                    "var mount = document.getElementById('recaptcha');" +
                    "var frame = document.createElement('iframe');" +
                    "frame.setAttribute('title', 'reCAPTCHA');" +
                    "mount.appendChild(frame);" +
                    "globalThis.__captchaHydrated = mount.getElementsByTagName('iframe').length;")
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var completed = await Task.WhenAny(repaintRequested.Task, Task.Delay(1000));

            Assert.Same(repaintRequested.Task, completed);
            Assert.Equal("1", engine.Evaluate("String(globalThis.__captchaHydrated)")?.ToString());
            Assert.Equal("1", engine.Evaluate("String(document.getElementById('recaptcha').getElementsByTagName('iframe').length)")?.ToString());
        }

        [Fact]
        public async Task AsyncExternalLoader_InsertBeforeDynamicExternalScript_HydratesBeforeRender()
        {
            var baseUri = new Uri("https://www.google.com/sorry/index");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <form id="captcha-form">
                      <div id="recaptcha" class="g-recaptcha"></div>
                      <script src="/recaptcha/enterprise.js" async defer></script>
                    </form>
                  </body>
                </html>
                """,
                baseUri).Parse();

            var repaintRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                AllowExternalScripts = true,
                RequestRender = () => repaintRequested.TrySetResult(true),
                ExternalScriptFetcher = static (uri, _) =>
                {
                    if (uri.AbsolutePath.EndsWith("/enterprise.js", StringComparison.Ordinal))
                    {
                        return Task.FromResult(
                            "var d=document,po=d.createElement('script');" +
                            "po.type='text/javascript';" +
                            "po.async=true;" +
                            "po.charset='utf-8';" +
                            "po.src='https://www.gstatic.com/recaptcha/releases/test/recaptcha__en.js';" +
                            "var s=d.getElementsByTagName('script')[0];" +
                            "s.parentNode.insertBefore(po,s);");
                    }

                    if (uri.AbsolutePath.EndsWith("/recaptcha__en.js", StringComparison.Ordinal))
                    {
                        return Task.FromResult(
                            "var mount=document.getElementById('recaptcha');" +
                            "var frame=document.createElement('iframe');" +
                            "frame.setAttribute('title','reCAPTCHA');" +
                            "mount.appendChild(frame);" +
                            "globalThis.__captchaHydrated=mount.getElementsByTagName('iframe').length;");
                    }

                    return Task.FromResult(string.Empty);
                }
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var completed = await Task.WhenAny(repaintRequested.Task, Task.Delay(1000));

            Assert.Same(repaintRequested.Task, completed);
            Assert.Equal("1", engine.Evaluate("String(globalThis.__captchaHydrated)")?.ToString());
            Assert.Equal("1", engine.Evaluate("String(document.getElementById('recaptcha').getElementsByTagName('iframe').length)")?.ToString());
        }

        [Fact]
        public async Task AsyncExternalLoader_RecaptchaPlatformApis_HydrateChallengeFrame()
        {
            var baseUri = new Uri("https://www.google.com/sorry/index");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <form id="captcha-form">
                      <div id="recaptcha" class="g-recaptcha"><span id="existing"></span></div>
                      <script src="/recaptcha/enterprise.js" async defer></script>
                    </form>
                  </body>
                </html>
                """,
                baseUri).Parse();

            var repaintRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                AllowExternalScripts = true,
                RequestRender = () => repaintRequested.TrySetResult(true),
                ExternalScriptFetcher = static (uri, _) =>
                {
                    if (uri.AbsolutePath.EndsWith("/enterprise.js", StringComparison.Ordinal))
                    {
                        return Task.FromResult(
                            "var d=document,po=d.createElement('script');" +
                            "po.async=true;" +
                            "po.src='https://www.gstatic.com/recaptcha/releases/test/recaptcha__en.js';" +
                            "d.getElementsByTagName('script')[0].parentNode.insertBefore(po,d.getElementsByTagName('script')[0]);");
                    }

                    if (uri.AbsolutePath.EndsWith("/recaptcha__en.js", StringComparison.Ordinal))
                    {
                        return Task.FromResult(
                            "var ev=document.createEvent('Event');" +
                            "ev.initEvent('challenge-ready',true,true);" +
                            "globalThis.__eventType=ev.type;" +
                            "globalThis.__storagePromiseType=typeof document.hasStorageAccess().then;" +
                            "globalThis.__requestStoragePromiseType=typeof document.requestStorageAccess().then;" +
                            "document.requestStorageAccess().then(function(){globalThis.__requestStorageResolved='yes';});" +
                            "globalThis.__postMessageType=typeof postMessage;" +
                            "try { postMessage('captcha-ready','*'); globalThis.__postMessageResult='ok'; } catch(e) { globalThis.__postMessageResult=e.name + ':' + e.message; }" +
                            "globalThis.__scrollingElementTag=document.scrollingElement.tagName;" +
                            "var textNode=document.createTextNode('x');" +
                            "globalThis.__characterDataSurface=String(textNode.id) + ':' + String(textNode.localName);" +
                            "var channel=new MessageChannel();" +
                            "globalThis.__messagePortType=typeof channel.port1.postMessage;" +
                            "var worker=new Worker('blob:recaptcha');" +
                            "globalThis.__workerSurface=typeof Worker + ':' + typeof worker.postMessage + ':' + typeof worker.terminate + ':' + typeof worker.addEventListener;" +
                            "worker.postMessage('probe');" +
                            "worker.terminate();" +
                            "var mount=document.getElementById('recaptcha');" +
                            "var frame=document.createElement('iframe');" +
                            "frame.tabIndex=0;" +
                            "globalThis.__frameTabIndex=String(frame.tabIndex);" +
                            "globalThis.__frameCurrentStyleType=typeof frame.currentStyle.getPropertyValue;" +
                            "frame.setAttribute('title','reCAPTCHA');" +
                            "mount.prepend(frame);" +
                            "globalThis.__captchaHydrated=mount.getElementsByTagName('iframe').length;" +
                            "globalThis.__firstChildTag=mount.firstElementChild.tagName;");
                    }

                    return Task.FromResult(string.Empty);
                }
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var completed = await Task.WhenAny(repaintRequested.Task, Task.Delay(1000));

            Assert.Same(repaintRequested.Task, completed);
            await WaitForEngineValueAsync(
                engine,
                "String(globalThis.__eventType)",
                "challenge-ready");
            Assert.Equal("challenge-ready", engine.Evaluate("String(globalThis.__eventType)")?.ToString());
            Assert.Equal("function", engine.Evaluate("String(globalThis.__storagePromiseType)")?.ToString());
            Assert.Equal("function", engine.Evaluate("String(globalThis.__requestStoragePromiseType)")?.ToString());
            Assert.Equal("yes", engine.Evaluate("String(globalThis.__requestStorageResolved)")?.ToString());
            Assert.Equal("function", engine.Evaluate("String(globalThis.__postMessageType)")?.ToString());
            Assert.Equal("ok", engine.Evaluate("String(globalThis.__postMessageResult)")?.ToString());
            Assert.Equal("HTML", engine.Evaluate("String(globalThis.__scrollingElementTag)")?.ToString());
            Assert.Equal("undefined:undefined", engine.Evaluate("String(globalThis.__characterDataSurface)")?.ToString());
            Assert.Equal("function", engine.Evaluate("String(globalThis.__messagePortType)")?.ToString());
            Assert.Equal("function:function:function:function", engine.Evaluate("String(globalThis.__workerSurface)")?.ToString());
            Assert.Equal("0", engine.Evaluate("String(globalThis.__frameTabIndex)")?.ToString());
            Assert.Equal("function", engine.Evaluate("String(globalThis.__frameCurrentStyleType)")?.ToString());
            Assert.Equal("1", engine.Evaluate("String(globalThis.__captchaHydrated)")?.ToString());
            Assert.Equal("IFRAME", engine.Evaluate("String(globalThis.__firstChildTag)")?.ToString());
        }

        [Fact]
        public async Task AsyncExternalLoader_RecaptchaAutoRenderDomSurface_HydratesChallengeFrame()
        {
            var baseUri = new Uri("https://www.google.com/sorry/index");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <form id="captcha-form">
                      <div id="recaptcha" class="g-recaptcha"><span id="existing"></span></div>
                      <script src="/recaptcha/enterprise.js" async defer></script>
                    </form>
                  </body>
                </html>
                """,
                baseUri).Parse();

            var repaintRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                AllowExternalScripts = true,
                RequestRender = () => repaintRequested.TrySetResult(true),
                ExternalScriptFetcher = static (uri, _) =>
                {
                    if (uri.AbsolutePath.EndsWith("/enterprise.js", StringComparison.Ordinal))
                    {
                        return Task.FromResult(
                            "var d=document,po=d.createElement('script');" +
                            "po.async=true;" +
                            "po.src='https://www.gstatic.com/recaptcha/releases/test/recaptcha__en.js';" +
                            "d.getElementsByTagName('script')[0].parentNode.insertBefore(po,d.getElementsByTagName('script')[0]);");
                    }

                    if (uri.AbsolutePath.EndsWith("/recaptcha__en.js", StringComparison.Ordinal))
                    {
                        return Task.FromResult(
                            "var candidates=document.getElementsByClassName('g-recaptcha');" +
                            "Array.prototype.forEach.call(candidates,function(mount){" +
                            "  if (!mount || mount.nodeType !== 1) throw Error('placeholder:' + typeof mount + ':' + typeof (mount && mount.nodeType) + ':' + String(mount && mount.nodeType) + ':' + String(mount && mount.nodeType === 1));" +
                            "  var stale=mount.firstChild;" +
                            "  if (stale) mount.removeChild(stale);" +
                            "  var frame=document.createElement('iframe');" +
                            "  if (typeof frame.contentWindow !== 'object') throw Error('contentWindow');" +
                            "  if (typeof frame.contentDocument !== 'object') throw Error('contentDocument');" +
                            "  frame.setAttribute('title','reCAPTCHA');" +
                            "  mount.appendChild(frame);" +
                            "});" +
                            "globalThis.__captchaHydrated=document.getElementById('recaptcha').getElementsByTagName('iframe').length;" +
                            "globalThis.__captchaChildren=document.getElementById('recaptcha').childNodes.length;");
                    }

                    return Task.FromResult(string.Empty);
                }
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var completed = await Task.WhenAny(repaintRequested.Task, Task.Delay(1000));

            Assert.Same(repaintRequested.Task, completed);
            Assert.Equal("1", engine.Evaluate("String(globalThis.__captchaHydrated)")?.ToString());
            Assert.Equal("1", engine.Evaluate("String(globalThis.__captchaChildren)")?.ToString());
        }

        [Fact]
        public async Task IFrameContentWindowPostMessage_DeliversMessageEventsToFrameFacade()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <iframe id="challenge" src="https://child.test/frame"></iframe>
                  </body>
                </html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            engine.Evaluate(
                "var frame=document.getElementById('challenge');" +
                "globalThis.__frameMessageCount=0;" +
                "frame.contentWindow.onmessage=function(event){" +
                "  globalThis.__frameOnMessage=String(event.data);" +
                "};" +
                "frame.contentWindow.addEventListener('message',function(event){" +
                "  globalThis.__frameMessageCount++;" +
                "  globalThis.__frameMessage=String(event.data)+'|'+event.origin+'|'+String(event.source===window)+'|'+String(event.target===frame.contentWindow)+'|'+String(event.currentTarget===frame.contentWindow);" +
                "});" +
                "frame.contentWindow.postMessage('blocked','https://other.test');" +
                "frame.contentWindow.postMessage('captcha-command','https://child.test');");

            for (var i = 0; i < 20; i++)
            {
                if (engine.Evaluate("String(globalThis.__frameMessageCount || 0)")?.ToString() == "1")
                {
                    break;
                }

                await Task.Delay(25);
            }

            Assert.Equal("1", engine.Evaluate("String(globalThis.__frameMessageCount)")?.ToString());
            Assert.Equal("captcha-command", engine.Evaluate("String(globalThis.__frameOnMessage)")?.ToString());
            Assert.Equal(
                "captcha-command|https://parent.test|false|true|true",
                engine.Evaluate("String(globalThis.__frameMessage)")?.ToString());
        }

        [Fact]
        public async Task SetSubdocumentDomAsync_PreservesParentWindowListenersAndRestoresDocument()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <div id="top-marker"></div>
                    <iframe id="child" src="https://child.test/frame"></iframe>
                    <script>
                      window.__parentMessages = 0;
                      window.addEventListener('message', function(event) {
                        window.__parentMessages++;
                        window.__lastParentMessage = String(event.data) + '|' + document.getElementById('top-marker').id;
                      });
                    </script>
                  </body>
                </html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var frameUri = new Uri("https://child.test/frame");
            var frameDocument = new HtmlParser(
                """
                <html>
                      <body id="frame-body">
                        <script>
                          document.body.className += ' js-enabled';
                          globalThis.__frameScriptSawDocument = document.body.id;
                          globalThis.__frameCookieLabelType = typeof navigator.cookieDeprecationLabel.getValue;
                          globalThis.__frameSendBeaconType = typeof navigator.sendBeacon;
                          globalThis.__frameWindowPostMessageType = typeof window.postMessage;
                      globalThis.__frameWindowDocumentBody = window.document.body.id;
                      globalThis.__frameWindowIsSelf = String(window === self);
                      window.__frameMessages = 0;
                      window.addEventListener('message', function(event) {
                        window.__frameMessages++;
                        window.__lastFrameMessage = String(event.data) + '|' + event.origin + '|' + String(event.currentTarget === window);
                      });
                    </script>
                  </body>
                </html>
                """,
                frameUri).Parse();

            var frameElement = Assert.IsType<Element>(document.GetElementById("child"));
            frameElement.AppendChild(frameDocument);
            await engine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);

            Assert.Contains("js-enabled", frameDocument.Body.ClassName);
            Assert.Equal("frame-body", engine.Evaluate("String(globalThis.__frameScriptSawDocument)")?.ToString());
            Assert.Equal("function", engine.Evaluate("String(globalThis.__frameCookieLabelType)")?.ToString());
            Assert.Equal("function", engine.Evaluate("String(globalThis.__frameSendBeaconType)")?.ToString());
            Assert.Equal("function", engine.Evaluate("String(globalThis.__frameWindowPostMessageType)")?.ToString());
            Assert.Equal("frame-body", engine.Evaluate("String(globalThis.__frameWindowDocumentBody)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(globalThis.__frameWindowIsSelf)")?.ToString());
            Assert.Equal("top-marker", engine.Evaluate("document.getElementById('top-marker').id")?.ToString());

            engine.Evaluate(
                "document.getElementById('child').contentWindow.postMessage('frame-command','https://child.test');");
            for (var i = 0; i < 20; i++)
            {
                if (engine.Evaluate("String(document.getElementById('child').contentWindow.__frameMessages || 0)")?.ToString() == "1")
                {
                    break;
                }

                await Task.Delay(25);
            }

            Assert.Equal(
                "frame-command|https://parent.test|true",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__lastFrameMessage)")?.ToString());

            engine.Evaluate("postMessage('alive-after-frame','*');");
            for (var i = 0; i < 20; i++)
            {
                if (engine.Evaluate("String(globalThis.__parentMessages || 0)")?.ToString() == "1")
                {
                    break;
                }

                await Task.Delay(25);
            }

            Assert.Equal("1", engine.Evaluate("String(globalThis.__parentMessages)")?.ToString());
            Assert.Equal(
                "alive-after-frame|top-marker",
                engine.Evaluate("String(globalThis.__lastParentMessage)")?.ToString());
        }

        [Fact]
        public async Task SetSubdocumentDomAsync_LoadListenerExceptionDoesNotAbortFrameLifecycle()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <iframe id="child" src="https://child.test/frame"></iframe>
                  </body>
                </html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var frameUri = new Uri("https://child.test/frame");
            var frameDocument = new HtmlParser(
                """
                <html>
                  <body id="frame-body">
                    <script>
                      window.__frameLoadCount = 0;
                      window.addEventListener('load', function() {
                        throw new TypeError('frame load boom');
                      });
                      window.addEventListener('load', function() {
                        window.__frameLoadCount++;
                        window.__frameLoadDocumentBody = document.body.id;
                      });
                    </script>
                  </body>
                </html>
                """,
                frameUri).Parse();

            var frameElement = Assert.IsType<Element>(document.GetElementById("child"));
            frameElement.AppendChild(frameDocument);
            await engine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);

            Assert.Equal(
                "1",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__frameLoadCount)")?.ToString());
            Assert.Equal(
                "frame-body",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__frameLoadDocumentBody)")?.ToString());
            Assert.Equal("complete", engine.Evaluate("document.readyState")?.ToString());
        }

        [Fact]
        public async Task FrameElementClickHandler_PostsToParentWithFrameSourceAndOrigin()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <div id="top-marker"></div>
                    <iframe id="child" src="https://child.test/frame"></iframe>
                    <script>
                      window.__parentMessages = 0;
                      window.addEventListener('message', function(event) {
                        var frame = document.getElementById('child');
                        window.__parentMessages++;
                        window.__lastParentMessage =
                          String(event.data) + '|' +
                          event.origin + '|' +
                          String(event.source === frame.contentWindow) + '|' +
                          String(event.currentTarget === window) + '|' +
                          document.getElementById('top-marker').id;
                      });
                    </script>
                  </body>
                </html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var frameUri = new Uri("https://child.test/frame");
            var frameDocument = new HtmlParser(
                """
                <html>
                      <body id="frame-body">
                        <button id="send" type="button">Send</button>
                        <script>
                          document.getElementById('send').addEventListener('click', function (event) {
                            var path = event.composedPath();
                            window.__handlerEventShape =
                              String(event.isTrusted) + '|' +
                              String(event.composed) + '|' +
                              String(typeof event.composedPath) + '|' +
                              String(path[0] === document.getElementById('send')) + '|' +
                              String(path.indexOf(window) >= 0);
                            window.__handlerWindowIsSelf = String(window === self);
                            window.__handlerDocumentBody = document.body.id;
                            window.__handlerParentShape =
                              String(typeof parent.postMessage) + '|' +
                              String(parent === top) + '|' +
                              String(parent === window);
                            try {
                              parent.postMessage('from-frame', 'https://parent.test');
                              window.__handlerPostMessageResult = 'sent';
                            } catch (err) {
                              window.__handlerPostMessageResult = String(err && err.message || err);
                            }
                          });
                        </script>
                  </body>
                </html>
                """,
                frameUri).Parse();

            var frameElement = Assert.IsType<Element>(document.GetElementById("child"));
            frameElement.AppendChild(frameDocument);
            await engine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);

            var sendButton = Assert.IsType<Element>(frameDocument.GetElementById("send"));
            Assert.True(engine.DispatchEventForElement(sendButton, "click"));
            Assert.Equal(
                "function|true|false",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__handlerParentShape)")?.ToString());
            Assert.Equal(
                "sent",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__handlerPostMessageResult)")?.ToString());

            for (var i = 0; i < 20; i++)
            {
                if (engine.Evaluate("String(globalThis.__parentMessages || 0)")?.ToString() == "1")
                {
                    break;
                }

                await Task.Delay(25);
            }

            Assert.Equal(
                "from-frame|https://child.test|true|true|top-marker",
                engine.Evaluate("String(globalThis.__lastParentMessage)")?.ToString());
            Assert.Equal(
                "true",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__handlerWindowIsSelf)")?.ToString());
            Assert.Equal(
                "frame-body",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__handlerDocumentBody)")?.ToString());
            Assert.Equal(
                "true|true|function|true|true",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__handlerEventShape)")?.ToString());
        }

        [Fact]
        public async Task FrameClickScheduledTimer_RunsWithFrameWindowContext()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <div id="top-marker"></div>
                    <iframe id="child" src="https://child.test/frame"></iframe>
                    <script>
                      window.__parentMessages = 0;
                      window.addEventListener('message', function(event) {
                        var frame = document.getElementById('child');
                        window.__parentMessages++;
                        window.__lastParentMessage =
                          String(event.data) + '|' +
                          event.origin + '|' +
                          String(event.source === frame.contentWindow) + '|' +
                          document.getElementById('top-marker').id;
                      });
                    </script>
                  </body>
                </html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var frameUri = new Uri("https://child.test/frame");
            var frameDocument = new HtmlParser(
                """
                <html>
                  <body id="frame-body">
                    <button id="send" type="button">Send</button>
                    <script>
                      document.getElementById('send').addEventListener('click', function () {
                        setTimeout(function () {
                          window.__timerDocumentBody = document.body.id;
                          window.__timerLocation = location.href;
                          window.__timerThisIsWindow = String(this === window);
                          parent.postMessage('timer:' + document.body.id + ':' + location.href, 'https://parent.test');
                        }, 0);
                      });
                    </script>
                  </body>
                </html>
                """,
                frameUri).Parse();

            var frameElement = Assert.IsType<Element>(document.GetElementById("child"));
            frameElement.AppendChild(frameDocument);
            await engine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);

            var sendButton = Assert.IsType<Element>(frameDocument.GetElementById("send"));
            Assert.True(engine.DispatchEventForElement(sendButton, "click"));

            for (var i = 0; i < 20; i++)
            {
                if (engine.Evaluate("String(globalThis.__parentMessages || 0)")?.ToString() == "1")
                {
                    break;
                }

                await Task.Delay(25);
            }

            Assert.Equal(
                "timer:frame-body:https://child.test/frame|https://child.test|true|top-marker",
                engine.Evaluate("String(globalThis.__lastParentMessage)")?.ToString());
            Assert.Equal(
                "frame-body",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__timerDocumentBody)")?.ToString());
            Assert.Equal(
                "https://child.test/frame",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__timerLocation)")?.ToString());
            Assert.Equal(
                "true",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__timerThisIsWindow)")?.ToString());
        }

        [Fact]
        public async Task FrameClickPromiseMicrotask_RunsBeforeInputEventCompletes()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <div id="top-marker"></div>
                    <iframe id="child" src="https://child.test/frame"></iframe>
                    <script>
                      window.__parentMessages = 0;
                      window.addEventListener('message', function(event) {
                        var frame = document.getElementById('child');
                        window.__parentMessages++;
                        window.__lastParentMessage =
                          String(event.data) + '|' +
                          event.origin + '|' +
                          String(event.source === frame.contentWindow) + '|' +
                          document.getElementById('top-marker').id;
                      });
                    </script>
                  </body>
                </html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var frameUri = new Uri("https://child.test/frame");
            var frameDocument = new HtmlParser(
                """
                <html>
                  <body id="frame-body">
                    <button id="send" type="button">Send</button>
                    <script>
                      document.getElementById('send').addEventListener('click', function () {
                        Promise.resolve().then(function () {
                          window.__microtaskDocumentBody = document.body.id;
                          window.__microtaskLocation = location.href;
                          parent.postMessage('microtask:' + document.body.id + ':' + location.href, 'https://parent.test');
                        });
                      });
                    </script>
                  </body>
                </html>
                """,
                frameUri).Parse();

            var frameElement = Assert.IsType<Element>(document.GetElementById("child"));
            frameElement.AppendChild(frameDocument);
            await engine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);

            var sendButton = Assert.IsType<Element>(frameDocument.GetElementById("send"));
            Assert.True(engine.DispatchEventForElement(sendButton, "click"));

            for (var i = 0; i < 20; i++)
            {
                if (engine.Evaluate("String(globalThis.__parentMessages || 0)")?.ToString() == "1")
                {
                    break;
                }

                await Task.Delay(25);
            }

            Assert.Equal(
                "microtask:frame-body:https://child.test/frame|https://child.test|true|top-marker",
                engine.Evaluate("String(globalThis.__lastParentMessage)")?.ToString());
            Assert.Equal(
                "frame-body",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__microtaskDocumentBody)")?.ToString());
            Assert.Equal(
                "https://child.test/frame",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__microtaskLocation)")?.ToString());
        }

        [Fact]
        public async Task FrameClickDispatch_RunsWindowAndDocumentCaptureBeforeTarget()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <iframe id="child" src="https://child.test/frame"></iframe>
                  </body>
                </html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var frameUri = new Uri("https://child.test/frame");
            var frameDocument = new HtmlParser(
                """
                <html>
                  <body>
                    <button id="send" type="button">Send</button>
                    <script>
                      window.__events = [];
                      window.addEventListener('click', function (event) {
                        window.__events.push('win-capture:' + event.eventPhase + ':' + String(event.currentTarget === window));
                      }, true);
                      var docCaptureListener = {
                        label: 'doc-object',
                        handleEvent: function (event) {
                          window.__events.push('doc-capture:' + event.eventPhase + ':' + this.label);
                        }
                      };
                      document.addEventListener('click', docCaptureListener, { capture: true });
                      document.getElementById('send').addEventListener('click', function (event) {
                        window.__events.push('target:' + event.eventPhase + ':' + String(event.currentTarget === this));
                        event.preventDefault();
                      });
                      document.addEventListener('click', function (event) {
                        window.__events.push('doc-bubble:' + event.eventPhase + ':' + document.body.tagName);
                      });
                      window.addEventListener('click', function (event) {
                        window.__events.push('win-bubble:' + event.eventPhase + ':' + String(event.currentTarget === window));
                      });
                    </script>
                  </body>
                </html>
                """,
                frameUri).Parse();

            var frameElement = Assert.IsType<Element>(document.GetElementById("child"));
            frameElement.AppendChild(frameDocument);
            await engine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);

            var sendButton = Assert.IsType<Element>(frameDocument.GetElementById("send"));
            Assert.False(engine.DispatchEventForElement(sendButton, "click"));

            Assert.Equal(
                "win-capture:1:true,doc-capture:1:doc-object,target:2:true,doc-bubble:3:BODY,win-bubble:3:true",
                engine.Evaluate("String(document.getElementById('child').contentWindow.__events.join(','))")?.ToString());
        }

        [Fact]
        public async Task ElementGetElementsByClassName_ReturnsScopedCollection()
        {
            var baseUri = new Uri("https://www.google.com/recaptcha/enterprise/anchor");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <div id="host">
                      <span class="target">one</span>
                      <span class="target other">two</span>
                    </div>
                    <span class="target">outside</span>
                    <script>
                      var items = document.getElementById('host').getElementsByClassName('target');
                      globalThis.__scopedLength = String(items.length);
                      globalThis.__firstText = items[0].textContent;
                      globalThis.__secondText = items.item(1).textContent;
                    </script>
                  </body>
                </html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                AllowExternalScripts = true
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("2", engine.Evaluate("String(globalThis.__scopedLength)")?.ToString());
            Assert.Equal("one", engine.Evaluate("String(globalThis.__firstText)")?.ToString());
            Assert.Equal("two", engine.Evaluate("String(globalThis.__secondText)")?.ToString());
        }

        [Fact]
        public async Task GetComputedStyle_ExposesGetPropertyValue()
        {
            var baseUri = new Uri("https://www.google.com/recaptcha/enterprise/anchor");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <span id="probe">x</span>
                    <span id="unstyled">y</span>
                    <input id="token" value="from-attribute">
                    <script>
                      var style = getComputedStyle(document.getElementById('probe'));
                      globalThis.__direction = style.getPropertyValue('direction');
                      globalThis.__fontSize = style.getPropertyValue('font-size');
                      globalThis.__currentDirection = document.getElementById('probe').currentStyle.getPropertyValue('direction');
                      globalThis.__emptyDirection = getComputedStyle(document.getElementById('unstyled')).getPropertyValue('direction');
                      var token = document.getElementById('token');
                      globalThis.__inputInitialValue = token.value;
                      token.value = 'from-property';
                      globalThis.__inputUpdatedValue = token.getAttribute('value') + ':' + token.value;
                    </script>
                  </body>
                </html>
                """,
                baseUri).Parse();
            var probe = document.GetElementById("probe");
            var cachedStyle = new CssComputed();
            cachedStyle.Map["direction"] = "rtl";
            cachedStyle.Map["font-size"] = "18px";
            probe.SetComputedStyle(cachedStyle);

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                AllowExternalScripts = true
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("rtl", engine.Evaluate("String(globalThis.__direction)")?.ToString());
            Assert.Equal("18px", engine.Evaluate("String(globalThis.__fontSize)")?.ToString());
            Assert.Equal("rtl", engine.Evaluate("String(globalThis.__currentDirection)")?.ToString());
            Assert.Equal(string.Empty, engine.Evaluate("String(globalThis.__emptyDirection)")?.ToString());
            Assert.Equal("from-attribute", engine.Evaluate("String(globalThis.__inputInitialValue)")?.ToString());
            Assert.Equal("from-property:from-property", engine.Evaluate("String(globalThis.__inputUpdatedValue)")?.ToString());
        }

        [Fact]
        public async Task IframeSrcSetBeforeAppend_QueuesFrameElementLoaderOnce()
        {
            var baseUri = new Uri("https://www.google.com/sorry/index");
            var document = new HtmlParser(
                """
                <html>
                  <body>
                    <script>
                      var frame = document.createElement('iframe');
                      frame.src = '/recaptcha/enterprise/anchor?k=site';
                      document.body.appendChild(frame);
                      frame.setAttribute('src', '/recaptcha/enterprise/anchor?k=site');
                    </script>
                  </body>
                </html>
                """,
                baseUri).Parse();

            var loaded = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loadCount = 0;
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                AllowExternalScripts = true,
                FrameElementLoader = (frame, uri) =>
                {
                    loadCount++;
                    loaded.TrySetResult(uri);
                    return Task.CompletedTask;
                }
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var completed = await Task.WhenAny(loaded.Task, Task.Delay(1000));

            Assert.Same(loaded.Task, completed);
            var loadedUri = await loaded.Task;
            Assert.Equal("https://www.google.com/recaptcha/enterprise/anchor?k=site", loadedUri.AbsoluteUri);
            await Task.Delay(100);
            Assert.Equal(1, loadCount);
        }

        private static JsHostAdapter CreateHost()
        {
            return new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: _ => { });
        }

        private static async Task WaitForEngineValueAsync(
            FenJsBrowserScriptEngine engine,
            string expression,
            string expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < deadline)
            {
                if (engine.Evaluate(expression)?.ToString() == expected)
                {
                    return;
                }

                await Task.Delay(25);
            }
        }
    }
}
