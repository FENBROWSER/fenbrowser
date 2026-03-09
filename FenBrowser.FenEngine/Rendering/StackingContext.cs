using System.Collections.Generic;
using System.Linq;
using FenBrowser.FenEngine.Layout.Tree;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Represents a CSS Stacking Context (z-index layer).
    /// </summary>
    public class StackingContext
    {
        public LayoutBox Root { get; set; }
        public int ZIndex { get; set; }
        
        // Layers
        public List<StackingContext> NegativeZ { get; } = new List<StackingContext>();
        public List<LayoutBox> BlockLevel { get; } = new List<LayoutBox>();
        public List<LayoutBox> FloatLevel { get; } = new List<LayoutBox>();
        public List<LayoutBox> InlineLevel { get; } = new List<LayoutBox>();
        public List<StackingContext> PositiveZ { get; } = new List<StackingContext>();

        public StackingContext(LayoutBox root)
        {
            Root = root;
            var style = root.ComputedStyle;
            if (style != null && style.ZIndex.HasValue) ZIndex = style.ZIndex.Value;
            else ZIndex = 0; // Auto treats as 0 for stacking level
        }

        public static StackingContext Build(LayoutBox root)
        {
            var ctx = new StackingContext(root);
            ProcessChildren(root, ctx);
            // Sort once at build time so GetPaintOrder() never mutates.
            ctx.NegativeZ.Sort((a, b) => a.ZIndex.CompareTo(b.ZIndex));
            ctx.PositiveZ.Sort((a, b) => a.ZIndex.CompareTo(b.ZIndex));
            return ctx;
        }

        private static void ProcessChildren(LayoutBox parent, StackingContext ctx)
        {
            foreach (var child in parent.Children)
            {
                // Does this child establish a new Stacking Context?
                // Rules: root, z-index != auto && positioned, opacity < 1, transform != none, etc.
                bool isPositioned = child.ComputedStyle?.Position != "static";
                bool hasZIndex = child.ComputedStyle?.ZIndex != null;
                bool isOpacity = child.ComputedStyle?.Opacity < 1.0f;
                // Simplified check
                
                if ((isPositioned && hasZIndex) || isOpacity)
                {
                    // New Context
                    var childCtx = Build(child);
                    if (childCtx.ZIndex < 0) ctx.NegativeZ.Add(childCtx);
                    else ctx.PositiveZ.Add(childCtx);
                }
                else
                {
                    // Same Context
                    // Classify
                    if (isPositioned && !hasZIndex) contextuallyPositioned(child, ctx); // z-index: auto positioned
                    else if (child.ComputedStyle?.Float != "none") ctx.FloatLevel.Add(child);
                    else if (child is BlockBox) ctx.BlockLevel.Add(child);
                    else ctx.InlineLevel.Add(child); // Inlines/Texts
                    
                    // Recurse (unless it was a new context, which processed its own subtree)
                    ProcessChildren(child, ctx);
                }
            }
        }
        
        private static void contextuallyPositioned(LayoutBox child, StackingContext ctx)
        {
             // Positioned elements with z-index: auto paint *after* floats but before positive Z?
             // Actually, "Positioned descendants with z-index: auto" are layer 6 (Positive Z > 0 is layer 7).
             // Layer 6 is higher than inline/float.
             // We'll treat them as a pseudo-positive context with z=0 (but sorted by tree order).
             // For simplicity, add to PositiveZ with z=0?
             // Or create specific list.
             // Let's create a proxy StackingContext with z=0.
             var proxy = new StackingContext(child) { ZIndex = 0 };
             ProcessChildren(child, proxy);
             ctx.PositiveZ.Add(proxy);
        }

        public IEnumerable<LayoutBox> GetPaintOrder()
        {
            // Paint order per CSS2.1 §E.2 appendix (Elaborate description of Stacking Contexts).
            // Lists are pre-sorted during Build(); no mutation happens here.

            // Step 2: Negative Z child contexts (most-negative first).
            foreach (var c in NegativeZ)
            {
                yield return c.Root;
                foreach (var b in c.GetPaintOrder()) yield return b;
            }

            // Step 3: Block-level non-positioned descendants.
            foreach (var b in BlockLevel) yield return b;

            // Step 4: Floating descendants.
            foreach (var b in FloatLevel) yield return b;

            // Step 5: Inline-level (and positioned z-index:auto) descendants.
            foreach (var b in InlineLevel) yield return b;

            // Step 6+7: Positive-Z child contexts (z-index:auto proxied as z=0, then ascending).
            foreach (var c in PositiveZ)
            {
                yield return c.Root;
                foreach (var b in c.GetPaintOrder()) yield return b;
            }
        }
    }
}
