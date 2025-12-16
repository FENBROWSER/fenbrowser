using SkiaSharp;
using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Core;

namespace FenBrowser.FenEngine.Rendering.Layout
{
    /// <summary>
    /// Block layout algorithm implementation.
    /// Computes layout for block-level elements according to CSS block formatting context.
    /// </summary>
    public static class BlockLayout
    {
        /// <summary>
        /// Helper struct for float tracking.
        /// </summary>
        public struct FloatRect
        {
            public float Left { get; set; }
            public float Right { get; set; }
            public float Top { get; set; }
            public float Bottom { get; set; }
        }

        /// <summary>
        /// Compute block layout for a container and its children.
        /// </summary>
        /// <param name="engine">Layout engine for recursive layout calls</param>
        /// <param name="node">The block container element</param>
        /// <param name="contentBox">The content box area available for children</param>
        /// <param name="availableWidth">Available width for layout</param>
        /// <param name="maxChildWidth">Output: maximum width of children</param>
        /// <param name="shrinkToContent">If true, shrink to fit content</param>
        /// <returns>Total content height after layout</returns>
        public static float Compute(
            ILayoutEngine engine,
            LiteElement node,
            SKRect contentBox,
            float availableWidth,
            out float maxChildWidth,
            bool shrinkToContent = false)
        {
            var ctx = engine.Context;
            
            float childY = contentBox.Top;
            float startY = childY;
            maxChildWidth = 0;
            float trackedMaxChildWidth = 0;

            // Float tracking
            var leftFloats = new List<FloatRect>();
            var rightFloats = new List<FloatRect>();

            // Get text-align from node style
            CssComputed nodeStyle = ctx.GetStyle(node);
            string textAlign = nodeStyle?.TextAlign?.ToString()?.ToLowerInvariant() ?? "left";

            // Helper to get available X range at a given Y position
            Func<float, (float left, float right)> getAvailableRangeAtY = (y) =>
            {
                float left = contentBox.Left;
                float right = contentBox.Right;
                foreach (var f in leftFloats) if (y >= f.Top && y < f.Bottom) left = Math.Max(left, f.Right);
                foreach (var f in rightFloats) if (y >= f.Top && y < f.Bottom) right = Math.Min(right, f.Left);
                return (left, right);
            };

            // Helper to clear floats
            Func<string, float> getClearY = (clearType) =>
            {
                float clearY = childY;
                if (clearType == "left" || clearType == "both") foreach (var f in leftFloats) clearY = Math.Max(clearY, f.Bottom);
                if (clearType == "right" || clearType == "both") foreach (var f in rightFloats) clearY = Math.Max(clearY, f.Bottom);
                return clearY;
            };

            if (node.Children != null)
            {
                List<LiteElement> pendingInlines = new List<LiteElement>();

                Action flushInlines = () =>
                {
                    if (pendingInlines.Count == 0) return;
                    
                    // For now, inline context is handled by the main renderer
                    // This is a simplified version - real implementation would call InlineLayout
                    foreach (var inline in pendingInlines)
                    {
                        engine.ComputeLayout(inline, contentBox.Left, childY, availableWidth, shrinkToContent: true);
                        var box = ctx.GetBox(inline);
                        if (box != null)
                        {
                            childY += box.MarginBox.Height;
                            if (box.MarginBox.Width > trackedMaxChildWidth) 
                                trackedMaxChildWidth = box.MarginBox.Width;
                        }
                    }
                    pendingInlines.Clear();
                };

                foreach (var child in node.Children)
                {
                    // Skip hidden inputs
                    if (child.Tag?.ToUpperInvariant() == "INPUT")
                    {
                        string inputType = child.Attr != null && child.Attr.TryGetValue("type", out var t) ? t : "";
                        if (inputType.ToLowerInvariant() == "hidden") continue;
                    }

                    // Style & Display
                    CssComputed childStyle = ctx.GetStyle(child);

                    // Position handling (Abs/Fixed -> Remove from flow)
                    string pos = childStyle?.Position?.ToLowerInvariant();
                    if (pos == "absolute" || pos == "fixed")
                    {
                        // Deferred - handled by main renderer
                        continue;
                    }

                    // Clear handling
                    string clearVal = null;
                    if (childStyle?.Map?.TryGetValue("clear", out var cv) == true) 
                        clearVal = cv.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(clearVal))
                    {
                        childY = getClearY(clearVal);
                    }

                    // Float handling
                    string floatVal = childStyle?.Float?.ToLowerInvariant();
                    if (floatVal == "left" || floatVal == "right")
                    {
                        flushInlines();
                        
                        var (rangeLeft, rangeRight) = getAvailableRangeAtY(childY);
                        engine.ComputeLayout(child, rangeLeft, childY, rangeRight - rangeLeft, shrinkToContent: true);
                        
                        var floatBox = ctx.GetBox(child);
                        if (floatBox != null)
                        {
                            var fr = new FloatRect
                            {
                                Top = childY,
                                Bottom = childY + floatBox.MarginBox.Height,
                                Left = floatBox.MarginBox.Left,
                                Right = floatBox.MarginBox.Right
                            };
                            
                            if (floatVal == "left")
                            {
                                fr.Left = rangeLeft;
                                fr.Right = rangeLeft + floatBox.MarginBox.Width;
                                leftFloats.Add(fr);
                            }
                            else
                            {
                                fr.Right = rangeRight;
                                fr.Left = rangeRight - floatBox.MarginBox.Width;
                                rightFloats.Add(fr);
                                engine.ShiftTree(child, fr.Left - floatBox.MarginBox.Left, 0);
                            }
                        }
                        continue;
                    }

                    // Display type
                    string d = childStyle?.Display?.ToLowerInvariant() ?? "block";
                    if (child.IsText) d = "inline";

                    bool isInline = d == "inline" || d == "inline-block" || d == "inline-flex" || d == "inline-grid";

                    if (isInline)
                    {
                        pendingInlines.Add(child);
                    }
                    else // BLOCK
                    {
                        flushInlines();
                        
                        // Layout Block
                        var (rangeLeft, rangeRight) = getAvailableRangeAtY(childY);
                        float blockW = rangeRight - rangeLeft;

                        engine.ComputeLayout(child, rangeLeft, childY, blockW, shrinkToContent: shrinkToContent);
                        
                        var bBox = ctx.GetBox(child);
                        if (bBox != null)
                        {
                            childY += bBox.MarginBox.Height;
                            childY = Math.Max(childY, getClearY("both"));
                            if (bBox.MarginBox.Width > trackedMaxChildWidth) 
                                trackedMaxChildWidth = bBox.MarginBox.Width;
                        }
                    }
                }
                flushInlines();
                childY = Math.Max(childY, getClearY("both"));
            }

            maxChildWidth = trackedMaxChildWidth;
            return childY - startY;
        }
    }
}
