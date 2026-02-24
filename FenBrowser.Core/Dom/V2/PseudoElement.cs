using FenBrowser.Core.Css;
using System.Collections.Generic;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// Represents a generated pseudo-element (::before, ::after).
    /// Used by the layout engine to inject content into the rendering tree.
    /// </summary>
    public class PseudoElement : Element
    {
        public new FenBrowser.Core.Css.CssComputed ComputedStyle { get; set; }
        public string PseudoType { get; set; } // "before", "after", "marker"
        public Element OriginatingElement { get; set; }

        // Pseudo-elements commonly generate text nodes; expose full child nodes for layout/tests.
        public new List<Node> Children
        {
            get
            {
                var list = new List<Node>();
                var nodes = ChildNodes;
                for (int i = 0; i < nodes.Length; i++)
                {
                    list.Add(nodes[i]);
                }

                return list;
            }
        }

        public PseudoElement(Element originatingElement, string pseudoType, CssComputed style) 
            : base(pseudoType.ToUpperInvariant()) // Tag name like "BEFORE", "AFTER"
        {
            OriginatingElement = originatingElement;
            PseudoType = pseudoType;
            ComputedStyle = style;
        }
    }
}
