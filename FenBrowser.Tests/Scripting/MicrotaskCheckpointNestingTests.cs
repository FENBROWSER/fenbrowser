using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML 8.1.4.4 "clean up after running script": the microtask checkpoint waits until the
/// JavaScript execution context stack is empty. The first classList read compiles a
/// prototype helper through an evaluation nested in the running script, and that nested
/// evaluation used to drain the page's whole queue in the middle of it.
/// </summary>
public sealed class MicrotaskCheckpointNestingTests
{
    [Fact]
    public async Task PromiseReactions_WaitForTheScriptThatQueuedThem()
    {
        var engine = await StartAsync(
            """
            <script>
            var log = [];
            Promise.resolve().then(function () { log.push('promise'); });
            var list = document.createElement('div').classList;
            log.push('sync:' + typeof list);
            globalThis.__during = log.join(',');
            </script>
            """);

        try
        {
            Assert.Equal("sync:object", engine.Evaluate("String(globalThis.__during)")?.ToString());
            Assert.Equal("sync:object,promise", engine.Evaluate("String(log.join(','))")?.ToString());
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
