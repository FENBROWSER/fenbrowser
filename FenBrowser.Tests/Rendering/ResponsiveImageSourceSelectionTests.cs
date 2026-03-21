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
