using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class FenJsAbortSignalTests
{
    [Fact]
    public async Task AbortSignalDispatchesAbortListeners()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        var result = engine.Evaluate("""
            (function () {
                var controller = new AbortController();
                var calls = 0;
                controller.signal.addEventListener('abort', function () { calls++; });
                controller.abort('done');
                controller.abort('ignored');
                return controller.signal.aborted &&
                    controller.signal.reason === 'done' &&
                    calls === 1;
            })();
            """);

        Assert.Equal("True", result?.ToString());
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
