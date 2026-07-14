using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public sealed class ChildNodeListTests
    {
        [Fact]
        public void ChildNodes_ReturnsSameLiveCollectionAcrossMutations()
        {
            var document = Document.CreateHtmlDocument();
            var parent = document.CreateElement("div");
            NodeList childNodes = parent.ChildNodes;

            parent.AppendChild(document.CreateElement("span"));

            Assert.Same(childNodes, parent.ChildNodes);
            Assert.Equal(1, childNodes.Length);

            parent.AppendChild(document.CreateElement("strong"));

            Assert.Equal(2, childNodes.Length);
        }

        [Fact]
        public void ChildNodes_RepeatedAccess_DoesNotAllocate()
        {
            var document = Document.CreateHtmlDocument();
            var parent = document.CreateElement("div");
            NodeList childNodes = parent.ChildNodes;

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
            {
                childNodes = parent.ChildNodes;
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            GC.KeepAlive(childNodes);

            Assert.Equal(0, allocated);
        }
    }
}
