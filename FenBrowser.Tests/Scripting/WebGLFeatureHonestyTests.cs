using System;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering.WebGL;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

// RENDER-005: the software WebGL placeholder cannot execute shaders, so WebGL
// feature detection must report unsupported and the scripting surface must
// resolve canvas.getContext("webgl"/"webgl2") to null — while Canvas 2D stays
// fully operational.
[Collection("Engine Tests")]
public sealed class WebGLFeatureHonestyTests : IDisposable
{
    public WebGLFeatureHonestyTests()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    public void Dispose()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    [Fact]
    public void SupportProbes_ReportWebGLAndWebGL2Unsupported()
    {
        Assert.False(WebGLContextManager.IsWebGLSupported());
        Assert.False(WebGLContextManager.IsWebGL2Supported());
    }

    [Theory]
    [InlineData("webgl")]
    [InlineData("webgl2")]
    [InlineData("experimental-webgl")]
    public void ScriptingSurface_GetContextWebGL_ReturnsNull(string contextKind)
    {
        Assert.Equal("true", EvaluateOnCanvasPage(
            $"String(document.createElement('canvas').getContext('{contextKind}') === null)"));
    }

    [Fact]
    public void Canvas2d_RemainsOperational()
    {
        Assert.Equal("true", EvaluateOnCanvasPage(
            "var c = document.createElement('canvas');" +
            "c.width = 8; c.height = 8;" +
            "var ctx = c.getContext('2d');" +
            "ctx.fillStyle = '#00ff00';" +
            "ctx.fillRect(0, 0, 4, 4);" +
            "var url = c.toDataURL();" +
            "String(ctx !== null && url.startsWith('data:image/png;base64,') && url.length > 100)"));
    }

    private static string EvaluateOnCanvasPage(string script)
    {
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, __) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        var document = new HtmlParser("<html><body></body></html>", new Uri("https://canvas.test/page")).Parse();
        engine.SetDomAsync(document.DocumentElement, new Uri("https://canvas.test/page")).GetAwaiter().GetResult();
        return engine.Evaluate(script)?.ToString();
    }
}
