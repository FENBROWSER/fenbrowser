using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class FenJsPopupSecurityTests
{
    [Fact]
    public async Task WindowOpen_WithoutUserActivation_IsBlockedByDefault()
    {
        var bridgeCalls = 0;
        var result = await RunAsync(
            (engine, _) => engine.Evaluate("window.open('https://popup.test/') === null"),
            () =>
            {
                bridgeCalls++;
                return new object();
            },
            blockPopups: true);

        Assert.Equal("True", result);
        Assert.Equal(0, bridgeCalls);
    }

    [Fact]
    public async Task WindowOpen_DisallowedScheme_IsRejectedBeforeHostBridge()
    {
        var bridgeCalls = 0;
        var result = await RunAsync(
            (engine, _) => engine.Evaluate("window.open('javascript:alert(1)') === null"),
            () =>
            {
                bridgeCalls++;
                return new object();
            },
            blockPopups: false);

        Assert.Equal("True", result);
        Assert.Equal(0, bridgeCalls);
    }

    [Fact]
    public async Task WindowOpen_Noopener_OpensButDoesNotExposeOpenerHandle()
    {
        var bridgeCalls = 0;
        var result = await RunAsync(
            (engine, _) => engine.Evaluate("window.open('https://popup.test/', '_blank', 'noopener') === null"),
            () =>
            {
                bridgeCalls++;
                return new object();
            },
            blockPopups: false);

        Assert.Equal("True", result);
        Assert.Equal(1, bridgeCalls);
    }

    [Fact]
    public async Task WindowOpen_DuringTrustedClick_IsAllowedWithPopupBlockingEnabled()
    {
        var bridgeCalls = 0;
        var result = await RunAsync(
            (engine, document) =>
            {
                engine.Evaluate("window.__opened = false; document.getElementById('open').addEventListener('click', function () { window.__opened = window.open('https://popup.test/') !== null; });");
                var button = Assert.IsType<Element>(document.GetElementById("open"));
                Assert.True(engine.DispatchEventForElement(button, "click", new BrowserDomEventInit { IsTrusted = true }));
                return engine.Evaluate("String(window.__opened)");
            },
            () =>
            {
                bridgeCalls++;
                return new object();
            },
            blockPopups: true);

        Assert.Equal("true", result);
        Assert.Equal(1, bridgeCalls);
    }

    private static async Task<string> RunAsync(
        Func<FenJsBrowserScriptEngine, Document, object> action,
        Func<object> openWindow,
        bool blockPopups)
    {
        var originalBlockPopups = BrowserSettings.Instance.BlockPopups;
        var originalOpenWindow = JsDialogBridge.OpenWindow;
        try
        {
            BrowserScriptEngineRuntime.Reset();
            BrowserSettings.Instance.BlockPopups = blockPopups;
            JsDialogBridge.OpenWindow = (_, _, _) => openWindow();
            var baseUri = new Uri("https://fixture.test/popup-security.html");
            var document = new HtmlParser("<html><body><button id='open'>Open</button></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };
            await engine.SetDomAsync(document.DocumentElement, baseUri);
            return action(engine, document)?.ToString() ?? "null";
        }
        finally
        {
            JsDialogBridge.OpenWindow = originalOpenWindow;
            BrowserSettings.Instance.BlockPopups = originalBlockPopups;
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
