using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Svg;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    public sealed class SvgForeignNamespaceVoidTests
    {
        private const string XhtmlDeclarations =
            "xmlns='http://www.w3.org/2000/svg' xmlns:h='http://www.w3.org/1999/xhtml'";

        private static SvgRenderLimits FullLimits() => new SvgRenderLimits
        {
            MaxRecursionDepth = 32,
            MaxFilterCount = 10,
            MaxRenderTimeMs = 250,
            MaxElementCount = 50000
        };

        private static SvgParsedDocument Parse(string source, out bool ok, out string fatal)
        {
            ok = SvgMarkupParser.TryParse(source, FullLimits(), out var doc, out fatal);
            return doc;
        }

        private static SvgElement Child(SvgElement parent, string name) =>
            parent.Children.FirstOrDefault(child => child.Name == name);

        private static bool WarnedAboutUnnesting(SvgParsedDocument doc) =>
            doc.Report.Warnings.Any(warning =>
                warning.Contains("has no end tag", StringComparison.Ordinal));

        [Fact]
        public void UnclosedForeignElement_DoesNotCaptureTheSiblingsThatFollowIt()
        {
            var doc = Parse(
                "<svg " + XhtmlDeclarations + ">" +
                "<h:base href='http://www.example.com/'>" +
                "<rect width='20' height='20' fill='green'/>" +
                "<text x='0' y='10'>ink</text></svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            var root = doc.Root;
            var foreign = Child(root, "h:base");
            Assert.NotNull(foreign);
            Assert.Empty(foreign.Children);
            Assert.NotNull(Child(root, "rect"));
            Assert.NotNull(Child(root, "text"));
            Assert.Equal(3, root.Children.Count);
            Assert.True(WarnedAboutUnnesting(doc));
            Assert.False(doc.Report.UnsupportedFeatureIgnored);
            Assert.Empty(doc.Report.FallbackReasonCodes);
        }

        [Fact]
        public void UnclosedForeignElement_RendersTheFollowingSiblingsInsteadOfDroppingThem()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20' " + XhtmlDeclarations + ">" +
                "<h:base href='http://www.example.com/'>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            AssertFailsClosed(result);
            Assert.True(result.RequiresFallback);
            Assert.Contains("unsupported-element", result.FallbackReasonCodes);
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("has no end tag", StringComparison.Ordinal));
            Assert.Contains(result.Warnings, warning =>
                warning.Contains("unknown element 'h:base' skipped", StringComparison.Ordinal));
        }

        [Fact]
        public void UnclosedForeignElement_PaintsTheSwallowedSubtreeAndCarriesTheFallbackReason()
        {
            using var rendered = RenderWithReport(
                "<svg width='20' height='20' " + XhtmlDeclarations + ">" +
                "<h:base href='http://www.example.com/'>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            Assert.Contains("unsupported-element", rendered.FallbackReasonCodes);
            Assert.Equal((byte)255, rendered.Bitmap.GetPixel(10, 10).Alpha);
            Assert.True(rendered.Bitmap.GetPixel(10, 10).Green > 100);
        }

        [Fact]
        public void PairedForeignElements_StayInertAndAdmissibleWithoutAReasonCode()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20' " + XhtmlDeclarations + ">" +
                "<h:base href='http://www.example.com/'/>" +
                "<h:div></h:div>" +
                "<h:iframe src='resources/blank.htm'></h:iframe>" +
                "<h:audio controls='controls'></h:audio>" +
                "<h:video controls='controls'></h:video>" +
                "<h:canvas tabindex='0'></h:canvas>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.DoesNotContain("unsupported-element", result.FallbackReasonCodes);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(10, 10).Alpha);
            Assert.True(result.Bitmap.GetPixel(10, 10).Green > 100);
        }

        [Fact]
        public void SelfClosedForeignElement_StaysAdmissibleWithoutAReasonCode()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20' " + XhtmlDeclarations + ">" +
                "<h:base href='http://www.example.com/'/>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.DoesNotContain("unsupported-element", result.FallbackReasonCodes);
            Assert.Equal((byte)255, result.Bitmap.GetPixel(10, 10).Alpha);
        }

        [Fact]
        public void UnclosedForeignElement_WithAPairedDescendant_StillDoesNotCaptureSiblings()
        {
            var doc = Parse(
                "<svg " + XhtmlDeclarations + ">" +
                "<h:div><h:span>text</h:span><rect width='20' height='20'/></svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            var foreign = Child(doc.Root, "h:div");
            Assert.NotNull(foreign);
            Assert.Empty(foreign.Children);
            Assert.NotNull(Child(doc.Root, "rect"));
            Assert.True(WarnedAboutUnnesting(doc));
        }

        [Fact]
        public void SelfClosedForeignElement_KeepsItsExistingShape()
        {
            var doc = Parse(
                "<svg " + XhtmlDeclarations + ">" +
                "<h:base href='http://www.example.com/'/>" +
                "<rect width='20' height='20'/></svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            var foreign = Child(doc.Root, "h:base");
            Assert.NotNull(foreign);
            Assert.Empty(foreign.Children);
            Assert.Equal("http://www.example.com/", foreign.GetAttribute("href"));
            Assert.NotNull(Child(doc.Root, "rect"));
            Assert.Equal(2, doc.Root.Children.Count);
            Assert.False(WarnedAboutUnnesting(doc));
        }

        [Fact]
        public void ProperlyPairedForeignElement_StillNestsItsOwnSubtree()
        {
            var doc = Parse(
                "<svg " + XhtmlDeclarations + ">" +
                "<h:b><rect width='20' height='20'/></h:b>" +
                "<rect width='10' height='10'/></svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            var foreign = Child(doc.Root, "h:b");
            Assert.NotNull(foreign);
            var nested = Assert.Single(foreign.Children);
            Assert.Equal("rect", nested.Name);
            Assert.Equal("20", nested.GetAttribute("width"));
            Assert.Equal(2, doc.Root.Children.Count);
            Assert.Equal("10", Child(doc.Root, "rect").GetAttribute("width"));
            Assert.False(WarnedAboutUnnesting(doc));
        }

        [Fact]
        public void ProperlyNestedPairedForeignElements_StillNestTheirOwnSubtree()
        {
            var doc = Parse(
                "<svg " + XhtmlDeclarations + ">" +
                "<h:div><h:div><rect width='20' height='20'/></h:div></h:div>" +
                "<rect width='10' height='10'/></svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            var outer = Child(doc.Root, "h:div");
            Assert.NotNull(outer);
            var inner = Assert.Single(outer.Children);
            Assert.Equal("rect", Assert.Single(inner.Children).Name);
            Assert.Equal(2, doc.Root.Children.Count);
            Assert.False(WarnedAboutUnnesting(doc));
        }

        [Fact]
        public void PairedForeignElement_KeepsItsContentInertAndUnrendered()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20' " + XhtmlDeclarations + ">" +
                "<h:div><rect width='20' height='20' fill='blue'/></h:div>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal((byte)255, result.Bitmap.GetPixel(10, 10).Alpha);
            Assert.True(result.Bitmap.GetPixel(10, 10).Green > 100);
            Assert.True(result.Bitmap.GetPixel(10, 10).Red < 60);
        }

        [Fact]
        public void ForeignObjectHtmlChildren_AreStillTraversed()
        {
            var doc = Parse(
                "<svg " + XhtmlDeclarations + ">" +
                "<foreignObject width='20' height='20'>" +
                "<h:div><rect width='20' height='20' fill='green'/></h:div>" +
                "</foreignObject>" +
                "<rect width='10' height='10'/></svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            var foreignObject = Child(doc.Root, "foreignObject");
            Assert.NotNull(foreignObject);
            var div = Assert.Single(foreignObject.Children);
            Assert.Equal("h:div", div.Name);
            Assert.Equal("rect", Assert.Single(div.Children).Name);
            Assert.Equal(2, doc.Root.Children.Count);
            Assert.False(WarnedAboutUnnesting(doc));
        }

        [Fact]
        public void ForeignObjectContent_StillFailsClosedAsUnsupported()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20' " + XhtmlDeclarations + ">" +
                "<foreignObject width='20' height='20'>" +
                "<h:div style='display: contents'>" +
                "<rect width='20' height='20' fill='green'/>" +
                "</h:div></foreignObject></svg>");

            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.True(result.RequiresFallback);
            Assert.Contains("unsupported-element", result.FallbackReasonCodes);
        }

        [Fact]
        public void MetadataContent_StaysInertAndSwallowsItsOwnSubtree()
        {
            var doc = Parse(
                "<svg " + XhtmlDeclarations + ">" +
                "<metadata><rect width='20' height='20' fill='blue'/><h:div>x</h:div></metadata>" +
                "<rect width='10' height='10'/></svg>",
                out var ok,
                out var fatal);

            Assert.True(ok, fatal);
            var metadata = Child(doc.Root, "metadata");
            Assert.NotNull(metadata);
            Assert.Empty(metadata.Children);
            Assert.Equal(2, doc.Root.Children.Count);
            Assert.Equal("10", Child(doc.Root, "rect").GetAttribute("width"));
            Assert.False(WarnedAboutUnnesting(doc));
        }

        [Fact]
        public void TitleAndDescContent_StayInert()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20' " + XhtmlDeclarations + ">" +
                "<title><rect width='20' height='20' fill='blue'/></title>" +
                "<desc><rect width='20' height='20' fill='blue'/></desc>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(SvgRenderResult.IsAdmissible(result));
            Assert.False(result.RequiresFallback, string.Join("; ", result.Warnings));
            Assert.Equal((byte)255, result.Bitmap.GetPixel(10, 10).Alpha);
            Assert.True(result.Bitmap.GetPixel(10, 10).Green > 100);
            Assert.True(result.Bitmap.GetPixel(10, 10).Red < 60);
        }

        [Fact]
        public void BareSvgNamespaceUnknownElement_StillFailsClosedWithoutTheForeignPath()
        {
            using var result = new FenSvgRenderer().Render(
                "<svg width='20' height='20' xmlns='http://www.w3.org/2000/svg'>" +
                "<base href='http://www.example.com/'/>" +
                "<rect width='20' height='20' fill='green'/></svg>");

            Assert.False(result.Success, result.ErrorMessage);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.Contains("unsupported-element", result.FallbackReasonCodes);
        }

        [Fact]
        public void RepeatedUnclosedForeignElements_StayBoundedAndKeepTheTrailingSibling()
        {
            string body = string.Concat(Enumerable.Repeat("<h:div>", 8000));
            var source = "<svg " + XhtmlDeclarations + ">" + body +
                "<rect width='20' height='20'/></svg>";

            var doc = Parse(source, out var ok, out var fatal);

            Assert.True(ok, fatal);
            Assert.Equal(1, doc.Root.Children.Count(child => child.Name == "rect"));
            Assert.True(WarnedAboutUnnesting(doc));
        }

        private static void AssertFailsClosed(SvgRenderResult result)
        {
            Assert.False(result.Success, result.ErrorMessage);
            Assert.Null(result.Bitmap);
            Assert.Null(result.Picture);
            Assert.False(SvgRenderResult.IsAdmissible(result));
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        private static EngineRender RenderWithReport(string source)
        {
            bool rendered = SvgRenderEngine.TryRender(
                source, SvgRenderLimits.Default, null, null, 0,
                out var picture, out float width, out float height, out string error,
                out _, out IReadOnlyList<string> fallbackReasonCodes, out _,
                out _, out _);

            Assert.True(rendered, error);
            Assert.NotNull(picture);
            var info = new SKImageInfo(
                Math.Max(1, (int)Math.Round(width)),
                Math.Max(1, (int)Math.Round(height)));
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.DrawPicture(picture);
            picture.Dispose();
            return new EngineRender(surface.Snapshot(), fallbackReasonCodes);
        }

        private sealed class EngineRender : IDisposable
        {
            private readonly SKImage _image;

            public EngineRender(SKImage image, IReadOnlyList<string> fallbackReasonCodes)
            {
                _image = image;
                FallbackReasonCodes = fallbackReasonCodes;
            }

            public IReadOnlyList<string> FallbackReasonCodes { get; }

            public SKBitmap Bitmap =>
                SKBitmap.FromImage(_image) ??
                throw new InvalidOperationException("rasterization produced no bitmap");

            public void Dispose() => _image.Dispose();
        }
    }
}
