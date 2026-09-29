using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// CSS Fonts 4 §5.2 face matching and §7.1 variable-font weight for @font-face families.
/// Resolution used to hand back whichever face of a family loaded first, and a variable
/// face at its default instance: github.com's Mona Sans (wght 200..900, default 200)
/// rendered every heading and button hairline.
/// </summary>
[Collection("Synthetic CAPTCHA")]
public sealed class FontRegistryWeightMatchingTests
{
    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Fonts", "Woff2", name + ".woff2"));

    private static async Task WithFaces(Dictionary<string, byte[]> bodies, string[] rules, Func<Task> assertions)
    {
        var previousFetcher = FontRegistry.FetchDetailedAsync;
        FontRegistry.Clear();
        try
        {
            FontRegistry.FetchDetailedAsync = uri => Task.FromResult(new BinaryFetchResult
            {
                FinalUri = uri,
                Body = bodies[Path.GetFileName(uri.AbsolutePath)],
            });
            foreach (var rule in rules)
            {
                FontRegistry.ParseAndRegister(rule, new Uri("https://fonts.example.test/"));
            }

            await FontRegistry.LoadPendingFontsAsync();
            await assertions();
        }
        finally
        {
            FontRegistry.FetchDetailedAsync = previousFetcher;
            FontRegistry.Clear();
        }
    }

    private static float WeightAxis(SKTypeface typeface)
    {
        var wght = SKFourByteTag.Parse("wght");
        foreach (var coordinate in typeface.VariationDesignPosition)
        {
            if (coordinate.Axis == wght) return coordinate.Value;
        }

        return float.NaN;
    }

    [Theory]
    [InlineData(425, 425f)]
    [InlineData(700, 700f)]
    [InlineData(100, 200f)] // clamped to the face's declared 200..900
    public Task VariableFace_IsInstancedAtTheUsedWeight(int weight, float expectedAxis) =>
        WithFaces(
            new Dictionary<string, byte[]> { ["mono.woff2"] = Fixture("MonaSansMonoVF") },
            new[] { "font-family: 'Mono Probe'; src: url('mono.woff2') format('woff2'); font-weight: 200 900;" },
            () =>
            {
                var typeface = FontRegistry.TryResolve("Mono Probe", weight, SKFontStyleSlant.Upright);
                Assert.NotNull(typeface);
                Assert.Equal(expectedAxis, WeightAxis(typeface), 1);
                return Task.CompletedTask;
            });

    [Fact]
    public Task StaticFaces_AreMatchedByWeight_NotByLoadOrder() =>
        WithFaces(
            new Dictionary<string, byte[]>
            {
                ["regular.woff2"] = Fixture("valid-005"),
                ["bold.woff2"] = Fixture("MonaSansMonoVF"),
            },
            new[]
            {
                "font-family: 'Pair Probe'; src: url('regular.woff2') format('woff2'); font-weight: 400;",
                "font-family: 'Pair Probe'; src: url('bold.woff2') format('woff2'); font-weight: 700;",
            },
            () =>
            {
                // The two files are told apart by glyph count (5 vs 746).
                Assert.Equal(5, FontRegistry.TryResolve("Pair Probe", 400, SKFontStyleSlant.Upright).GlyphCount);
                Assert.Equal(746, FontRegistry.TryResolve("Pair Probe", 700, SKFontStyleSlant.Upright).GlyphCount);
                // §5.2: 600 looks heavier first; 300 looks lighter first, then heavier.
                Assert.Equal(746, FontRegistry.TryResolve("Pair Probe", 600, SKFontStyleSlant.Upright).GlyphCount);
                Assert.Equal(5, FontRegistry.TryResolve("Pair Probe", 300, SKFontStyleSlant.Upright).GlyphCount);
                return Task.CompletedTask;
            });
}
