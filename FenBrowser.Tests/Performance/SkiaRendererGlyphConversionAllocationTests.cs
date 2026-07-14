using System;
using System.Reflection;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Backends;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;
using TypographyGlyphRun = FenBrowser.FenEngine.Typography.GlyphRun;

namespace FenBrowser.Tests.Performance;

public sealed class SkiaRendererGlyphConversionAllocationTests
{
    private readonly ITestOutputHelper _output;

    public SkiaRendererGlyphConversionAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DrawText_GlyphPathAvoidsIteratorAllocation()
    {
        var method = typeof(SkiaRenderer).GetMethod(
            "DrawText",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var drawText = method!.CreateDelegate<Action<SkiaRenderer, IRenderBackend, TextPaintNode>>();
        var glyphs = new PositionedGlyph[32];
        for (var index = 0; index < glyphs.Length; index++)
        {
            glyphs[index] = new PositionedGlyph((ushort)(index + 1), 10 + index * 8, 24);
        }

        var node = new TextPaintNode
        {
            Bounds = new SKRect(0, 0, 300, 40),
            TextOrigin = new SKPoint(10, 24),
            FallbackText = string.Empty,
            FontSize = 16,
            Typeface = SKTypeface.Default,
            Color = SKColors.Black,
            Glyphs = glyphs
        };
        var renderer = new SkiaRenderer();

        var semanticBackend = new CapturingBackend();
        drawText(renderer, semanticBackend, node);
        Assert.NotNull(semanticBackend.LastGlyphRun);
        Assert.Equal(glyphs.Length, semanticBackend.LastGlyphRun!.Glyphs.Length);
        Assert.Equal(glyphs[0].GlyphId, semanticBackend.LastGlyphRun.Glyphs[0].GlyphId);
        Assert.Equal(glyphs[0].X, semanticBackend.LastGlyphRun.Glyphs[0].X);
        Assert.Equal(glyphs[^1].Y, semanticBackend.LastGlyphRun.Glyphs[^1].Y);

        var measurementBackend = new HeadlessRenderBackend { LogCommands = false };
        drawText(renderer, measurementBackend, node);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            drawText(renderer, measurementBackend, node);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"1,000 warmed glyph-path draws allocated {allocated:N0} B.");
        Assert.InRange(allocated, 1, 609_000);
    }

    private sealed class CapturingBackend : HeadlessRenderBackend, IRenderBackend
    {
        public TypographyGlyphRun LastGlyphRun { get; private set; }

        public new void DrawGlyphRun(SKPoint origin, TypographyGlyphRun glyphs, SKColor color, float opacity = 1f)
        {
            LastGlyphRun = glyphs;
        }
    }
}
