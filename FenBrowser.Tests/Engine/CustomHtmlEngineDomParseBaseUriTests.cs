using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class CustomHtmlEngineDomParseBaseUriTests
    {
        [Fact]
        public async Task RunDomParseAsync_AssignsBaseUri_ForRelativeImageIntrinsicLookup()
        {
            ImageLoader.ClearCache();
            try
            {
                const string absoluteImageUrl = "https://example.test/images/hero.png";
                await ImageLoader.PrewarmImageAsync(
                    absoluteImageUrl,
                    CreatePngStream(40, 20, SKColors.CadetBlue));

                using var engine = new CustomHtmlEngine();
                const string html = "<!doctype html><html><body><img src='/images/hero.png'></body></html>";
                var baseUri = new Uri("https://example.test/page");

                var parseMethod = typeof(CustomHtmlEngine).GetMethod(
                    "RunDomParseAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.NotNull(parseMethod);

                var parseTaskObj = parseMethod.Invoke(engine, new object[] { html, baseUri });
                var parseTask = Assert.IsAssignableFrom<Task>(parseTaskObj);
                await parseTask;

                var resultProperty = parseTask.GetType().GetProperty("Result");
                Assert.NotNull(resultProperty);
                var parseResult = resultProperty.GetValue(parseTask);
                Assert.NotNull(parseResult);

                var domProperty = parseResult.GetType().GetProperty("Dom");
                Assert.NotNull(domProperty);
                var dom = Assert.IsAssignableFrom<Node>(domProperty.GetValue(parseResult));

                var document = dom as Document ?? dom.OwnerDocument;
                Assert.NotNull(document);
                Assert.Equal(baseUri.AbsoluteUri, document.URL);
                Assert.Equal(baseUri.AbsoluteUri, document.BaseURI);

                var image = dom.Descendants().OfType<Element>()
                    .First(e => string.Equals(e.TagName, "img", StringComparison.OrdinalIgnoreCase));

                bool hasIntrinsic = ReplacedElementSizing.TryResolveIntrinsicSizeFromElement(
                    "IMG",
                    image,
                    out float intrinsicWidth,
                    out float intrinsicHeight);

                Assert.True(hasIntrinsic);
                Assert.Equal(40f, intrinsicWidth);
                Assert.Equal(20f, intrinsicHeight);
            }
            finally
            {
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
    }
}
