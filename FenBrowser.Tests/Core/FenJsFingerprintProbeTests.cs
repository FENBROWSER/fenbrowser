using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// The window and navigator surface that bot-detection scripts read first.
/// Every member here exists in mainstream browsers and is provided by the
/// engine; the subsystems it lacks (WebGL, WebAudio, WebRTC, WebAssembly,
/// shared workers, push, file pickers) are deliberately not listed, because
/// pretending to have them would break the sites that then use them.
/// </summary>
public sealed class FenJsFingerprintProbeTests
{
    [Theory]
    [InlineData("String(window.frames === window) + String(frames === self)", "truetrue")]
    [InlineData("typeof window.chrome", "object")]
    [InlineData("typeof window.chrome.runtime", "object")]
    [InlineData("navigator.plugins.length > 0", "true")]
    [InlineData("navigator.mimeTypes.length > 0", "true")]
    [InlineData("typeof navigator.hardwareConcurrency", "number")]
    [InlineData("typeof navigator.deviceMemory", "number")]
    [InlineData("typeof navigator.permissions", "object")]
    [InlineData("typeof navigator.permissions.query", "function")]
    [InlineData("typeof Notification", "function")]
    [InlineData("typeof Notification.permission", "string")]
    [InlineData("typeof screen.availWidth", "number")]
    [InlineData("typeof screen.colorDepth", "number")]
    [InlineData("typeof screen.orientation", "object")]
    [InlineData("typeof window.outerWidth", "number")]
    [InlineData("typeof window.devicePixelRatio", "number")]
    [InlineData("typeof performance.memory", "object")]
    [InlineData("typeof performance.timing", "object")]
    [InlineData("typeof performance.getEntriesByType", "function")]
    [InlineData("typeof PerformanceObserver", "function")]
    [InlineData("typeof Intl.DateTimeFormat().resolvedOptions().timeZone", "string")]
    [InlineData("typeof document.hasFocus", "function")]
    [InlineData("typeof document.hidden", "boolean")]
    [InlineData("typeof document.visibilityState", "string")]
    [InlineData("typeof document.createElement('canvas').getContext('2d')", "object")]
    [InlineData("typeof navigator.getBattery", "function")]
    [InlineData("typeof navigator.connection", "object")]
    [InlineData("typeof navigator.storage", "object")]
    [InlineData("typeof navigator.storage.estimate", "function")]
    [InlineData("typeof navigator.serviceWorker", "object")]
    [InlineData("typeof navigator.credentials", "object")]
    [InlineData("typeof navigator.locks", "object")]
    [InlineData("typeof navigator.clipboard", "object")]
    [InlineData("typeof navigator.geolocation", "object")]
    [InlineData("typeof navigator.vendor", "string")]
    [InlineData("navigator.vendor", "Google Inc.")]
    [InlineData("typeof navigator.productSub", "string")]
    [InlineData("typeof navigator.maxTouchPoints", "number")]
    [InlineData("typeof navigator.pdfViewerEnabled", "boolean")]
    [InlineData("typeof navigator.cookieEnabled", "boolean")]
    [InlineData("typeof navigator.onLine", "boolean")]
    [InlineData("typeof navigator.javaEnabled", "function")]
    [InlineData("typeof window.matchMedia", "function")]
    [InlineData("typeof window.matchMedia('(prefers-color-scheme: dark)').matches", "boolean")]
    [InlineData("typeof CSS.supports", "function")]
    [InlineData("typeof window.getComputedStyle(document.body).getPropertyValue", "function")]
    [InlineData("typeof document.fonts", "object")]
    [InlineData("typeof document.fonts.check", "function")]
    [InlineData("typeof window.crypto.subtle", "object")]
    [InlineData("typeof window.crypto.getRandomValues", "function")]
    [InlineData("typeof window.indexedDB", "object")]
    [InlineData("typeof window.localStorage", "object")]
    [InlineData("typeof window.openDatabase", "undefined")]
    [InlineData("typeof window.SharedArrayBuffer", "undefined")]
    [InlineData("typeof window.requestIdleCallback", "function")]
    [InlineData("typeof window.queueMicrotask", "function")]
    [InlineData("typeof structuredClone", "function")]
    [InlineData("typeof window.ResizeObserver", "function")]
    [InlineData("typeof window.IntersectionObserver", "function")]
    [InlineData("typeof window.MutationObserver", "function")]
    [InlineData("typeof window.PointerEvent", "function")]
    [InlineData("typeof window.TouchEvent", "undefined")]
    [InlineData("typeof document.documentElement.requestFullscreen", "function")]
    [InlineData("typeof Element.prototype.animate", "function")]
    [InlineData("typeof Element.prototype.attachShadow", "function")]
    [InlineData("typeof window.history.pushState", "function")]
    [InlineData("typeof window.customElements", "object")]
    [InlineData("typeof window.visualViewport", "object")]
    [InlineData("typeof window.screenX", "number")]
    [InlineData("typeof window.innerWidth", "number")]
    [InlineData("Object.getOwnPropertyDescriptor(Navigator.prototype, 'webdriver') !== undefined", "true")]
    [InlineData("navigator.webdriver", "false")]
    [InlineData("typeof Function.prototype.toString.call(document.createElement)", "string")]
    [InlineData("Function.prototype.toString.call(document.createElement).indexOf('[native code]') > 0", "true")]
    [InlineData("Function.prototype.toString.call(setTimeout).indexOf('[native code]') > 0", "true")]
    [InlineData("String(Object.getPrototypeOf(navigator))", "[object Navigator]")]
    [InlineData("String(navigator)", "[object Navigator]")]
    [InlineData("String(window)", "[object Window]")]
    [InlineData("String(document)", "[object HTMLDocument]")]
    [InlineData("String(document.body)", "[object HTMLBodyElement]")]
    [InlineData("Object.prototype.toString.call(document.createElement('img'))", "[object HTMLImageElement]")]
    [InlineData("typeof Error().stack", "string")]
    [InlineData("new Error('x').stack.indexOf('x') >= 0", "true")]
    [InlineData("(function(){ try { null.x } catch (e) { return e.message.indexOf('null') >= 0 || e.message.indexOf('undefined') >= 0 } })()", "true")]
    [InlineData("Object.keys(window).length > 0", "true")]
    [InlineData("typeof window.onerror", "object")]
    [InlineData("'ontouchstart' in window", "false")]
    [InlineData("typeof window.Notification.requestPermission", "function")]
    [InlineData("typeof window.BroadcastChannel", "function")]
    [InlineData("typeof window.Worker", "function")]
    [InlineData("typeof HTMLElement.prototype.inert", "boolean")]
    [InlineData("typeof navigator.userActivation", "object")]
    [InlineData("typeof navigator.scheduling", "object")]
    [InlineData("typeof window.scheduler", "object")]
    [InlineData("typeof window.trustedTypes", "object")]
    [InlineData("typeof window.reportError", "function")]
    [InlineData("typeof window.origin", "string")]
    [InlineData("typeof window.isSecureContext", "boolean")]
    [InlineData("typeof window.crossOriginIsolated", "boolean")]
    [InlineData("typeof window.ai", "undefined")]
    public async Task Probe(string expression, string expected)
    {
        var engine = await CreateEngineAsync("<html><body><div id=\"d\">x</div></body></html>");
        string result;
        try { result = engine.Evaluate($"String({expression});")?.ToString(); }
        catch (Exception ex) { result = "THREW " + ex.GetType().Name; }
        Assert.Equal(expected, result);
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string html)
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
