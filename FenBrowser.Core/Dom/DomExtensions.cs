using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.Core.Dom
{
    public static class DomExtensions
    {
        public static IEnumerable<Node> Descendants(this Node node)
        {
            if (node == null) yield break;
            foreach (var child in node.Children)
            {
                yield return child;
                foreach (var descendant in child.Descendants())
                {
                    yield return descendant;
                }
            }
        }
        
        public static IEnumerable<Element> DescendantsAndSelf(this Element element)
        {
            if (element == null) yield break;
            yield return element;
            foreach (var child in element.Descendants().OfType<Element>())
            {
                yield return child;
            }
        }
        
        public static IEnumerable<Node> DescendantsAndSelf(this Node node)
        {
             if (node == null) yield break;
             yield return node;
             foreach (var d in node.Descendants()) yield return d;
        }

        public static Element ParentElement(this Node node)
        {
            return node.Parent as Element;
        }
        
        /// <summary>Get children that are Elements (LiteElement compatibility)</summary>
        public static IEnumerable<Element> ElementChildren(this Node node)
        {
            if (node == null) return Enumerable.Empty<Element>();
            return node.Children.OfType<Element>();
        }
        
        /// <summary>Get all Element ancestors (LiteElement compatibility)</summary>
        public static IEnumerable<Node> Ancestors(this Node node)
        {
            for (var p = node?.Parent; p != null; p = p.Parent)
                yield return p;
        }
        
        /// <summary>Remove all children from node (LiteElement compatibility)</summary>
        public static void RemoveAllChildren(this Node node)
        {
            if (node == null) return;
            node.Children.Clear();
        }
    }
}
