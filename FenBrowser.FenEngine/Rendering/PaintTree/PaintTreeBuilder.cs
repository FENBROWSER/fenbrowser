using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Constructs a PaintTree from the DOM, Style, and Layout data.
    /// Handles stacking contexts, z-index sorting, and visibility culling.
    /// </summary>
    public sealed class PaintTreeBuilder
    {
        private readonly IReadOnlyDictionary<Node, BoxModel> _boxes;
        private readonly IReadOnlyDictionary<Node, CssComputed> _styles;
        private readonly float _viewportWidth;
        private readonly float _viewportHeight;

        private PaintTreeBuilder(
            IReadOnlyDictionary<Node, BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            float viewportWidth,
            float viewportHeight)
        {
            _boxes = boxes;
            _styles = styles;
            _viewportWidth = viewportWidth;
            _viewportHeight = viewportHeight;
        }

        /// <summary>
        /// Builds a paint tree for the given root element and layout/style state.
        /// </summary>
        public static PaintTree Build(
            Element root,
            IReadOnlyDictionary<Node, BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            float viewportWidth,
            float viewportHeight)
        {
            if (root == null) return PaintTree.Empty;

            var builder = new PaintTreeBuilder(boxes, styles, viewportWidth, viewportHeight);
            
            // The root effectively creates the initial stacking context
            var rootContext = new StackingContext(root);
            builder.BuildRecursive(root, rootContext, null, 0);
            
            // Sort and finalize the root context into a PaintNode tree
            var rootPaintNode = rootContext.Flatten();
            
            if (rootPaintNode == null) return PaintTree.Empty;

            // Final recursive bounds pass to ensure all nodes have correct VisualBounds
            CalculateFinalVisualBounds(rootPaintNode, 0);
            
            return new PaintTree(rootPaintNode);
        }

        private static void CalculateFinalVisualBounds(PaintNode node, int depth)
        {
            if (node == null) return;
            if (depth > 256) return; // Guard against ultra-deep trees

            // Start with own bounds
            SKRect bounds = node.IsText ? node.Box.ContentBox : node.Box.MarginBox;
            
            // If it has box shadows, expand bounds
            if (node.BoxShadows != null)
            {
                foreach (var shadow in node.BoxShadows)
                {
                    float inflate = shadow.BlurRadius + shadow.SpreadRadius;
                    SKRect shadowRect = bounds;
                    shadowRect.Offset(shadow.OffsetX, shadow.OffsetY);
                    shadowRect.Inflate(inflate, inflate);
                    bounds = SKRect.Union(bounds, shadowRect);
                }
            }

            foreach (var child in node.Children)
            {
                CalculateFinalVisualBounds(child, depth + 1);
                bounds = SKRect.Union(bounds, child.VisualBounds);
            }

            node.VisualBounds = bounds;
        }

        private void BuildRecursive(Node node, StackingContext currentContext, PaintNode parentNode, int depth)
        {
            if (node == null) return;
            // Only process Elements and TextNs
            if (!(node is Element) && !(node is Text)) return;

            if (depth > 256) return; // Guard against stack overflow

            // Get Box and Style
            if (!_boxes.TryGetValue(node, out var box) || box == null)
            {
                // Even if no box (e.g. display:none), specific logic might needed? 
                // No, layout engine handles display:none by not creating box.
                return;
            }

            // Persistence of style
            _styles.TryGetValue(node, out var style);
            if (LayoutHelper.ShouldHide(node, style)) return;

            // Create PaintNode
            var paintNode = new PaintNode
            {
                DomNode = node,
                Box = box,
                Style = style,
                IsText = node is Text,
                TextContent = (node as Text)?.Data,
                ZIndex = style?.ZIndex ?? 0,
                Opacity = (float)(style?.Opacity ?? 1.0),
                IsVisible = true, // We already checked ShouldHide
                BackgroundColor = style?.BackgroundColor,
                BorderColor = style?.BorderBrushColor,
                BorderRadius = ParseBorderRadius(style),
                Transform = ParseTransform(style?.Transform),
                BoxShadows = !string.IsNullOrEmpty(style?.BoxShadow) ? BoxShadowParsed.Parse(style.BoxShadow) : null,
                CreatesStackingContext = DetermineCreatesStackingContext(style),
                ClipRect = (style?.Overflow?.ToLowerInvariant() == "hidden") ? box.PaddingBox : (SKRect?)null
            };

            // Link to parent (Build Tree) or Stacking Context (Layer)
            // Logic:
            // 1. New Stacking Context -> Layer (processed separately)
            // 2. Positioned Element (Abs/Fixed/Rel+Z) -> Layer (escapes normal flow clip, handled by SC)
            // 3. Normal Flow (Block/Inline/Float/Rel-Auto) -> Tree (preserves hierarchy and clip)

            StackingContext newContext = null;
            bool isLayer = false;

            if (paintNode.CreatesStackingContext)
            {
                newContext = new StackingContext(node) { RootNode = paintNode };
                currentContext.AddChildContext(newContext);
                isLayer = true;
            }
            else
            {
                // Check if positioned (but not SC). i.e. Positioned with Z-Index Auto/0?
                // DetermineCreatesStackingContext strict rules: Rel+Z -> SC. Abs+Z -> SC.
                // Abs+Auto -> Not SC.
                // Fixed -> Always SC.
                
                string pos = style?.Position?.ToLowerInvariant();
                bool isPositioned = pos == "absolute" || pos == "fixed" || (pos == "relative" && style?.ZIndex.HasValue == true);
                // Actually DetermineCreatesStackingContext handles most cases.
                // Abs w/o Z-index does NOT create SC. But it IS a Layer (Step 6).
                // So if !CreatesSC and IsPositioned (Abs/Rel), it moves to PositionedLayers.
                
                // Simplified Check for Layer vs Flow:
                // Only Abs/Fixed are "out of flow" layers if they don't create SC.
                // Rel is "in flow" for layout but can be z-ordered? 
                // Rel without Z-index stays in flow (Step 3/5 with offset).
                
                bool isOutFlow = pos == "absolute" || pos == "fixed";
                
                if (isOutFlow)
                {
                    currentContext.AddNode(paintNode); // Add to PositionedLayers
                    isLayer = true;
                }
                else
                {
                    // Normal Flow (including Rel-Auto, Floats)
                    // Add to parent's children to maintain hierarchy/clip
                    if (parentNode != null)
                    {
                        parentNode.Children.Add(paintNode);
                    }
                }
            }

            // Recurse Children
            // If new SC, it becomes the context. Root is paintNode.
            // If Layer, it escapes parent, but its children belong to it.
            // If Flow, it stays attached.
            
            var contextForChildren = newContext ?? currentContext;
            var parentForChildren = paintNode; // Always link children to this node

            if (node is Element elem && elem.Children != null)
            {
                foreach (var child in elem.Children)
                {
                    BuildRecursive(child, contextForChildren, parentForChildren, depth + 1);
                }
            }
        }

        private static bool DetermineCreatesStackingContext(CssComputed style)
        {
            if (style == null) return false;
            string pos = style.Position?.ToLowerInvariant();
            
            if (pos == "fixed" || pos == "sticky") return true;
            if ((pos == "absolute" || pos == "relative") && style.ZIndex.HasValue && style.ZIndex.Value != 0) return true; // Standard says z-index != auto
            if (style.Opacity.HasValue && style.Opacity.Value < 1.0) return true;
            if (!string.IsNullOrEmpty(style.Transform) && style.Transform != "none") return true;
            
            return false;
        }

        private static float[] ParseBorderRadius(CssComputed style)
        {
             if (style == null) return null;
             var br = style.BorderRadius;
             if (br.TopLeft.Value == 0 && br.TopRight.Value == 0 && br.BottomRight.Value == 0 && br.BottomLeft.Value == 0) return null;
             
             return new float[] 
             { 
                 (float)br.TopLeft.Value, 
                 (float)br.TopRight.Value, 
                 (float)br.BottomRight.Value, 
                 (float)br.BottomLeft.Value 
             };
        }

        private static SKMatrix? ParseTransform(string transform)
        {
            if (string.IsNullOrEmpty(transform) || transform == "none") return null;
            try
            {
                var t3d = FenEngine.Rendering.CssTransform3D.Parse(transform);
                return t3d.ToSKMatrix();
            }
            catch { return null; }
        }
        
        /// <summary>
        /// Represents a Stacking Context for gathering and sorting PaintNodes.
        /// </summary>
        private class StackingContext
        {
            public Node SourceNode { get; }
            public PaintNode RootNode { get; set; }
            
            // Categories based on Paint Order (simplification of CSS spec)
            public List<StackingContext> NegativeZIndexChildren { get; } = new List<StackingContext>();
            public List<PaintNode> BlockChildren { get; } = new List<PaintNode>();
            public List<PaintNode> FloatChildren { get; } = new List<PaintNode>();
            public List<PaintNode> InlineChildren { get; } = new List<PaintNode>();
            public List<PaintNode> AutoZIndexChildren { get; } = new List<PaintNode>(); // Positioned z=0/auto
            public List<StackingContext> PositiveZIndexChildren { get; } = new List<StackingContext>();

            public StackingContext(Node source)
            {
                SourceNode = source;
            }

            public void AddChildContext(StackingContext childContext)
            {
                int z = childContext.RootNode?.ZIndex ?? 0;
                if (z < 0) NegativeZIndexChildren.Add(childContext);
                else PositiveZIndexChildren.Add(childContext); // >= 0 goes here for contexts
                // Note: Spec says z=0 stacked contexts are treated atomically with z=0 positioned elements
                // For simplicity, we lump positive and 0 contexts together or separate them.
            }

            public void AddNode(PaintNode node)
            {
                // Classify node for painting order
                // 1. Background/Border is painted by the parent (container of this context or parent node loop)
                // Nodes here are CHILDREN of the context root (or descendants not in other contexts)
                
                var style = node.Style;
                string pos = style?.Position?.ToLowerInvariant();
                bool isPositioned = pos == "absolute" || pos == "relative" || pos == "fixed" || pos == "sticky";
                bool isFloat = style?.Float != "none" && !string.IsNullOrEmpty(style?.Float);
                
                if (isPositioned)
                {
                    // Positioned elements with z-index:auto (checked as 0 here due to style parsing)
                    int z = style?.ZIndex ?? 0;
                    if (z < 0) 
                    {
                        // Needs to create a pseudo-context or just handle as paint node
                        // If it's a PaintNode but NOT a Stacking Context, it sits in the "Negative Z-Index" phase of THIS context?
                        // Spec: z-index applies to positioned elements. 
                        // If z<0, it paints BEFORE block children of this context.
                        // We will add to a special list or rely on StackingContext if we want strict z-ordering.
                        // For simplicity: Add to a generic list we sort later?
                        // Let's store in distinct bucket.
                        // Actually, if it DOESN'T create a StackingContext, it's just a positioned element in this context.
                        // Its painting order depends on z-index.
                        // But PaintNode property z-index is only relevant for positioned elements.
                         // .. implementing full spec is complex.
                         // Simplified: 
                         // Positioned nodes go to AutoZIndexChildren if z=0, else if z>0...
                         // Actually, positioned z<0 is painted before blocks.
                    }
                    else
                    {
                        AutoZIndexChildren.Add(node);
                    }
                }
                else if (isFloat)
                {
                    FloatChildren.Add(node);
                }
                else if (IsInline(style, node.DomNode)) // Inline flow
                {
                    InlineChildren.Add(node);
                }
                else
                {
                    BlockChildren.Add(node);
                }
            }

            private bool IsInline(CssComputed style, Node node)
            {
                string display = style?.Display?.ToLowerInvariant();
                if (display == "inline" || display == "inline-block" || display == "inline-flex" || node is Text) return true;
                return false;
            }

            public PaintNode Flatten()
            {
                // Sort sub-contexts and layers
                NegativeZIndexChildren.Sort((a, b) => (a.RootNode?.ZIndex ?? 0).CompareTo(b.RootNode?.ZIndex ?? 0));
                PositiveZIndexChildren.Sort((a, b) => (a.RootNode?.ZIndex ?? 0).CompareTo(b.RootNode?.ZIndex ?? 0));

                var resultChildren = new List<PaintNode>();

                // 2. Negative Z-Index Contexts
                foreach (var ctx in NegativeZIndexChildren)
                    resultChildren.Add(ctx.Flatten());

                // 3-5. Normal Flow (Already present in RootNode.Children as a Tree)
                // We preserve the existing Normal Flow hierarchy (Block/Float/Inline/Rel).
                if (RootNode != null && RootNode.Children != null)
                {
                    resultChildren.AddRange(RootNode.Children);
                }

                // 6. Positioned (z=0 / auto) Layers
                resultChildren.AddRange(AutoZIndexChildren);

                // 7. Positive Z-Index Contexts
                foreach (var ctx in PositiveZIndexChildren)
                    resultChildren.Add(ctx.Flatten());

                if (RootNode != null)
                {
                    // Update RootNode with the complete ordered list
                    // Note: This flattens the top-level layers into the Root's children, 
                    // wrapping the Normal Flow tree.
                    RootNode.Children = resultChildren;
                    return RootNode;
                }
                
                return null;
            }
        }
    }
}
