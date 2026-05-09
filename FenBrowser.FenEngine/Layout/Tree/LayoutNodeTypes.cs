using FenBrowser.Core.Dom.V2;
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
        public BlockBox(LayoutBoxStore store, int storeId) : base(store, storeId) { }

        // Compatibility constructor for tests that create standalone boxes.
        public BlockBox(Node sourceNode, CssComputed style)
            : base(CreateStandaloneStore(sourceNode, style, out var storeId), storeId)
        {
        }

        private static LayoutBoxStore CreateStandaloneStore(Node sourceNode, CssComputed style, out int storeId)
        {
            var store = new LayoutBoxStore();
            storeId = store.CreateBox(sourceNode, style ?? new CssComputed(), LayoutBoxStore.BoxType.Block);
            return store;
        }
    }

    /// <summary>
    /// Represents an inline-level box (e.g., span, strong, em).
    /// Does not break flow; laid out horizontally within an Inline Formatting Context (IFC).
    /// </summary>
    public class InlineBox : LayoutBox
    {
        public InlineBox(LayoutBoxStore store, int storeId) : base(store, storeId) { }
    }

    /// <summary>
    /// A special BlockBox created to wrap a sequence of Inline children when they share a parent with Block children.
    /// Essential for preserving the CSS stricture: "A block container either contains only block-level boxes or only inline-level boxes".
    /// </summary>
    public class AnonymousBlockBox : BlockBox
    {
        public AnonymousBlockBox(LayoutBoxStore store, int storeId) : base(store, storeId) 
        {
        }
    }

    /// <summary>
    /// Represents a leaf node containing text.
    /// Always strictly inside an InlineContext.
    /// </summary>
    public class TextLayoutBox : LayoutBox
    {
        public int StartOffset { get; set; } = 0;
        public int Length { get; set; } = -1;

        public string TextContent 
        {
            get 
            {
                string full = (SourceNode as Text)?.Data ?? "";
                if (Length < 0) return full.Substring(Math.Min(full.Length, StartOffset));
                int len = Math.Min(Length, full.Length - StartOffset);
                return full.Substring(StartOffset, Math.Max(0, len));
            }
        }

        public TextLayoutBox(LayoutBoxStore store, int storeId) : base(store, storeId) { }
        
        public override string ToString() => $"TextLayoutBox \"{TextContent.Replace("\n", "\\n").Replace("\r", "")}\"";
    }
}

