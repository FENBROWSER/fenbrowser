using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Interaction;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class ResponsiveImageSourceSelectionTests
    {
        [Fact]
        public async System.Threading.Tasks.Task PaintTreeBuilder_ImgSrcSet_UsesSelectedCandidateInsteadOfFallbackSrc()
        {
            ImageLoader.ClearCache();
            var previousFetcher = ImageLoader.FetchBytesAsync;
            try
            {
                const string selectedUrl = "https://example.test/hero@1_5x.png";
                const string fallbackUrl = "https://example.test/hero.png";
                string requestedUrl = null;
                var pngBytes = CreatePngBytes(8, 8, SKColors.Red);
                ImageLoader.FetchBytesAsync = uri =>
                {
                    requestedUrl = uri?.AbsoluteUri;
                    return System.Threading.Tasks.Task.FromResult(pngBytes);
                };

                var image = new Element("img");
                image.SetAttribute("src", fallbackUrl);
                image.SetAttribute("srcset", "https://example.test/hero@1_5x.png 1.5x");

                var styles = new Dictionary<Node, CssComputed>
                {
                    [image] = new CssComputed { Display = "block", Width = 8, Height = 8 }
                };

                var boxes = new Dictionary<Node, BoxModel>();
                var box = BoxModel.FromContentBox(0, 0, 8, 8);
                boxes[image] = box;

                var builderType = typeof(SkiaDomRenderer).Assembly.GetType("FenBrowser.FenEngine.Rendering.NewPaintTreeBuilder");
                Assert.NotNull(builderType);

                var ctor = builderType!.GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    new[]
                    {
                        typeof(IReadOnlyDictionary<Node, BoxModel>),
                        typeof(IReadOnlyDictionary<Node, CssComputed>),
                        typeof(float),
                        typeof(float),
                        typeof(ScrollManager),
                        typeof(string)
                    },
                    modifiers: null);
                Assert.NotNull(ctor);

                var builder = ctor!.Invoke(new object[]
                {
                    boxes,
                    styles,
                    16f,
                    16f,
                    new ScrollManager(),
                    "https://example.test/page"
                });

                var method = builderType.GetMethod(
                    "BuildImageOrSvgNode",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(method);

                var imageNode = method!.Invoke(builder, new object[] { image, box, styles[image], false, false }) as ImagePaintNode;
                Assert.NotNull(imageNode);

                var loaded = SpinWait.SpinUntil(
                    () => requestedUrl != null && ImageLoader.ContainsCachedImage(selectedUrl),
                    System.TimeSpan.FromSeconds(2));

                Assert.True(loaded);
                Assert.Equal(selectedUrl, requestedUrl);
                Assert.False(ImageLoader.ContainsCachedImage(fallbackUrl));
            }
            finally
            {
                ImageLoader.FetchBytesAsync = previousFetcher;
                ImageLoader.ClearCache();
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task PaintTreeBuilder_PictureSource_RespectsMediaQueries()
        {
            ImageLoader.ClearCache();
            var previousFetcher = ImageLoader.FetchBytesAsync;
            try
            {
                const string desktopUrl = "https://example.test/flower-desktop.png";
                const string mobileUrl = "https://example.test/flower-mobile.png";
                const string fallbackUrl = "https://example.test/flower-fallback.png";
                string requestedUrl = null;
                var pngBytes = CreatePngBytes(8, 8, SKColors.Blue);
                ImageLoader.FetchBytesAsync = uri =>
                {
                    requestedUrl = uri?.AbsoluteUri;
                    return System.Threading.Tasks.Task.FromResult(pngBytes);
                };

                var picture = new Element("picture");
                var desktopSource = new Element("source");
                desktopSource.SetAttribute("media", "(min-width: 1024px)");
                desktopSource.SetAttribute("srcset", desktopUrl + " 1x");

                var mobileSource = new Element("source");
                mobileSource.SetAttribute("media", "(max-width: 599px)");
                mobileSource.SetAttribute("srcset", mobileUrl + " 1x");

                var image = new Element("img");
                image.SetAttribute("src", fallbackUrl);

                picture.AppendChild(desktopSource);
                picture.AppendChild(mobileSource);
                picture.AppendChild(image);

                var styles = new Dictionary<Node, CssComputed>
                {
                    [picture] = new CssComputed { Display = "block", Width = 8, Height = 8 },
                    [image] = new CssComputed { Display = "block", Width = 8, Height = 8 }
                };

                var boxes = new Dictionary<Node, BoxModel>();
                var box = BoxModel.FromContentBox(0, 0, 8, 8);
                boxes[picture] = box;
                boxes[image] = box;

                var builderType = typeof(SkiaDomRenderer).Assembly.GetType("FenBrowser.FenEngine.Rendering.NewPaintTreeBuilder");
                Assert.NotNull(builderType);

                var ctor = builderType!.GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    new[]
                    {
                        typeof(IReadOnlyDictionary<Node, BoxModel>),
                        typeof(IReadOnlyDictionary<Node, CssComputed>),
                        typeof(float),
                        typeof(float),
                        typeof(ScrollManager),
                        typeof(string)
                    },
                    modifiers: null);
                Assert.NotNull(ctor);

                var builder = ctor!.Invoke(new object[]
                {
                    boxes,
                    styles,
                    500f,
                    800f,
                    new ScrollManager(),
                    "https://example.test/page"
                });

                var method = builderType.GetMethod(
                    "BuildImageOrSvgNode",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(method);

                var imageNode = method!.Invoke(builder, new object[] { image, box, styles[image], false, false }) as ImagePaintNode;
                Assert.NotNull(imageNode);

                var loaded = SpinWait.SpinUntil(
                    () => requestedUrl != null && ImageLoader.ContainsCachedImage(mobileUrl),
                    System.TimeSpan.FromSeconds(2));

                Assert.True(loaded);
                Assert.Equal(mobileUrl, requestedUrl);
                Assert.False(ImageLoader.ContainsCachedImage(desktopUrl));
                Assert.False(ImageLoader.ContainsCachedImage(fallbackUrl));
            }
            finally
            {
                ImageLoader.FetchBytesAsync = previousFetcher;
                ImageLoader.ClearCache();
            }
        }

        [Fact]
        public void Selector_PictureDataEmptyPlaceholder_FallsBackToImgSrc()
        {
            var picture = new Element("picture");
            picture.SetAttribute("data-anim-lazy-image", string.Empty);

            var placeholderSource = new Element("source");
            placeholderSource.SetAttribute("data-empty", string.Empty);
            placeholderSource.SetAttribute("media", "(min-width:0px)");
            placeholderSource.SetAttribute("srcset", "data:image/gif;base64,R0lGODlhAQABAHAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");

            const string fallbackUrl = "/images/hero-real.png";
            var image = new Element("img");
            image.SetAttribute("src", fallbackUrl);

            picture.AppendChild(placeholderSource);
            picture.AppendChild(image);

            var selectorType = typeof(SkiaDomRenderer).Assembly.GetType("FenBrowser.FenEngine.Rendering.ResponsiveImageSourceSelector");
            Assert.NotNull(selectorType);

            var method = selectorType!.GetMethod(
                "PickCurrentImageSource",
                BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);

            var selected = method!.Invoke(null, new object[] { image, 1280d, 720d, 1d }) as string;
            Assert.Equal(fallbackUrl, selected);
        }

        [Fact]
        public void Selector_ImgDataSrcAndDataSrcSet_AreUsedWhenStandardAttrsMissing()
        {
            const string dataSrc = "https://example.test/fallback-data-src.png";
            const string dataSrcSet = "https://example.test/hero-640.png 640w, https://example.test/hero-1280.png 1280w";

            var image = new Element("img");
            image.SetAttribute("data-src", dataSrc);
            image.SetAttribute("data-srcset", dataSrcSet);

            var selectorType = typeof(SkiaDomRenderer).Assembly.GetType("FenBrowser.FenEngine.Rendering.ResponsiveImageSourceSelector");
            Assert.NotNull(selectorType);

            var method = selectorType!.GetMethod(
                "PickCurrentImageSource",
                BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);

            var selected = method!.Invoke(null, new object[] { image, 1100d, 700d, 1d }) as string;
            Assert.Equal("https://example.test/hero-1280.png", selected);
        }

        private static MemoryStream CreatePngStream(int width, int height, SKColor color)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            surface.Canvas.Clear(color);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return new MemoryStream(data.ToArray(), writable: false);
        }

        private static byte[] CreatePngBytes(int width, int height, SKColor color)
        {
            using var stream = CreatePngStream(width, height, color);
            return stream.ToArray();
        }
    }
}
