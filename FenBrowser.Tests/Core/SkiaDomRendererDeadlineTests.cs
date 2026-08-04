using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class SkiaDomRendererDeadlineTests
{
    [Fact]
    public void ResolveLayoutDeadlineBudget_ExpandsOnlyForLargeFullDocumentLayout()
    {
        var previous = Environment.GetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS");
        try
        {
            Environment.SetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS", null);

            Assert.Equal(3000d, SkiaDomRenderer.ResolveLayoutDeadlineBudgetMs(250, fullDocumentLayout: true));
            Assert.Equal(3000d, SkiaDomRenderer.ResolveLayoutDeadlineBudgetMs(2891, fullDocumentLayout: false));

            var budget = SkiaDomRenderer.ResolveLayoutDeadlineBudgetMs(2891, fullDocumentLayout: true);
            Assert.Equal(25000d, budget);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS", previous);
        }
    }

    [Fact]
    public void ResolveLayoutDeadlineBudget_EnvironmentOverrideWins()
    {
        var previous = Environment.GetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS");
        try
        {
            Environment.SetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS", "0");
            Assert.Equal(0d, SkiaDomRenderer.ResolveLayoutDeadlineBudgetMs(5000, fullDocumentLayout: true));

            Environment.SetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS", "1234");
            Assert.Equal(1234d, SkiaDomRenderer.ResolveLayoutDeadlineBudgetMs(5000, fullDocumentLayout: true));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FEN_LAYOUT_DEADLINE_MS", previous);
        }
    }

    [Fact]
    public async Task RenderFrame_WhenLayoutDeadlineExpires_DoesNotPaintRenderError()
    {
        var html = "<!doctype html><html><body>" +
            string.Concat(Enumerable.Range(0, 4000).Select(i => $"<div class='n{i % 8}'>content</div>")) +
            "</body></html>";
        var baseUri = new Uri("https://deadline.test/");
        var doc = new HtmlParser(html, baseUri).Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 320, viewportHeight: 240);
        var renderer = new SkiaDomRenderer { LayoutDeadlineBudgetOverrideMs = 0.001 };

        using var bitmap = new SKBitmap(320, 240);
        using var canvas = new SKCanvas(bitmap);
        var result = renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = styles,
            Viewport = new SKRect(0, 0, 320, 240),
            BaseUrl = baseUri.AbsoluteUri,
            InvalidationReason = RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Dom,
            RequestedBy = "deadline-test",
            EmitVerificationReport = false
        });

        Assert.True(result.WatchdogTriggered);
        Assert.Equal(RenderFrameWatchdogAction.ScheduledFollowup, result.WatchdogAction);
        Assert.Equal(RenderFrameRasterMode.None, result.RasterMode);
        Assert.DoesNotContain(SampledPixels(bitmap, 0, 0, 240, 80), IsRedErrorPixel);
    }

    private static IEnumerable<SKColor> SampledPixels(SKBitmap bitmap, int left, int top, int width, int height)
    {
        for (var y = top; y < top + height; y++)
        {
            for (var x = left; x < left + width; x++)
            {
                yield return bitmap.GetPixel(x, y);
            }
        }
    }

    private static bool IsRedErrorPixel(SKColor color)
    {
        return color.Red > 180 && color.Green < 80 && color.Blue < 80;
    }
}
