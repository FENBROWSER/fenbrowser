using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public sealed class TreeWalkerCoreTests
    {
        [Fact]
        // DOM 6.2: currentNode may be set outside root, and traversal then starts
        // from there - the algorithms stop only on reaching root itself (WPT
        // dom/traversal/TreeWalker-currentNode.html). Each call moves
        // currentNode, so it is reset before the next.
        public void CurrentNode_CanLeaveRootAndTraversalStartsFromIt()
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

            Assert.Same(document.Body, walker.ParentNode());
            walker.CurrentNode = outsider;
            Assert.Null(walker.FirstChild());
            Assert.Null(walker.LastChild());
            Assert.Same(root, walker.PreviousSibling());
            walker.CurrentNode = outsider;
            Assert.Null(walker.NextSibling());
            Assert.Same(child, walker.PreviousNode());
            walker.CurrentNode = outsider;
            Assert.Null(walker.NextNode());
            Assert.Same(outsider, walker.CurrentNode);
        }
    }
}
