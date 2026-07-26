using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Performance;

[Collection("Performance diagnostics")]
public sealed class LazyScriptLayoutTests
{
    [Fact]
    public async Task RenderAsyncDoesNotForceLayoutBeforeScriptThatDoesNotReadGeometry()
    {
        using var engine = new CustomHtmlEngine();
        var renderer = new SkiaDomRenderer();
        engine.SetExternalRenderer(renderer);

        var result = await engine.RenderAsync(
            """
            <!doctype html>
            <html>
              <head><style>main { width: 200px; height: 80px; }</style></head>
              <body><main>content</main><script>globalThis.layoutIndependentWork = 1;</script></body>
            </html>
            """,
            new Uri("https://layout.test/"),
            _ => Task.FromResult(string.Empty),
            imageLoader: null,
            onNavigate: _ => { },
            viewportWidth: 320,
            viewportHeight: 200,
            forceJavascript: true);

        Assert.Same(renderer, result);
        Assert.Null(renderer.LastLayout);
    }
}
