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
                        var resolvedCursor = ResolveCursor(element, tagLow, href, isClickable, isEditable);
                        result = new global::FenBrowser.FenEngine.Interaction.HitTestResult(
                            TagName: tagLow ?? "",
                            Href: href,
                            Cursor: resolvedCursor,
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
        
        /// <summary>
        /// Resolve the cursor type for a hit element by checking the CSS 'cursor' property
        /// on the element and its ancestors, then applying CSS UI Module spec defaults for 'auto'.
        /// </summary>
        private static global::FenBrowser.FenEngine.Interaction.CursorType ResolveCursor(
            Element element, string tagLow, string href, bool isClickable, bool isEditable)
        {
            // 1. Walk up ancestors to find an explicit CSS cursor declaration
            var current = element;
            string cssCursor = null;
            while (current != null)
            {
                var style = current.GetComputedStyle();
                if (style != null && !string.IsNullOrEmpty(style.Cursor))
                {
                    var val = style.Cursor.Trim().ToLowerInvariant();
                    if (val != "auto" && val != "inherit")
                    {
                        cssCursor = val;
                        break;
                    }
                }
                current = current.ParentElement;
            }

            // 2. If an explicit CSS cursor is set (not auto), use it
            if (!string.IsNullOrEmpty(cssCursor))
            {
                return MapCssCursorToType(cssCursor);
            }

            // 3. CSS cursor: auto (default) — apply spec heuristics
            //    Per CSS UI Module Level 3 §7.1:
            //    - Links (href) → pointer
            //    - Editable elements (input, textarea, contenteditable) → text
            //    - Buttons, labels, select → pointer (clickable affordance)
            //    - Everything else → default
            if (!string.IsNullOrEmpty(href))
                return global::FenBrowser.FenEngine.Interaction.CursorType.Pointer;

            if (isEditable)
                return global::FenBrowser.FenEngine.Interaction.CursorType.Text;

            // contenteditable check
            if (element.GetAttribute("contenteditable") == "true")
                return global::FenBrowser.FenEngine.Interaction.CursorType.Text;

            // Clickable elements (button, label, select) → pointer
            if (tagLow == "button" || tagLow == "select" || tagLow == "label" ||
                tagLow == "summary" || tagLow == "details")
                return global::FenBrowser.FenEngine.Interaction.CursorType.Pointer;

            // Elements with onclick/role=button/tabindex should show pointer
            if (!string.IsNullOrEmpty(element.GetAttribute("onclick")) ||
                element.GetAttribute("role") == "button" ||
                element.GetAttribute("role") == "link")
                return global::FenBrowser.FenEngine.Interaction.CursorType.Pointer;

            return global::FenBrowser.FenEngine.Interaction.CursorType.Default;
        }

        /// <summary>
        /// Map a CSS cursor keyword to the engine's CursorType enum.
        /// Reference: CSS UI Module Level 3 §7.1.1
        /// </summary>
        private static global::FenBrowser.FenEngine.Interaction.CursorType MapCssCursorToType(string cssCursor)
        {
            return cssCursor switch
            {
                "default" => global::FenBrowser.FenEngine.Interaction.CursorType.Default,
                "pointer" => global::FenBrowser.FenEngine.Interaction.CursorType.Pointer,
                "text" => global::FenBrowser.FenEngine.Interaction.CursorType.Text,
                "wait" => global::FenBrowser.FenEngine.Interaction.CursorType.Wait,
                "progress" => global::FenBrowser.FenEngine.Interaction.CursorType.Wait,
                "not-allowed" => global::FenBrowser.FenEngine.Interaction.CursorType.NotAllowed,
                "no-drop" => global::FenBrowser.FenEngine.Interaction.CursorType.NotAllowed,
                "move" => global::FenBrowser.FenEngine.Interaction.CursorType.Move,
                "all-scroll" => global::FenBrowser.FenEngine.Interaction.CursorType.Move,
                "crosshair" => global::FenBrowser.FenEngine.Interaction.CursorType.Crosshair,
                "grab" => global::FenBrowser.FenEngine.Interaction.CursorType.Grab,
                "grabbing" => global::FenBrowser.FenEngine.Interaction.CursorType.Grabbing,
                "n-resize" or "s-resize" or "ns-resize" => global::FenBrowser.FenEngine.Interaction.CursorType.ResizeNS,
                "e-resize" or "w-resize" or "ew-resize" => global::FenBrowser.FenEngine.Interaction.CursorType.ResizeEW,
                "ne-resize" or "sw-resize" or "nesw-resize" => global::FenBrowser.FenEngine.Interaction.CursorType.ResizeNESW,
                "nw-resize" or "se-resize" or "nwse-resize" => global::FenBrowser.FenEngine.Interaction.CursorType.ResizeNWSE,
                "col-resize" => global::FenBrowser.FenEngine.Interaction.CursorType.ResizeEW,
                "row-resize" => global::FenBrowser.FenEngine.Interaction.CursorType.ResizeNS,
                "help" => global::FenBrowser.FenEngine.Interaction.CursorType.Default, // No help cursor in Silk.NET
                "context-menu" => global::FenBrowser.FenEngine.Interaction.CursorType.Default,
                "cell" => global::FenBrowser.FenEngine.Interaction.CursorType.Crosshair,
                "vertical-text" => global::FenBrowser.FenEngine.Interaction.CursorType.Text,
                "alias" => global::FenBrowser.FenEngine.Interaction.CursorType.Default,
                "copy" => global::FenBrowser.FenEngine.Interaction.CursorType.Default,
                "none" => global::FenBrowser.FenEngine.Interaction.CursorType.Default,
                "zoom-in" or "zoom-out" => global::FenBrowser.FenEngine.Interaction.CursorType.Default,
                _ => global::FenBrowser.FenEngine.Interaction.CursorType.Default,
            };
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


