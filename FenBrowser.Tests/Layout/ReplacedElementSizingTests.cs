using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class ReplacedElementSizingTests
    {
        [Fact]
        public void ResolveReplacedSize_Svg_DefaultsTo300x150()
        {
            var size = ReplacedElementSizing.ResolveReplacedSize(
                "SVG",
                new CssComputed(),
                new SKSize(float.PositiveInfinity, float.PositiveInfinity),
                intrinsicWidth: 0f,
                intrinsicHeight: 0f,
                attributeWidth: 0f,
                attributeHeight: 0f,
                constrainAutoToAvailableWidth: false);

            Assert.Equal(300f, size.Width);
            Assert.Equal(150f, size.Height);
        }

        [Fact]
        public void ResolveReplacedSize_UsesAspectRatio_WhenOneDimensionSpecified()
        {
            var style = new CssComputed
            {
                Width = 320,
                AspectRatio = 16.0 / 9.0
            };

            var size = ReplacedElementSizing.ResolveReplacedSize(
                "IMG",
                style,
                new SKSize(float.PositiveInfinity, float.PositiveInfinity),
                intrinsicWidth: 0f,
                intrinsicHeight: 0f,
                attributeWidth: 0f,
                attributeHeight: 0f,
                constrainAutoToAvailableWidth: false);

            Assert.Equal(320f, size.Width);
            Assert.Equal(180f, size.Height, 2);
        }

        [Fact]
        public void ResolveReplacedSize_PreservesAttributeSize_WhenAutoConstraintEnabled()
        {
            var size = ReplacedElementSizing.ResolveReplacedSize(
                "IMG",
                new CssComputed(),
                new SKSize(500, 400),
                intrinsicWidth: 0f,
                intrinsicHeight: 0f,
                attributeWidth: 1200f,
                attributeHeight: 600f,
                constrainAutoToAvailableWidth: true);

            Assert.Equal(1200f, size.Width);
            Assert.Equal(600f, size.Height);
        }

        [Fact]
        public void MinimalLayoutComputer_MeasureCanvasWithoutAttrs_UsesSpecDefault()
        {
            var canvas = new Element("CANVAS");
            var styles = new Dictionary<Node, CssComputed>
            {
                [canvas] = new CssComputed { Display = "inline" }
            };

            var computer = new MinimalLayoutComputer(styles, 1024, 768);
            var metrics = computer.Measure(canvas, new SKSize(1024, 768));

            Assert.Equal(300f, metrics.MaxChildWidth);
            Assert.Equal(150f, metrics.ContentHeight);
        }

        [Fact]
        public void MinimalLayoutComputer_MeasureSvgViewBox_UsesIntrinsicDimensions()
        {
            var svg = new Element("SVG");
            svg.SetAttribute("viewBox", "0 0 640 320");

            var styles = new Dictionary<Node, CssComputed>
            {
                [svg] = new CssComputed { Display = "inline" }
            };

            var computer = new MinimalLayoutComputer(styles, 1024, 768);
            var metrics = computer.Measure(svg, new SKSize(1024, 768));

            Assert.Equal(640f, metrics.MaxChildWidth);
            Assert.Equal(320f, metrics.ContentHeight);
        }

        [Fact]
        public void TryResolveIntrinsicSizeFromElement_SvgViewBox_ReturnsViewBoxSize()
        {
            var svg = new Element("SVG");
            svg.SetAttribute("viewBox", "0 0 24 24");

            bool ok = ReplacedElementSizing.TryResolveIntrinsicSizeFromElement("SVG", svg, out float width, out float height);

            Assert.True(ok);
            Assert.Equal(24f, width);
            Assert.Equal(24f, height);
        }

        [Fact]
        public void TryResolveIntrinsicSizeFromElement_MaterialIconViewBox_UsesIconFallback()
        {
            var svg = new Element("SVG");
            svg.SetAttribute("viewBox", "0 -960 960 960");
            svg.SetAttribute("aria-hidden", "true");
            svg.SetAttribute("focusable", "false");

            bool ok = ReplacedElementSizing.TryResolveIntrinsicSizeFromElement("SVG", svg, out float width, out float height);

            Assert.True(ok);
            Assert.Equal(24f, width);
            Assert.Equal(24f, height);
        }

        [Fact]
        public void MinimalLayoutComputer_ArrangeFlexWithSvgViewBox_DoesNotFallbackTo300x150()
        {
            var container = new Element("DIV");
            var svg = new Element("SVG");
            svg.SetAttribute("viewBox", "0 0 24 24");
            container.AppendChild(svg);

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed
                {
                    Display = "flex",
                    AlignItems = "flex-start",
                    Width = 200,
                    Height = 80
                },
                [svg] = new CssComputed
                {
                    Display = "inline-block"
                }
            };

            var computer = new MinimalLayoutComputer(styles, 800, 600);
            computer.Arrange(container, new SKRect(0, 0, 200, 80));
            var svgBox = computer.GetBox(svg);

            Assert.NotNull(svgBox);
            Assert.True(svgBox.ContentBox.Width <= 32f, $"Expected icon-scale width, got {svgBox.ContentBox.Width}");
            Assert.True(svgBox.ContentBox.Height <= 32f, $"Expected icon-scale height, got {svgBox.ContentBox.Height}");
        }

        [Fact]
        public void TryResolveIntrinsicSizeFromElement_ObjectDataUrl_ReturnsBitmapSize()
        {
            var doc = Document.CreateHtmlDocument();
            doc.URL = "http://acid3.acidtests.org/";
            doc.BaseURI = doc.URL;

            var obj = doc.CreateElement("object");
            obj.SetAttribute("data", CreatePngDataUrl(12, 18, SKColors.Red));

            bool ok = ReplacedElementSizing.TryResolveIntrinsicSizeFromElement("OBJECT", obj, out float width, out float height);

            Assert.True(ok);
            Assert.Equal(12f, width);
            Assert.Equal(18f, height);
        }

        [Fact]
        public void TryResolveIntrinsicSizeFromElement_ImgDataUrl_ReturnsBitmapSize()
        {
            var doc = Document.CreateHtmlDocument();
            doc.URL = "http://acid2.acidtests.org/";
            doc.BaseURI = doc.URL;

            var img = doc.CreateElement("img");
            img.SetAttribute("src", CreatePngDataUrl(64, 64, SKColors.Red));

            bool ok = ReplacedElementSizing.TryResolveIntrinsicSizeFromElement("IMG", img, out float width, out float height);

            Assert.True(ok);
            Assert.Equal(64f, width);
            Assert.Equal(64f, height);
        }

        [Fact]
        public async Task MinimalLayoutComputer_MeasureImgInPicture_UsesSelectedSourceIntrinsicDimensions()
        {
            ImageLoader.ClearCache();

            try
            {
                const string selectedUrl = "https://example.test/responsive/selected.png";
                const string fallbackUrl = "https://example.test/responsive/fallback.png";

                await ImageLoader.PrewarmImageAsync(selectedUrl, CreatePngStream(80, 40, SKColors.Red));
                await ImageLoader.PrewarmImageAsync(fallbackUrl, CreatePngStream(20, 20, SKColors.Blue));

                var picture = new Element("picture");
                var source = new Element("source");
                source.SetAttribute("media", "(min-width:0px)");
                source.SetAttribute("srcset", selectedUrl + " 1x");

                var image = new Element("img");
                image.SetAttribute("src", fallbackUrl);

                picture.AppendChild(source);
                picture.AppendChild(image);

                var styles = new Dictionary<Node, CssComputed>
                {
                    [picture] = new CssComputed { Display = "block" },
                    [image] = new CssComputed { Display = "inline" }
                };

                var computer = new MinimalLayoutComputer(styles, 1024, 768, "https://example.test/");
                var metrics = computer.Measure(image, new SKSize(1024, 768));

                Assert.Equal(80f, metrics.MaxChildWidth);
                Assert.Equal(40f, metrics.ContentHeight);
            }
            finally
            {
                ImageLoader.ClearCache();
            }
        }

        private static string CreatePngDataUrl(int width, int height, SKColor color)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            surface.Canvas.Clear(color);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
        }

        private static MemoryStream CreatePngStream(int width, int height, SKColor color)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            surface.Canvas.Clear(color);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return new MemoryStream(data.ToArray(), writable: false);
        }
    }
}
