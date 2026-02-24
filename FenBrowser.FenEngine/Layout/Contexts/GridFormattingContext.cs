using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    /// <summary>
    /// Grid formatting context backed by the production GridLayoutComputer.
    /// This avoids divergent placeholder behavior between box-tree layout and the
    /// main grid algorithm path.
    /// </summary>
    public class GridFormattingContext : FormattingContext
    {
        private static GridFormattingContext _instance;
        public static GridFormattingContext Instance => _instance ??= new GridFormattingContext();

        protected override void LayoutCore(LayoutBox box, LayoutState state)
        {
            if (box is not BlockBox container || container.SourceNode is not Element containerElement)
            {
                return;
            }

            var containerStyle = container.ComputedStyle ?? new CssComputed();
            ResolveContainerWidth(container, containerStyle, state);

            var nodeToBox = new Dictionary<Node, LayoutBox>();
            var styles = new Dictionary<Node, CssComputed>();
            CollectNodeMappings(container, nodeToBox, styles);
            styles[containerElement] = containerStyle;

            // Grid formatting should follow the laid-out box tree children that survived
            // visibility/display filtering, not raw DOM children.
            var childrenSource = container.Children
                .Select(child => child.SourceNode)
                .Where(node => node != null)
                .ToList();

            LayoutMetrics MeasureNode(Node node, SKSize availableSize, int depth)
            {
                if (!nodeToBox.TryGetValue(node, out var childBox))
                {
                    return new LayoutMetrics();
                }

                float childWidth = availableSize.Width;
                if (float.IsNaN(childWidth))
                {
                    childWidth = 0f;
                }

                float childHeight = availableSize.Height;
                if (float.IsNaN(childHeight) || float.IsInfinity(childHeight))
                {
                    childHeight = state.ContainingBlockHeight > 0f ? state.ContainingBlockHeight : state.ViewportHeight;
                }

                float containingWidth = (!float.IsInfinity(childWidth) && childWidth > 0f)
                    ? childWidth
                    : Math.Max(0f, container.Geometry.ContentBox.Width);

                var childState = new LayoutState(
                    new SKSize(childWidth, childHeight),
                    containingWidth,
                    childHeight,
                    state.ViewportWidth,
                    state.ViewportHeight,
                    state.Deadline);

                FormattingContext.Resolve(childBox).Layout(childBox, childState);

                return new LayoutMetrics
                {
                    MaxChildWidth = Math.Max(0f, childBox.Geometry.MarginBox.Width),
                    ContentHeight = Math.Max(0f, childBox.Geometry.MarginBox.Height),
                    ActualHeight = Math.Max(0f, childBox.Geometry.MarginBox.Height)
                };
            }

            void ArrangeNode(Node node, SKRect rect, int depth)
            {
                if (!nodeToBox.TryGetValue(node, out var childBox))
                {
                    return;
                }

                float width = Math.Max(0f, rect.Width);
                float height = Math.Max(0f, rect.Height);

                LayoutBoxOps.ComputeBoxModelFromContent(childBox, width, height);

                float absoluteLeft = container.Geometry.ContentBox.Left + rect.Left;
                float absoluteTop = container.Geometry.ContentBox.Top + rect.Top;
                LayoutBoxOps.SetPosition(childBox, absoluteLeft, absoluteTop);

                var childState = new LayoutState(
                    new SKSize(width, height),
                    width,
                    height > 0f ? height : state.ContainingBlockHeight,
                    state.ViewportWidth,
                    state.ViewportHeight,
                    state.Deadline);

                FormattingContext.Resolve(childBox).Layout(childBox, childState);

                // Child layout may have recomputed local geometry; keep final grid placement.
                LayoutBoxOps.SetPosition(childBox, absoluteLeft, absoluteTop);
            }

            float measureHeightConstraint = state.AvailableSize.Height;
            if (float.IsNaN(measureHeightConstraint))
            {
                measureHeightConstraint = state.ViewportHeight;
            }

            var metrics = GridLayoutComputer.Measure(
                containerElement,
                new SKSize(container.Geometry.ContentBox.Width, measureHeightConstraint),
                styles,
                0,
                MeasureNode,
                childrenSource);

            var arrangedBoxes = new Dictionary<Node, BoxModel>();
            GridLayoutComputer.Arrange(
                containerElement,
                new SKRect(0f, 0f, container.Geometry.ContentBox.Width, Math.Max(0f, metrics.ContentHeight)),
                styles,
                arrangedBoxes,
                0,
                ArrangeNode,
                MeasureNode,
                childrenSource);

            float computedContentHeight = Math.Max(metrics.ContentHeight, ComputeChildrenBottom(container));
            computedContentHeight = ApplyHeightConstraints(containerStyle, computedContentHeight, state);

            LayoutBoxOps.ComputeBoxModelFromContent(container, container.Geometry.ContentBox.Width, computedContentHeight);
        }

        private static void CollectNodeMappings(
            LayoutBox box,
            IDictionary<Node, LayoutBox> nodeToBox,
            IDictionary<Node, CssComputed> styles)
        {
            if (box.SourceNode != null)
            {
                if (!nodeToBox.ContainsKey(box.SourceNode))
                {
                    nodeToBox[box.SourceNode] = box;
                }

                if (!styles.ContainsKey(box.SourceNode))
                {
                    styles[box.SourceNode] = box.ComputedStyle ?? new CssComputed();
                }
            }

            foreach (var child in box.Children)
            {
                CollectNodeMappings(child, nodeToBox, styles);
            }
        }

        private static float ComputeChildrenBottom(LayoutBox container)
        {
            if (container.Children.Count == 0)
            {
                return 0f;
            }

            float contentTop = container.Geometry.ContentBox.Top;
            float maxBottom = 0f;
            foreach (var child in container.Children)
            {
                maxBottom = Math.Max(maxBottom, child.Geometry.MarginBox.Bottom - contentTop);
            }

            return Math.Max(0f, maxBottom);
        }

        private static float ApplyHeightConstraints(CssComputed style, float contentHeight, LayoutState state)
        {
            float resolved = Math.Max(0f, contentHeight);
            if (style == null)
            {
                return resolved;
            }

            float containingHeight = state.ContainingBlockHeight > 0f
                ? state.ContainingBlockHeight
                : state.ViewportHeight;

            if (style.Height.HasValue)
            {
                resolved = (float)style.Height.Value;
            }
            else if (style.HeightPercent.HasValue && containingHeight > 0f)
            {
                resolved = (float)(style.HeightPercent.Value / 100d * containingHeight);
            }

            if (style.MinHeight.HasValue)
            {
                resolved = Math.Max(resolved, (float)style.MinHeight.Value);
            }
            else if (style.MinHeightPercent.HasValue && containingHeight > 0f)
            {
                resolved = Math.Max(resolved, (float)(style.MinHeightPercent.Value / 100d * containingHeight));
            }

            if (style.MaxHeight.HasValue)
            {
                resolved = Math.Min(resolved, (float)style.MaxHeight.Value);
            }
            else if (style.MaxHeightPercent.HasValue && containingHeight > 0f)
            {
                resolved = Math.Min(resolved, (float)(style.MaxHeightPercent.Value / 100d * containingHeight));
            }

            return Math.Max(0f, resolved);
        }

        private static void ResolveContainerWidth(LayoutBox box, CssComputed style, LayoutState state)
        {
            style ??= new CssComputed();

            float rawAvailable = state.AvailableSize.Width;
            bool widthUnconstrained = float.IsInfinity(rawAvailable) || float.IsNaN(rawAvailable);
            float available = widthUnconstrained ? state.ViewportWidth : rawAvailable;
            if (float.IsInfinity(available) || float.IsNaN(available) || available <= 0f)
            {
                available = 1920f;
            }

            box.Geometry.Padding = style.Padding;
            box.Geometry.Border = style.BorderThickness;
            box.Geometry.Margin = style.Margin;

            float horizontalChrome = (float)(
                style.Padding.Left + style.Padding.Right +
                style.BorderThickness.Left + style.BorderThickness.Right +
                style.Margin.Left + style.Margin.Right);

            float width;
            if (style.Width.HasValue)
            {
                width = (float)style.Width.Value;
            }
            else if (style.WidthPercent.HasValue)
            {
                width = (float)(style.WidthPercent.Value / 100d * available);
            }
            else if (widthUnconstrained)
            {
                width = Math.Max(0f, available - horizontalChrome);
            }
            else
            {
                width = Math.Max(0f, rawAvailable - horizontalChrome);
            }

            if (style.MinWidth.HasValue)
            {
                width = Math.Max(width, (float)style.MinWidth.Value);
            }
            else if (style.MinWidthPercent.HasValue)
            {
                width = Math.Max(width, (float)(style.MinWidthPercent.Value / 100d * available));
            }

            if (style.MaxWidth.HasValue)
            {
                width = Math.Min(width, (float)style.MaxWidth.Value);
            }
            else if (style.MaxWidthPercent.HasValue)
            {
                width = Math.Min(width, (float)(style.MaxWidthPercent.Value / 100d * available));
            }

            LayoutBoxOps.ComputeBoxModelFromContent(box, Math.Max(0f, width), Math.Max(0f, box.Geometry.ContentBox.Height));
        }
    }
}
