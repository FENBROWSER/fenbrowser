using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public sealed class TreeWalkerCoreTests
    {
        [Fact]
        public void CurrentNode_CanLeaveRootButTraversalStaysBounded()
        {
            var document = Document.CreateHtmlDocument();
            var root = document.CreateElement("div");
            var child = document.CreateElement("span");
            var outsider = document.CreateElement("p");

            root.AppendChild(child);
            document.Body.Append(root, outsider);

            var walker = document.CreateTreeWalker(root, NodeFilterShow.Element);
            walker.CurrentNode = child;
            Assert.Same(child, walker.CurrentNode);

            walker.CurrentNode = outsider;

            Assert.Same(outsider, walker.CurrentNode);
            Assert.Null(walker.ParentNode());
            Assert.Null(walker.FirstChild());
            Assert.Null(walker.LastChild());
            Assert.Null(walker.PreviousSibling());
            Assert.Null(walker.NextSibling());
            Assert.Null(walker.PreviousNode());
            Assert.Null(walker.NextNode());
        }
    }
}
