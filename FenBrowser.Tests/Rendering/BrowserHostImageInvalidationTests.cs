using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
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
            var document = Document.CreateHtmlDocument();
            var root = document.DocumentElement;
            Assert.NotNull(root);
            SetActiveDom(host, root);

            root.ClearDirty(InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);

            ImageLoader.RequestRepaint?.Invoke();

            Assert.True(root.PaintDirty);
            Assert.False(root.LayoutDirty);
        }

        [Fact]
        public void ImageLoaderRequestRelayout_MarksActiveDomLayoutAndPaintDirty()
        {
            using var host = new BrowserHost();
            var document = Document.CreateHtmlDocument();
            var root = document.DocumentElement;
            Assert.NotNull(root);
            SetActiveDom(host, root);

            root.ClearDirty(InvalidationKind.Style | InvalidationKind.Layout | InvalidationKind.Paint);

            ImageLoader.RequestRelayout?.Invoke();

            Assert.True(root.LayoutDirty);
            Assert.True(root.PaintDirty);
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
    }
}
