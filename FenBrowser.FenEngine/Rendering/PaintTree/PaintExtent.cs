using System;
using System.Runtime.CompilerServices;
using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// A conservative rectangle around everything a paint node and its subtree can draw,
    /// in the node's own coordinate space (before its own <see cref="PaintNodeBase.Transform"/>).
    /// Grouping nodes (opacity groups, filtered stacking contexts) carry their element's
    /// box as <see cref="PaintNodeBase.Bounds"/>, not the extent of what they paint, so the
    /// rasterizer could neither cull them nor size their Skia layers; every such group
    /// opened an unbounded, canvas-sized layer even when entirely offscreen.
    /// Paint nodes are immutable once published, so an extent is computed once per node
    /// and shared by every tree that keeps the node.
    /// </summary>
    internal static class PaintExtent
    {
        /// <summary>
        /// Slack around the computed extent for ink that leaves a node's box: glyph
        /// overhang, antialiasing, stroked outlines.
        /// </summary>
        public const float InkMargin = 64f;

        private sealed class Entry
        {
            public SKRect Extent;
            public bool Bounded;
        }

        private static readonly ConditionalWeakTable<PaintNodeBase, Entry> s_cache = new();

        /// <summary>
        /// The node's paint extent, or false when it cannot be bounded (a filter whose
        /// reach is unknown somewhere in the subtree).
        /// </summary>
        public static bool TryGet(PaintNodeBase node, out SKRect extent)
        {
            var entry = Compute(node);
            extent = entry.Extent;
            return entry.Bounded;
        }

        private static Entry Compute(PaintNodeBase node)
        {
            if (s_cache.TryGetValue(node, out var cached))
            {
                return cached;
            }

            var entry = new Entry { Bounded = true, Extent = SKRect.Empty };

            // DrawNode skips these outright, children included.
            bool drawn = !(node is OpacityGroupPaintNode && node.Opacity <= 0f) && IsFinite(node.Bounds);
            if (drawn)
            {
                entry.Extent = OwnExtent(node);

                var children = node.Children;
                if (children != null && children.Count > 0)
                {
                    var childUnion = SKRect.Empty;
                    for (int i = 0; i < children.Count && entry.Bounded; i++)
                    {
                        var child = children[i];
                        if (child == null) continue;
                        var childEntry = Compute(child);
                        if (!childEntry.Bounded)
                        {
                            entry.Bounded = false;
                            break;
                        }

                        var childExtent = childEntry.Extent;
                        if (childExtent.IsEmpty) continue;
                        if (child.Transform.HasValue)
                        {
                            childExtent = child.Transform.Value.MapRect(childExtent);
                        }
                        childUnion = Union(childUnion, childExtent);
                    }

                    // Scroll and sticky offsets move the node's content and children.
                    if (node is ScrollPaintNode scroll && !childUnion.IsEmpty)
                    {
                        childUnion.Offset(-scroll.ScrollX, -scroll.ScrollY);
                    }
                    else if (node is StickyPaintNode sticky && !childUnion.IsEmpty)
                    {
                        childUnion.Offset(sticky.StickyOffset.X, sticky.StickyOffset.Y);
                    }

                    entry.Extent = Union(entry.Extent, childUnion);
                }

                if (entry.Bounded &&
                    node is StackingContextPaintNode filtered &&
                    !string.IsNullOrWhiteSpace(filtered.Filter))
                {
                    if (CssFilterParser.TryGetVisualOutset(filtered.Filter, out var outset))
                    {
                        if (!entry.Extent.IsEmpty) entry.Extent.Inflate(outset, outset);
                    }
                    else
                    {
                        entry.Bounded = false;
                    }
                }

                // DrawNode pushes the clip before the node's own drawing, its children and
                // its filter layer, so nothing it paints leaves the clip.
                if (entry.Bounded && !entry.Extent.IsEmpty && TryGetClip(node, out var clip))
                {
                    entry.Extent = entry.Extent.IntersectsWith(clip)
                        ? SKRect.Intersect(entry.Extent, clip)
                        : SKRect.Empty;
                }
            }

            s_cache.AddOrUpdate(node, entry);
            return entry;
        }

        private static SKRect OwnExtent(PaintNodeBase node)
        {
            var bounds = node.Bounds;

            // Grouping nodes paint nothing themselves (SkiaRenderer.DrawSelf); their Bounds
            // is the element's box, which for a menu bar or a mega-menu host can be far
            // larger than anything visible. A hover highlight or focus ring and a
            // backdrop filter are the exceptions, drawn within the box.
            bool grouping = node is OpacityGroupPaintNode or ClipPaintNode or ScrollPaintNode or StickyPaintNode ||
                            (node is StackingContextPaintNode context && string.IsNullOrWhiteSpace(context.BackdropFilter));
            if (grouping && !node.IsHovered && !node.IsFocused)
            {
                return SKRect.Empty;
            }

            if (node is BoxShadowPaintNode shadow && !shadow.Inset)
            {
                var shadowRect = bounds;
                shadowRect.Offset(shadow.Offset);
                float reach = Math.Max(0f, shadow.Blur) + Math.Max(0f, shadow.Spread);
                shadowRect.Inflate(reach, reach);
                return Union(bounds, shadowRect);
            }

            return bounds;
        }

        private static bool TryGetClip(PaintNodeBase node, out SKRect clip)
        {
            if (node.ClipRect.HasValue)
            {
                clip = node.ClipRect.Value;
                return true;
            }

            if (node is ClipPaintNode clipNode && clipNode.ClipPath != null)
            {
                clip = clipNode.ClipPath.Bounds;
                return true;
            }

            clip = default;
            return false;
        }

        private static SKRect Union(SKRect a, SKRect b)
        {
            if (a.IsEmpty) return b;
            if (b.IsEmpty) return a;
            return SKRect.Union(a, b);
        }

        // Mirrors SkiaRenderer.IsValidBounds: what fails it is not drawn at all.
        private static bool IsFinite(SKRect r) =>
            float.IsFinite(r.Left) && float.IsFinite(r.Top) && float.IsFinite(r.Right) && float.IsFinite(r.Bottom) &&
            r.Width < 100000 && r.Height < 100000;
    }
}
