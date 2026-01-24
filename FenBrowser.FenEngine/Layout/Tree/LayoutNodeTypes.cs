using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;

namespace FenBrowser.FenEngine.Layout.Tree
{
    /// <summary>
    /// Represents a box that participates in a Block Formatting Context (BFC) 
    /// or establishes a new BFC for its children.
    /// Corresponds to 'display: block', 'display: flex', 'display: grid', etc.
    /// </summary>
    public class BlockBox : LayoutBox
    {
        public BlockBox(Node sourceNode, CssComputed style) : base(sourceNode, style) { }
    }

    /// <summary>
    /// Represents an inline-level box (e.g., span, strong, em).
    /// Does not break flow; laid out horizontally within an Inline Formatting Context (IFC).
    /// </summary>
    public class InlineBox : LayoutBox
    {
        public InlineBox(Node sourceNode, CssComputed style) : base(sourceNode, style) { }
    }

    /// <summary>
    /// A special BlockBox created to wrap a sequence of Inline children when they share a parent with Block children.
    /// Essential for preserving the CSS stricture: "A block container either contains only block-level boxes or only inline-level boxes".
    /// </summary>
    public class AnonymousBlockBox : BlockBox
    {
        public AnonymousBlockBox() : base(null, null) 
        {
            // Anonymous boxes typically inherit styles from parent, 
            // but we'll handle style resolution in the builder/fixup phase.
        }
    }

    /// <summary>
    /// Represents a leaf node containing text.
    /// Always strictly inside an InlineContext.
    /// </summary>
    public class TextLayoutBox : LayoutBox
    {
        public string TextContent => (SourceNode as Text)?.Data ?? "";

        public TextLayoutBox(Text sourceNode, CssComputed style) : base(sourceNode, style) { }
        
        public override string ToString() => $"TextLayoutBox \"{TextContent.Replace("\n", "\\n").Replace("\r", "")}\"";
    }
}
