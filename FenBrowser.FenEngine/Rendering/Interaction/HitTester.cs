using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Rendering; // Required for PaintNodeBase

namespace FenBrowser.FenEngine.Rendering.Interaction
{
    /// <summary>
    /// Hit testing for click detection.
    /// Determines which element is at a given (x, y) coordinate.
    /// </summary>
    public static class HitTester
    {
        /// <summary>
        /// Find the element at the given coordinates.
        /// Uses PaintTree if available for Stacking Context awareness, otherwise falls back to Box scan.
        /// </summary>
        public static Element HitTest(RenderContext ctx, float x, float y)
        {
            if (ctx == null) return null;

            // Priority: Use PaintTree (Stacking Context Aware)
            if (ctx.PaintTreeRoots != null && ctx.PaintTreeRoots.Count > 0)
            {
               if (HitTestRecursive(ctx.PaintTreeRoots, x, y, out var result))
               {
                   return result.NativeElement as Element;
               }
               return null;
            }
            
            // Fallback: Naive Box Scan
            return HitTestNaive(ctx, x, y);
        }

        private static Element HitTestNaive(RenderContext ctx, float x, float y)
        {
            if (ctx?.Boxes == null) return null;
            
            Element bestMatch = null;
            float minArea = float.MaxValue;

            var boxSnapshot = ctx.Boxes.ToArray();

            foreach (var kvp in boxSnapshot)
            {
                if (!(kvp.Key is Element element)) continue;
                var box = kvp.Value;

                if (box.MarginBox.Width <= 0 || box.MarginBox.Height <= 0) continue;

                var style = ctx.GetStyle(element);
                if (style != null && style.PointerEvents == "none") continue;

                if (box.BorderBox.Contains(x, y))
                {
                    float area = box.BorderBox.Width * box.BorderBox.Height;
                    if (area <= minArea)
                    {
                        minArea = area;
                        bestMatch = element;
                    }
                }
            }
            return bestMatch;
        }

        /// <summary>
        /// Recursive Stacking Context Aware Hit Test.
        /// Traverses Paint Tree in reverse paint order (Front-to-Back).
        /// </summary>
        public static bool HitTestRecursive(IReadOnlyList<PaintNodeBase> nodes, float x, float y, out global::FenBrowser.FenEngine.Interaction.HitTestResult result)
        {
            result = global::FenBrowser.FenEngine.Interaction.HitTestResult.None;
            if (nodes == null) return false;

            // Traverse in REVERSE paint order (Front-to-Back)
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                var node = nodes[i];
                if (node == null) continue;

                // 1. Transform to Local Space
                float localX = x;
                float localY = y;
                
                if (node.Transform.HasValue)
                {
                    if (!node.Transform.Value.TryInvert(out var inv)) continue;
                    var pt = inv.MapPoint(x, y);
                    localX = pt.X;
                    localY = pt.Y;
                }

                // 2. Check Clipping (Local Space)
                if (node.ClipRect.HasValue)
                {
                    if (!node.ClipRect.Value.Contains(localX, localY)) continue;
                }
                if (node is ClipPaintNode clipNode)
                {
                    bool inside = false;
                    if (clipNode.ClipPath != null) inside = clipNode.ClipPath.Contains(localX, localY);
                    else if (clipNode.ClipRect.HasValue) inside = clipNode.ClipRect.Value.Contains(localX, localY);
                    else inside = clipNode.Bounds.Contains(localX, localY);

                    if (!inside) continue;
                }

                // 3. Apply Scroll/Sticky (Content Space)
                float contentX = localX;
                float contentY = localY;

                if (node is ScrollPaintNode scrollNode)
                {
                    contentX += scrollNode.ScrollX;
                    contentY += scrollNode.ScrollY;
                }
                if (node is StickyPaintNode stickyNode)
                {
                    contentX -= stickyNode.StickyOffset.X;
                    contentY -= stickyNode.StickyOffset.Y;
                }

                // 4. Check Children (Front-most first)
                if (node.Children != null && node.Children.Count > 0)
                {
                    if (HitTestRecursive(node.Children, contentX, contentY, out result)) return true;
                }

                // 5. Check Self
                // Don't hit wrappers usually, but check source node
                bool isWrapper = node is StackingContextPaintNode || node is OpacityGroupPaintNode || node is StickyPaintNode || node is ScrollPaintNode;
                // Even wrappers might have background/border if they are associated with an element. 
                // SkiaRenderer draws Self at node.Bounds *in current space*.
                
                if (node.Bounds.Contains(contentX, contentY))
                {
                    var domNode = node.SourceNode;
                    var element = domNode as Element;
                    if (element == null && domNode?.ParentElement is Element parentEl) element = parentEl;

                    if (element != null)
                    {
                        if (!IsElementHitTestVisible(element))
                        {
                            continue;
                        }

                        // Found a hit! Resolve interactive ancestor.
                        var interactive = FindInteractiveAncestor(element);
                        string tagName = element.TagName;
                        string elementId = element.GetAttribute("id");
                        string href = null;

                        if (interactive != null)
                        {
                            var interactiveTag = interactive.TagName ?? string.Empty;
                            if (string.Equals(interactiveTag, "a", StringComparison.OrdinalIgnoreCase))
                            {
                                href = interactive.GetAttribute("href");
                                tagName = "a";
                                element = interactive;
                            }
                            else if (string.Equals(interactiveTag, "button", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(interactiveTag, "input", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(interactiveTag, "textarea", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(interactiveTag, "select", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(interactiveTag, "label", StringComparison.OrdinalIgnoreCase))
                            {
                                tagName = interactiveTag;
                                element = interactive;
                            }
                            else if (interactive.GetAttribute("contenteditable") == "true" ||
                                     !string.IsNullOrEmpty(interactive.GetAttribute("tabindex")))
                            {
                                tagName = interactiveTag;
                                element = interactive;
                            }
                        }

                        string tagLow = tagName?.ToLowerInvariant();
                        bool isClickable = !string.IsNullOrEmpty(href) || tagLow == "button" || tagLow == "input" || tagLow == "label";
                        bool isFocusable = isClickable || tagLow == "textarea" || tagLow == "select";
                        bool isEditable = tagLow == "input" || tagLow == "textarea";

                        string imageSrc = tagLow == "img" ? element.GetAttribute("src") : null;
                        result = new global::FenBrowser.FenEngine.Interaction.HitTestResult(
                            TagName: tagLow ?? "",
                            Href: href,
                            Cursor: !string.IsNullOrEmpty(href) ? global::FenBrowser.FenEngine.Interaction.CursorType.Pointer : (isEditable ? global::FenBrowser.FenEngine.Interaction.CursorType.Text : global::FenBrowser.FenEngine.Interaction.CursorType.Default),
                            IsClickable: isClickable,
                            IsFocusable: isFocusable,
                            IsEditable: isEditable,
                            ElementId: elementId,
                            NativeElement: element,
                            BoundingBox: node.Bounds,
                            ImageSrc: imageSrc
                        );
                        return true;
                    }
                }
            }
            return false;
        }
        
        private static Element FindInteractiveAncestor(Element element)
        {
            var current = element;
            while (current != null)
            {
                if (!IsElementHitTestVisible(current))
                {
                    current = current.ParentElement;
                    continue;
                }

                if (string.Equals(current.TagName, "a", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(current.TagName, "button", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(current.TagName, "input", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(current.TagName, "textarea", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(current.TagName, "select", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(current.TagName, "label", StringComparison.OrdinalIgnoreCase) ||
                    current.GetAttribute("contenteditable") == "true" ||
                    !string.IsNullOrEmpty(current.GetAttribute("tabindex")))
                    return current;
                current = current.ParentElement;
            }
            return null;
        }

        public static List<Element> HitTestAll(RenderContext ctx, float x, float y)
        {
             // Naive impl for now; used mainly by tooling.
             if (ctx?.Boxes == null) return new List<Element>();
             return HitTestAllNaive(ctx, x, y);
        }

        private static List<Element> HitTestAllNaive(RenderContext ctx, float x, float y)
        {
            if (ctx?.Boxes == null) return new List<Element>();
            var hits = new List<(Element, float)>();
            foreach (var kvp in ctx.Boxes)
            {
                if (kvp.Key is Element el && kvp.Value.BorderBox.Contains(x,y))
                    hits.Add((el, kvp.Value.BorderBox.Width * kvp.Value.BorderBox.Height));
            }
            return hits.OrderBy(h=>h.Item2).Select(h=>h.Item1).ToList();
        }

        public static Element HitTestClickable(RenderContext ctx, float x, float y)
        {
            var el = HitTest(ctx, x, y);
            // Walk up to find clickable
             while (el != null)
            {
                string tag = el.TagName?.ToUpperInvariant();
                if (tag == "A" || tag == "BUTTON" || tag == "INPUT" || tag == "SELECT") return el;
                el = el.ParentElement;
            }
            return null;
        }

        private static bool IsElementHitTestVisible(Element element)
        {
            if (element == null) return false;

            if (element.HasAttribute("hidden"))
            {
                return false;
            }

            var style = element.GetComputedStyle();
            if (style == null) return true;

            if (string.Equals(style.PointerEvents, "none", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(style.Visibility, "hidden", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(style.Visibility, "collapse", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(style.Display, "none", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Invisible overlays (opacity: 0) should not intercept hover/click.
            // Without this guard, hidden UI layers can block text inputs/buttons.
            if (style.Opacity.HasValue && style.Opacity.Value <= 0.001d)
            {
                return false;
            }

            return true;
        }
    }
}


