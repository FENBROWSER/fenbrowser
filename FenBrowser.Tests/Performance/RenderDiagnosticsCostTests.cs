using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Performance;

public sealed class RenderDiagnosticsCostTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task VerificationFlagControlsDebugScreenshot(bool emitVerificationReport, int expectedRequests)
    {
        const string source = "<!doctype html><html><body><div>frame</div></body></html>";
        var baseUri = new Uri("https://performance.test/");
        var document = new HtmlParser(source, baseUri).Parse();
        var root = Assert.IsType<Element>(document.DocumentElement);
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 96, viewportHeight: 64);
        var renderer = new SkiaDomRenderer
        {
            SafetyPolicy = new RendererSafetyPolicy { EnableWatchdog = false }
        };
        using var bitmap = new SKBitmap(96, 64);
        using var canvas = new SKCanvas(bitmap);

        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = styles,
            Viewport = new SKRect(0, 0, 96, 64),
            BaseUrl = baseUri.AbsoluteUri,
            InvalidationReason = RenderFrameInvalidationReason.Navigation,
            RequestedBy = nameof(VerificationFlagControlsDebugScreenshot),
            EmitVerificationReport = emitVerificationReport
        });
        canvas.Flush();

        Assert.Equal(expectedRequests, renderer.DebugScreenshotRequestCount);
        Assert.NotEqual(SKColors.Transparent, bitmap.GetPixel(0, 0));
    }
}
