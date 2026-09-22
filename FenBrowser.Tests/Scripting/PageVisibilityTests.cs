using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// The Page Visibility API: the host's tab and window state reaches the document, and
/// script sees it through visibilityState, hidden and the visibilitychange event.
/// </summary>
public sealed class PageVisibilityTests
{
    [Fact]
    public async Task ADocumentStartsVisible()
    {
        var engine = await CreateEngineAsync();
        Assert.Equal("visible", engine.Evaluate("document.visibilityState")?.ToString());
        Assert.Equal("false", engine.Evaluate("String(document.hidden)")?.ToString());
    }

    [Fact]
    public async Task HidingThePageUpdatesTheDocumentAndFiresVisibilitychange()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate(
            "globalThis.__log = [];" +
            "document.addEventListener('visibilitychange', function () {" +
            "  __log.push(document.visibilityState + ':' + document.hidden);" +
            "});");

        engine.SetPageVisible(false);
        Assert.Equal("hidden", engine.Evaluate("document.visibilityState")?.ToString());
        Assert.Equal("true", engine.Evaluate("String(document.hidden)")?.ToString());
        Assert.Equal("hidden:true", await WaitForAsync(engine, "__log.join(',')"));

        // The same state again is not a change, so no second event.
        engine.SetPageVisible(false);
        engine.SetPageVisible(true);
        Assert.Equal("hidden:true,visible:false", await WaitForAsync(engine, "__log.length > 1 ? __log.join(',') : ''"));
        Assert.Equal("visible", engine.Evaluate("document.visibilityState")?.ToString());
    }

    [Fact]
    public async Task TheOnvisibilitychangeHandlerRunsToo()
    {
        var engine = await CreateEngineAsync();
        engine.Evaluate("globalThis.__seen = ''; document.onvisibilitychange = function (e) { __seen = e.type + '/' + document.visibilityState; };");
        engine.SetPageVisible(false);
        Assert.Equal("visibilitychange/hidden", await WaitForAsync(engine, "__seen"));
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
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

    private static async Task<string> WaitForAsync(FenJsBrowserScriptEngine engine, string expression)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 5000)
        {
            var value = engine.Evaluate(expression)?.ToString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            await Task.Delay(25);
        }

        return engine.Evaluate(expression)?.ToString() ?? string.Empty;
    }
}
