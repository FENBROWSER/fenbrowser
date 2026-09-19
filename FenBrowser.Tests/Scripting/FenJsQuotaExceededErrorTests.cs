using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>WebIDL's QuotaExceededError: a DOMException subclass with quota and requested (mediasource-appendbuffer-quota-exceeded checks the constructor).</summary>
public sealed class FenJsQuotaExceededErrorTests
{
    [Fact]
    public async Task IsADomExceptionSubclass_WithQuotaAndRequested()
    {
        var baseUri = new Uri("https://fixture.test/index.html");
        var document = new HtmlParser(
            """
            <html><body><script>
            var e = new QuotaExceededError('full');
            globalThis.__a = [e instanceof DOMException, e instanceof QuotaExceededError, e.name, e.code, e.quota, e.requested, e.message].join(',');
            var f = new QuotaExceededError('x', { quota: 10, requested: 12 });
            globalThis.__b = [f.quota, f.requested].join(',');
            var bad = 'none';
            try { new QuotaExceededError('x', { quota: 12, requested: 10 }); } catch (err) { bad = err.name; }
            globalThis.__c = bad;
            </script></body></html>
            """,
            baseUri).Parse();

        var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
        try
        {
            await engine.SetDomAsync(document.DocumentElement, baseUri);
            Assert.Equal("true,true,QuotaExceededError,22,,,full", engine.Evaluate("String(globalThis.__a)")?.ToString());
            Assert.Equal("10,12", engine.Evaluate("String(globalThis.__b)")?.ToString());
            Assert.Equal("RangeError", engine.Evaluate("String(globalThis.__c)")?.ToString());
        }
        finally
        {
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
