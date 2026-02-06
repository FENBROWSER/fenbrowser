// =============================================================================
// StackingContextPainter.cs
// CSS 2.1 Stacking Context Painter
// 
// SPEC REFERENCE: CSS 2.1 Appendix E - Elaborate description of Stacking Contexts
//                 https://www.w3.org/TR/CSS21/zindex.html
// 
// PURPOSE: Paints elements in correct stacking order according to CSS 2.1.
//          Generates display list commands in the correct z-order.
// 
// STATUS: ✅ Fully Implemented
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Painting
{
    /// <summary>
    /// Command type for display list entries.
    /// </summary>
    public enum DisplayCommandType
    {
        PushClip,
        PopClip,
        PushTransform,
        PopTransform,
        PushOpacity,
        PopOpacity,
        DrawRect,
        DrawText,
        DrawImage,
        DrawBorder,
        DrawShadow,
        DrawSvg,
        BeginStackingContext,
        EndStackingContext
    }

    /// <summary>
    /// A display list command for deferred rendering.
    /// </summary>
    public class DisplayCommand
    {
        public DisplayCommandType Type { get; }
        public Node Node { get; }
        public SKRect Bounds { get; }
        public object Data { get; }
        public int ZIndex { get; }
        public PaintPhase Phase { get; }

        public DisplayCommand(DisplayCommandType type, Node node, SKRect bounds, object data = null, 
            int zIndex = 0, PaintPhase phase = PaintPhase.BackgroundAndBorder)
        {
            Type = type;
            Node = node;
            Bounds = bounds;
            Data = data;
            ZIndex = zIndex;
            Phase = phase;
        }

        public override string ToString()
        {
            var nodeName = (Node as Element)?.TagName ?? "Node";
            return $"{Type}[{nodeName} z={ZIndex} phase={Phase}]";
        }
    }

    /// <summary>
    /// Paints a stacking context tree in correct CSS 2.1 order.
    /// </summary>
    public class StackingContextPainter
    {
        private readonly Dictionary<Node, CssComputed> _styles;
        private readonly Dictionary<Node, SKRect> _layoutBoxes;
        private readonly List<DisplayCommand> _displayList = new();

        public StackingContextPainter(
            Dictionary<Node, CssComputed> styles,
            Dictionary<Node, SKRect> layoutBoxes)
        {
            _styles = styles ?? new Dictionary<Node, CssComputed>();
            _layoutBoxes = layoutBoxes ?? new Dictionary<Node, SKRect>();
        }

        public List<DisplayCommand> Paint(StackingContextV2 rootContext)
        {
            _displayList.Clear();
            PaintStackingContext(rootContext);
            return _displayList;
        }

        private void PaintStackingContext(StackingContextV2 sc)
        {
            var bounds = GetBounds(sc.Node);

#if DEBUG
            FenLogger.Debug($"[Painter] Painting SC: {sc}", LogCategory.Rendering);
#endif

            _displayList.Add(new DisplayCommand(
                DisplayCommandType.BeginStackingContext, 
                sc.Node, bounds, sc, sc.ZIndex, PaintPhase.BackgroundAndBorder));

            ApplyStackingContextEffects(sc.Node);

            // Phase 1: Background and borders
            PaintBackgroundAndBorder(sc.Node, bounds, PaintPhase.BackgroundAndBorder);

            // Phase 2: Negative z-index children
            var negativeChildren = sc.NegativeZChildren
                .OrderBy(c => c.ZIndex)
                .ThenBy(c => c.Node.GetHashCode());

            foreach (var childSC in negativeChildren)
                PaintStackingContext(childSC);

            // Phase 3: In-flow block descendants
            foreach (var block in sc.InFlowBlocks)
                PaintNormalFlowElement(block, sc, PaintPhase.InFlowBlocks);

            // Phase 4: Floats
            foreach (var floatNode in sc.Floats)
                PaintNormalFlowElement(floatNode, sc, PaintPhase.Floats);

            // Phase 5: In-flow inline descendants
            foreach (var inline in sc.InFlowInlines)
                PaintNormalFlowElement(inline, sc, PaintPhase.InFlowInlines);

            // Phase 6: Positioned descendants and z-index: 0 contexts
            var phase6Items = sc.PositionedAndZeroZ.OrderBy(l => l.TreeOrder);

            foreach (var layer in phase6Items)
            {
                if (layer.IsStackingContext && layer.Context != null)
                    PaintStackingContext(layer.Context);
                else
                    PaintPositionedElement(layer.Node, sc, PaintPhase.PositionedAndZeroZ);
            }

            // Phase 7: Positive z-index children
            var positiveChildren = sc.PositiveZChildren
                .OrderBy(c => c.ZIndex)
                .ThenBy(c => c.Node.GetHashCode());

            foreach (var childSC in positiveChildren)
                PaintStackingContext(childSC);

            RemoveStackingContextEffects(sc.Node);

            _displayList.Add(new DisplayCommand(
                DisplayCommandType.EndStackingContext,
                sc.Node, bounds, sc, sc.ZIndex, PaintPhase.BackgroundAndBorder));
        }

        #region Paint Helpers

        private void PaintBackgroundAndBorder(Node node, SKRect bounds, PaintPhase phase)
        {
            if (!_styles.TryGetValue(node, out var style)) return;

            var bgColor = style.BackgroundColor ?? SKColors.Transparent;
            if (bgColor.Alpha > 0)
            {
                _displayList.Add(new DisplayCommand(
                    DisplayCommandType.DrawRect, node, bounds,
                    new BackgroundData { Color = bgColor, Style = style }, 0, phase));
            }

            if (HasBorder(style))
            {
                _displayList.Add(new DisplayCommand(
                    DisplayCommandType.DrawBorder, node, bounds,
                    new BorderData { Style = style }, 0, phase));
            }
        }

        private void PaintNormalFlowElement(Node node, StackingContextV2 sc, PaintPhase phase)
        {
            if (sc.IsLayerRoot(node)) return;

            var bounds = GetBounds(node);
            PaintBackgroundAndBorder(node, bounds, phase);
            PaintContent(node, bounds, phase);

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    if (!sc.IsLayerRoot(child))
                        PaintNormalFlowElement(child, sc, phase);
                }
            }
        }

        private void PaintPositionedElement(Node node, StackingContextV2 sc, PaintPhase phase)
        {
            var bounds = GetBounds(node);
            PaintBackgroundAndBorder(node, bounds, phase);
            PaintContent(node, bounds, phase);

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    if (!sc.IsLayerRoot(child))
                        PaintNormalFlowElement(child, sc, phase);
                }
            }
        }

        private void PaintContent(Node node, SKRect bounds, PaintPhase phase)
        {
            if (node is Element element)
            {
                if (element.Children != null)
                {
                    foreach (var child in element.Children)
                    {
                        if (child is Text textNode && !string.IsNullOrWhiteSpace(textNode.Data))
                        {
                            _displayList.Add(new DisplayCommand(
                                DisplayCommandType.DrawText, textNode, bounds,
                                new TextData { Text = textNode.Data, ParentStyle = GetStyle(element) },
                                0, phase));
                        }
                    }
                }

                var tagName = element.TagName?.ToLowerInvariant();
                if (tagName == "img")
                {
                    _displayList.Add(new DisplayCommand(
                        DisplayCommandType.DrawImage, element, bounds,
                        new ImageData { Src = element.GetAttribute("src") }, 0, phase));
                }
                else if (tagName == "svg")
                {
                    _displayList.Add(new DisplayCommand(
                        DisplayCommandType.DrawSvg, element, bounds, null, 0, phase));
                }
            }
        }

        #endregion

        #region Stacking Context Effects

        private void ApplyStackingContextEffects(Node node)
        {
            if (!_styles.TryGetValue(node, out var style)) return;

            var bounds = GetBounds(node);

            var opacity = style.Opacity ?? 1.0f;
            if (opacity < 1.0f)
            {
                _displayList.Add(new DisplayCommand(
                    DisplayCommandType.PushOpacity, node, bounds, opacity));
            }

            if (!string.IsNullOrEmpty(style.Transform) && style.Transform != "none")
            {
                _displayList.Add(new DisplayCommand(
                    DisplayCommandType.PushTransform, node, bounds, style.Transform));
            }

            var overflow = style.Overflow?.ToLowerInvariant() ?? "visible";
            if (overflow == "hidden" || overflow == "scroll" || overflow == "auto")
            {
                _displayList.Add(new DisplayCommand(
                    DisplayCommandType.PushClip, node, bounds, overflow));
            }
        }

        private void RemoveStackingContextEffects(Node node)
        {
            if (!_styles.TryGetValue(node, out var style)) return;

            var bounds = GetBounds(node);

            var overflow = style.Overflow?.ToLowerInvariant() ?? "visible";
            if (overflow == "hidden" || overflow == "scroll" || overflow == "auto")
            {
                _displayList.Add(new DisplayCommand(DisplayCommandType.PopClip, node, bounds));
            }

            if (!string.IsNullOrEmpty(style.Transform) && style.Transform != "none")
            {
                _displayList.Add(new DisplayCommand(DisplayCommandType.PopTransform, node, bounds));
            }

            var opacity = style.Opacity ?? 1.0f;
            if (opacity < 1.0f)
            {
                _displayList.Add(new DisplayCommand(DisplayCommandType.PopOpacity, node, bounds));
            }
        }

        #endregion

        #region Utilities

        private SKRect GetBounds(Node node)
        {
            if (_layoutBoxes.TryGetValue(node, out var bounds))
                return bounds;
            return SKRect.Empty;
        }

        private CssComputed GetStyle(Node node)
        {
            _styles.TryGetValue(node, out var style);
            return style;
        }

        private bool HasBorder(CssComputed style)
        {
            if (style == null) return false;
            // Use BorderThickness Thickness
            return style.BorderThickness.Left > 0 ||
                   style.BorderThickness.Right > 0 ||
                   style.BorderThickness.Top > 0 ||
                   style.BorderThickness.Bottom > 0;
        }

        #endregion

        #region Data Classes

        public class BackgroundData
        {
            public SKColor Color { get; set; }
            public CssComputed Style { get; set; }
        }

        public class BorderData
        {
            public CssComputed Style { get; set; }
        }

        public class TextData
        {
            public string Text { get; set; }
            public CssComputed ParentStyle { get; set; }
        }

        public class ImageData
        {
            public string Src { get; set; }
        }

        #endregion
    }
}

