using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// DOM 2.2: an event the user agent dispatches is a platform object of its interface.
/// Engine-built events had Object.prototype as their prototype, so ShadyDOM's patchEvent
/// (which caches a patched prototype on the event's prototype) polluted Object.prototype
/// with an enumerable key that every for-in on YouTube then saw.
/// </summary>
public sealed class DispatchedEventPrototypeTests
{
    [Fact]
    public async Task EngineDispatchedEvents_HaveTheirInterfacePrototype()
    {
        var engine = await StartAsync(
            """
            <button id="b">b</button>
            <script>
            var seen = {};
            var b = document.getElementById('b');
            b.addEventListener('click', function (e) {
                seen.click = (e instanceof Event) + ':' + (e instanceof MouseEvent);
                Object.getPrototypeOf(e).__patchedByListener = 1;
            });
            b.addEventListener('focus', function (e) { seen.focus = e instanceof FocusEvent; });
            b.click();
            b.focus();
            var leaked = [];
            for (var k in {}) leaked.push(k);
            globalThis.__r = JSON.stringify({ click: seen.click, focus: seen.focus, leaked: leaked.join(',') });
            </script>
            """);

        try
        {
            Assert.Equal(
                "{\"click\":\"true:true\",\"focus\":true,\"leaked\":\"\"}",
                engine.Evaluate("String(globalThis.__r)")?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task BrandingNeverRunsPageGetters_FetchStillResolves()
    {
        // Every callback the engine delivers passes through the branding step, fetch
        // completions included. Reading `type` with a full [[Get]] ran this getter and
        // the fetch never resolved (WPT fetch/api/basic/stream-safe-creation).
        var engine = await StartAsync(
            """
            <script>
            Object.defineProperty(Object.prototype, 'type', {
                get: function () { throw new Error('type getter ran'); },
                configurable: true
            });
            globalThis.__r = 'pending';
            fetch('data:text/plain,hi')
                .then(function (r) { return r.text(); })
                .then(function (t) { globalThis.__r = 'ok:' + t; }, function (e) { globalThis.__r = 'rejected:' + e; });
            </script>
            """);

        try
        {
            var result = "pending";
            for (var i = 0; i < 100 && result == "pending"; i++)
            {
                await Task.Delay(50);
                result = engine.Evaluate("String(globalThis.__r)")?.ToString();
            }

            Assert.Equal("ok:hi", result);
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static async Task<FenJsBrowserScriptEngine> StartAsync(string body)
    {
        var baseUri = new Uri("https://fixture.test/index.html");
        var document = new HtmlParser("<html><body>" + body + "</body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }
}
