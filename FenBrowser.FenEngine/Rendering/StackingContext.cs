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
            // Iterative DFS to avoid stack overflow on deeply nested positioned layouts.
            var stack = new Stack<(LayoutBox box, StackingContext owner)>();
            foreach (var c in parent.Children) stack.Push((c, ctx));
            // Push in reverse so first child is processed first (LIFO -> reverse).
            var ordered = new Stack<(LayoutBox box, StackingContext owner)>();
            while (stack.Count > 0) ordered.Push(stack.Pop());

            while (ordered.Count > 0)
            {
                var (child, owner) = ordered.Pop();
                bool isPositioned = child.ComputedStyle?.Position != "static";
                bool hasZIndex = child.ComputedStyle?.ZIndex != null;
                bool isOpacity = child.ComputedStyle?.Opacity < 1.0f;

                if ((isPositioned && hasZIndex) || isOpacity)
                {
                    var childCtx = Build(child);
                    if (childCtx.ZIndex < 0) owner.NegativeZ.Add(childCtx);
                    else owner.PositiveZ.Add(childCtx);
                }
                else if (isPositioned && !hasZIndex)
                {
                    // Positioned z-index:auto: create proxy context; queue its children under proxy.
                    var proxy = new StackingContext(child) { ZIndex = 0 };
                    owner.PositiveZ.Add(proxy);
                    var tmp = new Stack<(LayoutBox, StackingContext)>();
                    foreach (var gc in child.Children) tmp.Push((gc, proxy));
                    while (tmp.Count > 0) ordered.Push(tmp.Pop());
                }
                else
                {
                    if (child.ComputedStyle?.Float != "none") owner.FloatLevel.Add(child);
                    else if (child is BlockBox) owner.BlockLevel.Add(child);
                    else owner.InlineLevel.Add(child);

                    var tmp = new Stack<(LayoutBox, StackingContext)>();
                    foreach (var gc in child.Children) tmp.Push((gc, owner));
                    while (tmp.Count > 0) ordered.Push(tmp.Pop());
                }
            }
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
