using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

[Collection("Performance diagnostics")]
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

    [Fact]
    public void BuildPaintTree_FallbackTextHasBoundedPaintGenerationAllocations()
    {
        const int iterations = 1_000;
        const string content = "Paint tree glyph allocation probe";

        var root = new Element("div");
        var text = new Text(content);
        root.AppendChild(text);

        var textBox = BoxModel.FromContentBox(0, 0, 280, 24);
        textBox.Lines = new List<ComputedTextLine>
        {
            new()
            {
                Text = content,
                Origin = new SKPoint(0, 0),
                Width = 280,
                Height = 24,
                Baseline = 18
            }
        };

        var boxes = new Dictionary<Node, BoxModel>
        {
            [root] = BoxModel.FromContentBox(0, 0, 280, 24),
            [text] = textBox
        };
        var style = new CssComputed
        {
            Display = "block",
            FontFamilyName = "Segoe UI",
            FontSize = 16,
            FontWeight = 400,
            ForegroundColor = SKColors.Black
        };
        var styles = new Dictionary<Node, CssComputed>
        {
            [root] = style,
            [text] = style
        };

        bool originalLogPaintCommands = DebugConfig.LogPaintCommands;
        DebugConfig.LogPaintCommands = false;
        try
        {
            _ = NewPaintTreeBuilder.Build(root, boxes, styles, 800, 600, null);

            ImmutablePaintTree tree = null;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                tree = NewPaintTreeBuilder.Build(root, boxes, styles, 800, 600, null);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            _output.WriteLine($"{iterations:N0} warmed fallback-text paint-tree builds allocated {allocated:N0} B.");

            var textNode = Assert.Single(tree!.Roots.OfType<TextPaintNode>());
            Assert.Equal(content, textNode.FallbackText);
            Assert.Null(textNode.Glyphs);
            Assert.InRange(allocated, 1, 3_230_000);

            DebugConfig.LogPaintCommands = true;
            var diagnosticTree = NewPaintTreeBuilder.Build(root, boxes, styles, 800, 600, null);
            var diagnosticTextNode = Assert.Single(diagnosticTree.Roots.OfType<TextPaintNode>());
            Assert.NotNull(diagnosticTextNode.Glyphs);
            Assert.NotEmpty(diagnosticTextNode.Glyphs!);
        }
        finally
        {
            DebugConfig.LogPaintCommands = originalLogPaintCommands;
        }
    }
}
