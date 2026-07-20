using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Tests.Logging;

namespace FenBrowser.Tests.Scripting;

[Collection(EngineLogTestCollection.Name)]
public sealed class FenJsViewportConsistencyTests
{
    [Fact]
    public async Task ViewportApisShareCssPixelDimensions()
    {
        var baseUri = new Uri("https://fixture.test/viewport.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                WindowWidth = 1280,
                WindowHeight = 800
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(
                "1280|1280|1280|800|1",
                engine.Evaluate(
                    "[innerWidth,document.documentElement.clientWidth," +
                    "visualViewport.width,visualViewport.height,devicePixelRatio].join('|')")?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    [Fact]
    public async Task ResizingSynchronizesScriptViewportMetrics()
    {
        var baseUri = new Uri("https://fixture.test/viewport-resize.html");
        try
        {
            BrowserScriptEngineRuntime.Reset();
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                WindowWidth = 1280,
                WindowHeight = 800
            };
            await engine.SetDomAsync(document.DocumentElement, baseUri);

            engine.WindowWidth = 900;
            engine.WindowHeight = 700;

            Assert.Equal(
                "900|900|900|700|true",
                engine.Evaluate(
                    "[innerWidth,document.documentElement.clientWidth," +
                    "visualViewport.width,visualViewport.height," +
                    "matchMedia('(max-width: 1000px)').matches].join('|')")?.ToString());
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
