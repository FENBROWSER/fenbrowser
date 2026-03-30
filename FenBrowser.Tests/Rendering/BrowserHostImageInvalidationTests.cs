using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    [Collection("Engine Tests")]
    public class BrowserHostImageInvalidationTests
    {
        [Fact]
        public void ImageLoaderRequestRepaint_MarksActiveDomPaintDirty()
        {
            using var host = new BrowserHost();
            object? repaintPayload = new object();
            var repaintRaised = false;

            host.RepaintReady += (_, payload) =>
            {
                repaintRaised = true;
                repaintPayload = payload;
            };
            ImageLoader.RequestRepaint?.Invoke();

            Assert.True(repaintRaised);
            Assert.Null(repaintPayload);
        }

        [Fact]
        public void ImageLoaderRequestRelayout_MarksActiveDomLayoutAndPaintDirty()
        {
            using var host = new BrowserHost();
            var document = Document.CreateHtmlDocument();
            var root = document.DocumentElement;
            Assert.NotNull(root);
            SetActiveDom(host, root);
            Assert.Same(root, host.GetDomRoot());
            object? repaintPayload = null;
            var repaintRaised = false;

            host.RepaintReady += (_, payload) =>
            {
                repaintRaised = true;
                repaintPayload = payload;
            };
            ImageLoader.RequestRelayout?.Invoke();

            Assert.True(repaintRaised);
            Assert.Same(root, repaintPayload);
        }

        [Fact]
        public async Task ImageLoaderPrewarm_BatchesRelayoutSignals_ForBurstLoads()
        {
            ImageLoader.ClearCache();
            var originalRelayout = ImageLoader.RequestRelayout;
            var originalRepaint = ImageLoader.RequestRepaint;
            var relayoutCount = 0;

            try
            {
                ImageLoader.RequestRepaint = () => { };
                ImageLoader.RequestRelayout = () => Interlocked.Increment(ref relayoutCount);

                var imageBytes = CreatePngBytes();

                await ImageLoader.PrewarmImageAsync("https://test.local/a.png", new MemoryStream(imageBytes));
                await ImageLoader.PrewarmImageAsync("https://test.local/b.png", new MemoryStream(imageBytes));

                await Task.Delay(250);

                Assert.Equal(1, Volatile.Read(ref relayoutCount));
            }
            finally
            {
                ImageLoader.RequestRelayout = originalRelayout;
                ImageLoader.RequestRepaint = originalRepaint;
                ImageLoader.ClearCache();
            }
        }

        private static void SetActiveDom(BrowserHost host, Node node)
        {
            var engineField = typeof(BrowserHost).GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(engineField);

            var engine = engineField!.GetValue(host);
            Assert.NotNull(engine);

            var activeDomField = engine.GetType().GetField("_activeDom", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(activeDomField);
            activeDomField!.SetValue(engine, node);
        }

        private static byte[] CreatePngBytes()
        {
            using var bitmap = new SKBitmap(2, 2);
            bitmap.Erase(SKColors.DeepSkyBlue);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }
}
