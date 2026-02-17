using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.Core.Dom.V2
{
    public static class DomExtensions
    {
        public static IEnumerable<Node> Descendants(this Node node)
        {
            return node.Descendants();
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
             return node.SelfAndDescendants();
        }

        // ParentElement is already a property on Node in V2, but for compatibility with method calls if any:
        /* 
        public static Element ParentElement(this Node node)
        {
            return node.ParentElement;
        }
        */
        
        /// <summary>Get children that are Elements (LiteElement compatibility)</summary>
        public static IEnumerable<Element> ElementChildren(this Node node)
        {
            if (node == null) yield break;
            
            // Optimization for ContainerNode which has list
            if (node is ContainerNode container)
            {
                for (var child = container.FirstChild; child != null; child = child.NextSibling)
                {
                    if (child is Element el)
                        yield return el;
                }
            }
        }
        
        /// <summary>Get all Element ancestors (LiteElement compatibility)</summary>
        public static IEnumerable<Node> Ancestors(this Node node)
        {
            return node.Ancestors();
        }
        
        /// <summary>Remove all children from node (LiteElement compatibility)</summary>
        public static void RemoveAllChildren(this Node node)
        {
            if (node is ContainerNode container)
            {
                container.RemoveAllChildren();
            }
        }
    }
}
