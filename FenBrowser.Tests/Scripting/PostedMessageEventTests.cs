using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML 9.3.3: the event a postMessage delivers is a MessageEvent, with the Event
/// methods, and the window's listeners honour stopImmediatePropagation on it.
/// </summary>
public sealed class PostedMessageEventTests
{
    [Fact]
    public async Task APostedMessageEventHasTheEventMethods()
    {
        var engine = await CreateEngineAsync("");
        engine.Evaluate(
            "globalThis.__seen = '';" +
            "window.addEventListener('message', function (e) {" +
            "  e.stopImmediatePropagation();" +
            "  __seen = (e instanceof MessageEvent) + ':' + (e instanceof Event) + ':' + e.data;" +
            "});" +
            "window.addEventListener('message', function () { __seen += ':second-ran'; });" +
            "window.postMessage('hi', '*');");

        Assert.Equal("true:true:hi", await WaitForAsync(engine, "__seen"));
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string body)
    {
        var baseUri = new Uri("https://example.test/");
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
