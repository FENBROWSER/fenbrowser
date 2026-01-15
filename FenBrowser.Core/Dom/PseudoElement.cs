using FenBrowser.Core.Css;

namespace FenBrowser.Core.Dom
{
    /// <summary>
    /// Represents a generated pseudo-element (::before, ::after).
    /// Used by the layout engine to inject content into the rendering tree.
    /// </summary>
    public class PseudoElement : Element
    {
        public CssComputed ComputedStyle { get; set; }
        public string PseudoType { get; set; } // "before", "after", "marker"
        public Element OriginatingElement { get; set; }

        public PseudoElement(Element originatingElement, string pseudoType, CssComputed style) 
            : base(pseudoType.ToUpperInvariant()) // Tag name like "BEFORE", "AFTER"
        {
            OriginatingElement = originatingElement;
            PseudoType = pseudoType;
            ComputedStyle = style;
        }
    }
}
