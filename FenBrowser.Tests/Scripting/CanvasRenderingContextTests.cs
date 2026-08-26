using System;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class CanvasRenderingContextTests : IDisposable
{
    public CanvasRenderingContextTests()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    public void Dispose()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    private static FenJsBrowserScriptEngine CreateEngine(Uri baseUri, out FenBrowser.Core.Dom.V2.Document document)
    {
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();
        return engine;
    }

    private static string EvaluateOnCanvasPage(string script)
    {
        var engine = CreateEngine(new Uri("https://canvas.test/page"), out _);
        return engine.Evaluate(script)?.ToString();
    }

    private static JsHostAdapter CreateHost()
    {
        return new JsHostAdapter(
            navigate: _ => { },
            post: (_, __) => { },
            status: _ => { },
            log: _ => { });
    }

    [Fact]
    public void GetContext2d_ReturnsSameContextAndWebglIsNull()
    {
        Assert.Equal("function", EvaluateOnCanvasPage(
            "typeof document.createElement('canvas').getContext"));
        Assert.Equal("true", EvaluateOnCanvasPage(
            "var c = document.createElement('canvas');" +
            "var a = c.getContext('2d'); var b = c.getContext('2d');" +
            "String(a === b && a !== null)"));
        Assert.Equal("true", EvaluateOnCanvasPage(
            "String(document.createElement('canvas').getContext('webgl') === null)"));
    }

    [Fact]
    public void FillRect_ToDataURL_ProducesPngDataUrl()
    {
        Assert.Equal("true", EvaluateOnCanvasPage(
            "var c = document.createElement('canvas');" +
            "c.width = 8; c.height = 8;" +
            "var ctx = c.getContext('2d');" +
            "ctx.fillStyle = '#ff0000';" +
            "ctx.fillRect(0, 0, 4, 4);" +
            "var url = c.toDataURL();" +
            "String(url.startsWith('data:image/png;base64,') && url.length > 100)"));
    }

    [Fact]
    public void ContextStateAndDrawingOperations_Work()
    {
        Assert.Equal("true", EvaluateOnCanvasPage(
            "var c = document.createElement('canvas');" +
            "c.width = 32; c.height = 32;" +
            "var ctx = c.getContext('2d');" +
            "ctx.fillStyle = 'rgba(10, 20, 30, 0.5)';" +
            "ctx.strokeStyle = 'rgb(1, 2, 3)';" +
            "ctx.lineWidth = 3.5;" +
            "ctx.save();" +
            "ctx.translate(5, 6);" +
            "ctx.scale(2, 2);" +
            "ctx.beginPath();" +
            "ctx.moveTo(0, 0);" +
            "ctx.lineTo(10, 10);" +
            "ctx.arc(16, 16, 8, 0, Math.PI * 2);" +
            "ctx.closePath();" +
            "ctx.fill();" +
            "ctx.stroke();" +
            "ctx.restore();" +
            "ctx.clearRect(0, 0, 4, 4);" +
            "var imageData = ctx.getImageData(0, 0, 2, 2);" +
            "ctx.setLineDash([4, 2]);" +
            "String(ctx.lineWidth === 3.5 && imageData.width === 2 && imageData.data.length === 16" +
            " && ctx.getLineDash().length === 2)"));
    }

    [Fact]
    public void WidthAttributeWrite_ResizesSurfaceAndClears()
    {
        Assert.Equal("true", EvaluateOnCanvasPage(
            "var c = document.createElement('canvas');" +
            "var ctx = c.getContext('2d');" +
            "ctx.fillRect(0, 0, 50, 50);" +
            "c.width = 64;" +
            "var imageData = c.getContext('2d').getImageData(0, 0, 1, 1);" +
            "String(c.width === 64 && imageData.data[3] === 0)"));
    }

    [Fact]
    public void MeasureText_ReturnsPositiveWidth()
    {
        Assert.Equal("true", EvaluateOnCanvasPage(
            "var c = document.createElement('canvas');" +
            "var ctx = c.getContext('2d');" +
            "var metrics = ctx.measureText('hello world');" +
            "String(metrics.width > 0)"));
    }

    [Fact]
    public void Gradient_AddColorStop_DoesNotThrow()
    {
        Assert.Equal("true", EvaluateOnCanvasPage(
            "var c = document.createElement('canvas');" +
            "var ctx = c.getContext('2d');" +
            "var gradient = ctx.createLinearGradient(0, 0, 10, 0);" +
            "gradient.addColorStop(0, '#000000');" +
            "gradient.addColorStop(1, 'rgba(255, 255, 255, 1)');" +
            "ctx.fillStyle = '#123456';" +
            "ctx.fillRect(0, 0, 10, 10);" +
            "String(gradient !== null)"));
    }
}
