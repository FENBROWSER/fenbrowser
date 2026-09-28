using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    // Shares the static JsDialogBridge with FenJsPopupSecurityTests.
    [Collection("Engine Tests")]
    public sealed class FenJsInputEventDispatchTests
    {
        [Fact]
        public async Task DispatchEventForElement_DeliversDoubleClickContextMenuAndPointerPayload()
        {
            var baseUri = new Uri("https://example.com/input-events.html");
            var document = new HtmlParser(
                """
                <html><body>
                  <button id="dblClickBtn">Double click</button>
                  <button id="rightClickBtn">Right click / context menu</button>
                  <div id="clickTarget"></div>
                  <script>
                    globalThis.__dblClicks = 0;
                    globalThis.__contextDefaultPrevented = false;
                    globalThis.__contextButton = -1;
                    globalThis.__lastPointer = "none";
                    globalThis.__lastPointerMove = "none";

                    document.getElementById("dblClickBtn").addEventListener("dblclick", function () {
                      globalThis.__dblClicks++;
                    });
                    document.getElementById("rightClickBtn").addEventListener("contextmenu", function (event) {
                      event.preventDefault();
                      globalThis.__contextDefaultPrevented = event.defaultPrevented;
                      globalThis.__contextButton = event.button;
                    });
                    document.getElementById("clickTarget").addEventListener("pointerdown", function (event) {
                      globalThis.__lastPointer = "down " + event.pointerType + ":" + event.button + ":" + event.clientX + "," + event.clientY;
                    });
                    document.getElementById("clickTarget").addEventListener("pointermove", function (event) {
                      globalThis.__lastPointerMove = event.pointerType + ":" + event.clientX + "," + event.clientY;
                    });
                  </script>
                </body></html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var dblClickButton = document.GetElementById("dblClickBtn");
            var rightClickButton = document.GetElementById("rightClickBtn");
            var clickTarget = document.GetElementById("clickTarget");

            Assert.True(engine.DispatchEventForElement(
                dblClickButton,
                "dblclick",
                new BrowserDomEventInit { ClientX = 12, ClientY = 20, Button = 0 }));

            var contextDefaultAllowed = engine.DispatchEventForElement(
                rightClickButton,
                "contextmenu",
                new BrowserDomEventInit { ClientX = 34, ClientY = 45, Button = 2 });

            Assert.True(engine.DispatchEventForElement(
                clickTarget,
                "pointerdown",
                new BrowserDomEventInit
                {
                    ClientX = 56,
                    ClientY = 67,
                    Button = 0,
                    Buttons = 1,
                    PointerType = "mouse",
                    Pressure = 0.5
                }));
            Assert.True(engine.DispatchEventForElement(
                clickTarget,
                "pointermove",
                new BrowserDomEventInit
                {
                    ClientX = 89,
                    ClientY = 91,
                    PointerType = "mouse",
                    IsPrimary = true
                }));

            Assert.Equal("1", engine.Evaluate("String(globalThis.__dblClicks)")?.ToString());
            Assert.False(contextDefaultAllowed);
            Assert.Equal("true", engine.Evaluate("String(globalThis.__contextDefaultPrevented)")?.ToString());
            Assert.Equal("2", engine.Evaluate("String(globalThis.__contextButton)")?.ToString());
            Assert.Equal("down mouse:0:56,67", engine.Evaluate("globalThis.__lastPointer")?.ToString());
            Assert.Equal("mouse:89,91", engine.Evaluate("globalThis.__lastPointerMove")?.ToString());
        }

        [Fact]
        public async Task DispatchEventForElement_EventListenerCanAccessFreshClassList()
        {
            var baseUri = new Uri("https://example.com/input-classlist.html");
            var document = new HtmlParser(
                """
                <html><body>
                  <button id="target" class="a b">Target</button>
                  <script>
                    globalThis.__classListFromInput = "unset";
                    document.getElementById("target").addEventListener("mousedown", function (event) {
                      var list = event.currentTarget.classList;
                      globalThis.__classListFromInput = list.contains("a") + ":" + (list instanceof DOMTokenList);
                    });
                  </script>
                </body></html>
                """,
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var target = document.GetElementById("target");
            var dispatch = Task.Run(() => engine.DispatchEventForElement(
                target,
                "mousedown",
                new BrowserDomEventInit { ClientX = 12, ClientY = 20, Button = 0 }));

            var completed = await Task.WhenAny(dispatch, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(dispatch, completed);
            Assert.True(await dispatch);
            Assert.Equal("true:true", engine.Evaluate("globalThis.__classListFromInput")?.ToString());
        }

        [Fact]
        public async Task BrowserUiApis_ExposeNotificationPopupAndDialogContracts()
        {
            var baseUri = new Uri("file:///C:/tests/browser-ui-api.html");
            var document = new HtmlParser(
                """
                <html><body>
                  <dialog id="testDialog"></dialog>
                  <button id="openPopup">Open</button>
                </body></html>
                """,
                baseUri).Parse();

            // window.open needs transient activation while popup blocking is on (the
            // default), and reaches the embedder through JsDialogBridge; the popup's
            // written markup comes back through FinalizePopupDocument on close().
            var originalOpenWindow = JsDialogBridge.OpenWindow;
            var originalFinalize = JsDialogBridge.FinalizePopupDocument;
            string popupHtml = null;
            JsDialogBridge.OpenWindow = (_, _, _) => new object();
            JsDialogBridge.FinalizePopupDocument = (_, html) => popupHtml = html;
            try
            {
                var engine = new FenJsBrowserScriptEngine(CreateHost())
                {
                    Sandbox = SandboxPolicy.AllowAll
                };

                await engine.SetDomAsync(document.DocumentElement, baseUri);

                engine.Evaluate(
                    """
                    globalThis.__apiProbe = {};
                    __apiProbe.notificationType = typeof Notification;
                    __apiProbe.notificationPermission = Notification.permission;
                    __apiProbe.secureContext = String(window.isSecureContext);
                    Notification.requestPermission(function (permission) {
                      __apiProbe.requestPermission = permission;
                    });
                    var notification = new Notification("Probe", { body: "Body", tag: "tag" });
                    __apiProbe.notificationTitle = notification.title;

                    document.getElementById("openPopup").addEventListener("click", function () {
                      var popup = window.open("", "ProbePopup", "width=320,height=200");
                      __apiProbe.popupOpened = !!popup;
                      popup.document.open();
                      popup.document.write("<h1>Popup</h1>");
                      popup.document.close();
                      popup.focus();
                    });

                    var dialog = document.getElementById("testDialog");
                    __apiProbe.dialogShowModalType = typeof dialog.showModal;
                    __apiProbe.dialogCloseType = typeof dialog.close;
                    __apiProbe.dialogClosed = false;
                    dialog.addEventListener("close", function () {
                      __apiProbe.dialogClosed = true;
                    });
                    dialog.showModal();
                    __apiProbe.dialogOpenAfterShow = dialog.open && dialog.hasAttribute("open");
                    dialog.close("ok");
                    __apiProbe.dialogOpenAfterClose = dialog.open;
                    """);

                var openButton = Assert.IsType<Element>(document.GetElementById("openPopup"));
                Assert.True(engine.DispatchEventForElement(openButton, "click", new BrowserDomEventInit { IsTrusted = true }));

                Assert.Equal("function", engine.Evaluate("String(__apiProbe.notificationType)")?.ToString());
                Assert.Equal("granted", engine.Evaluate("String(__apiProbe.notificationPermission)")?.ToString());
                Assert.Equal("granted", engine.Evaluate("String(__apiProbe.requestPermission)")?.ToString());
                Assert.Equal("true", engine.Evaluate("String(__apiProbe.secureContext)")?.ToString());
                Assert.Equal("Probe", engine.Evaluate("String(__apiProbe.notificationTitle)")?.ToString());
                Assert.Equal("true", engine.Evaluate("String(__apiProbe.popupOpened)")?.ToString());
                Assert.Equal("<h1>Popup</h1>", popupHtml);
                Assert.Equal("function", engine.Evaluate("String(__apiProbe.dialogShowModalType)")?.ToString());
                Assert.Equal("function", engine.Evaluate("String(__apiProbe.dialogCloseType)")?.ToString());
                Assert.Equal("true", engine.Evaluate("String(__apiProbe.dialogOpenAfterShow)")?.ToString());
                Assert.Equal("false", engine.Evaluate("String(__apiProbe.dialogOpenAfterClose)")?.ToString());
                Assert.Equal("true", engine.Evaluate("String(__apiProbe.dialogClosed)")?.ToString());
            }
            finally
            {
                JsDialogBridge.OpenWindow = originalOpenWindow;
                JsDialogBridge.FinalizePopupDocument = originalFinalize;
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
