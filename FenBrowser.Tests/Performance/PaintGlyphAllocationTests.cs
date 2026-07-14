using System;
using System.Collections.Generic;
using System.Reflection;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

public sealed class PaintGlyphAllocationTests
{
    private readonly ITestOutputHelper _output;

    public PaintGlyphAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void BuildPaintGlyphs_UsesOneFixedSizeResultContainer()
    {
        var method = typeof(NewPaintTreeBuilder).GetMethod(
            "BuildPaintGlyphs",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var buildGlyphs = method!.CreateDelegate<
            Func<string, string, float, int, SKPoint, IReadOnlyList<PositionedGlyph>>>();
        var origin = new SKPoint(12.5f, 48.25f);
        var warmup = buildGlyphs("Paint glyph allocation probe", "Segoe UI", 16f, 400, origin);
        Assert.NotNull(warmup);
        Assert.NotEmpty(warmup!);

        IReadOnlyList<PositionedGlyph> glyphs = null;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            glyphs = buildGlyphs("Paint glyph allocation probe", "Segoe UI", 16f, 400, origin);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"1,000 warmed glyph builds allocated {allocated:N0} B.");
        Assert.NotNull(glyphs);
        Assert.IsType<PositionedGlyph[]>(glyphs);
        Assert.Equal(warmup!.Count, glyphs!.Count);
        Assert.Equal(origin.X, glyphs[0].X);
        Assert.Equal(origin.Y, glyphs[0].Y);
        Assert.InRange(allocated, 1, 361_000);
    }
}
