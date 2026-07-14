using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public sealed class PaintTreeTraversalTests
    {
        [Fact]
        public void HasSingleRenderableChild_DoesNotAllocateAndPreservesSemantics()
        {
            var document = Document.CreateHtmlDocument();
            var parent = document.CreateElement("div");
            parent.AppendChild(new Text("rendered", document));

            Assert.True(NewPaintTreeBuilder.HasSingleRenderableChild(parent));

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            bool result = false;
            for (int i = 0; i < 10_000; i++)
            {
                result = NewPaintTreeBuilder.HasSingleRenderableChild(parent);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.True(result);
            Assert.Equal(0, allocated);

            parent.AppendChild(new Text(" ", document));
            parent.AppendChild(document.CreateElement("style"));
            Assert.True(NewPaintTreeBuilder.HasSingleRenderableChild(parent));

            parent.AppendChild(document.CreateElement("span"));
            Assert.False(NewPaintTreeBuilder.HasSingleRenderableChild(parent));
        }
    }
}
