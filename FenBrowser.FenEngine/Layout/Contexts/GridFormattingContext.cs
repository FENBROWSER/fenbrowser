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
            var childrenSource = new List<Node>(container.Children.Count);
            foreach (var child in container.Children)
            {
                var itemNode = child.SourceNode ?? FindFirstSourceNode(child);
                if (itemNode == null)
                {
                    continue;
                }

                childrenSource.Add(itemNode);
                nodeToBox[itemNode] = child;
                styles[itemNode] = child.ComputedStyle ?? new CssComputed();
            }
            var arrangedBoxes = new Dictionary<Node, BoxModel>();
            var measureCache = new Dictionary<GridMeasureKey, LayoutMetrics>();
            var activeMeasurements = new HashSet<GridMeasureKey>();

            LayoutMetrics MeasureNode(Node node, SKSize availableSize, int depth)
            {
                state.Deadline?.Check();

                if (!nodeToBox.TryGetValue(node, out var childBox))
                {
                    return new LayoutMetrics();
                }

                var key = GridMeasureKey.Create(node, availableSize);
                if (measureCache.TryGetValue(key, out var cachedMetrics))
                {
                    return cachedMetrics;
                }

                if (!activeMeasurements.Add(key))
                {
                    return BuildMetricsFromCurrentGeometry(childBox);
                }

                try
                {
                    float childWidth = availableSize.Width;
                    if (float.IsNaN(childWidth))
                    {
                        childWidth = 0f;
                    }

                    float childHeight = availableSize.Height;
                    bool hasDefiniteChildHeight =
                        !float.IsNaN(childHeight) &&
                        !float.IsInfinity(childHeight) &&
                        childHeight > 0f;
                    if (!hasDefiniteChildHeight)
                    {
                        // Intrinsic grid measurement is not a definite containing block
                        // for percentage-height items. Falling back to the viewport here
                        // lets grid items center inside a synthetic viewport-height track
                        // while the auto-height grid container later shrinks to content.
                        childHeight = 0f;
                    }

                    float containingWidth = (!float.IsInfinity(childWidth) && childWidth > 0f)
                        ? childWidth
                        : Math.Max(0f, container.Geometry.ContentBox.Width);

                    var childState = new LayoutState(
                        new SKSize(childWidth, childHeight),
                        containingWidth,
                        hasDefiniteChildHeight ? childHeight : 0f,
                        state.ViewportWidth,
                        state.ViewportHeight,
                        state.Deadline);

                    FormattingContext.Resolve(childBox).Layout(childBox, childState);

                    float baseline = 0f;
                    if (LayoutBoxOps.TryResolveBaselineOffsetFromMarginTop(childBox, out float resolvedBaseline))
                    {
                        baseline = resolvedBaseline;
                    }

                    var metrics = new LayoutMetrics
                    {
                        MaxChildWidth = Math.Max(0f, childBox.Geometry.MarginBox.Width),
                        ContentHeight = Math.Max(0f, childBox.Geometry.MarginBox.Height),
                        ActualHeight = Math.Max(0f, childBox.Geometry.MarginBox.Height),
                        Baseline = baseline
                    };

                    measureCache[key] = metrics;
                    return metrics;
                }
                finally
                {
                    activeMeasurements.Remove(key);
                }
            }

            void ArrangeNode(Node node, SKRect rect, int depth)
            {
                if (!nodeToBox.TryGetValue(node, out var childBox))
                {
                    return;
                }

                state.Deadline?.Check();

                float width = Math.Max(0f, rect.Width);
                float height = Math.Max(0f, rect.Height);

                // Set content width; height will be resolved by the child's layout pass
                // then clamped to the grid row height so all items in a row are equal.
                LayoutBoxOps.ComputeBoxModelFromContent(childBox, width, Math.Max(0f, childBox.Geometry.ContentBox.Height));

                float absoluteLeft = container.Geometry.ContentBox.Left + rect.Left;
                float absoluteTop = container.Geometry.ContentBox.Top + rect.Top;

                var childState = new LayoutState(
                    new SKSize(width, height),
                    width,
                    height > 0f ? height : state.ContainingBlockHeight,
                    state.ViewportWidth,
                    state.ViewportHeight,
                    state.Deadline);

                LayoutBoxOps.PositionSubtree(childBox, absoluteLeft, absoluteTop, childState);
                FormattingContext.Resolve(childBox).Layout(childBox, childState);

                // CSS Grid spec §12.4: all items in the same row share the row height
                // (the maximum of the row's track size).  The child's re-layout above
                // computes its natural content height; clamp to the grid-assigned cell
                // height so every item in the row fills the same vertical space.
                // rect.Height is the track size which corresponds to margin-box height;
                // subtract padding+border+margin to derive the required content height.
                if (height > 0f)
                {
                    float naturalContentHeight = childBox.Geometry.ContentBox.Height;
                    float verticalChrome =
                        (float)(childBox.Geometry.Padding.Top + childBox.Geometry.Padding.Bottom) +
                        (float)(childBox.Geometry.Border.Top + childBox.Geometry.Border.Bottom) +
                        (float)(childBox.Geometry.Margin.Top + childBox.Geometry.Margin.Bottom);
                    float requiredContentHeight = Math.Max(0f, height - verticalChrome);
                    if (naturalContentHeight < requiredContentHeight)
                    {
                        LayoutBoxOps.ComputeBoxModelFromContent(childBox, childBox.Geometry.ContentBox.Width, requiredContentHeight);
                    }
                }

                // Child layout may have recomputed local geometry; keep final grid placement.
                LayoutBoxOps.PositionSubtree(childBox, absoluteLeft, absoluteTop, childState);
                arrangedBoxes[node] = childBox.Geometry;
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

        private static LayoutMetrics BuildMetricsFromCurrentGeometry(LayoutBox box)
        {
            if (box?.Geometry == null)
            {
                return new LayoutMetrics();
            }

            float baseline = 0f;
            if (LayoutBoxOps.TryResolveBaselineOffsetFromMarginTop(box, out float resolvedBaseline))
            {
                baseline = resolvedBaseline;
            }

            return new LayoutMetrics
            {
                MaxChildWidth = Math.Max(0f, box.Geometry.MarginBox.Width),
                ContentHeight = Math.Max(0f, box.Geometry.MarginBox.Height),
                ActualHeight = Math.Max(0f, box.Geometry.MarginBox.Height),
                Baseline = baseline
            };
        }

        private readonly struct GridMeasureKey : IEquatable<GridMeasureKey>
        {
            private readonly Node _node;
            private readonly int _width;
            private readonly int _height;

            private GridMeasureKey(Node node, int width, int height)
            {
                _node = node;
                _width = width;
                _height = height;
            }

            public static GridMeasureKey Create(Node node, SKSize availableSize)
            {
                return new GridMeasureKey(
                    node,
                    NormalizeDimension(availableSize.Width),
                    NormalizeDimension(availableSize.Height));
            }

            public bool Equals(GridMeasureKey other)
            {
                return ReferenceEquals(_node, other._node) &&
                       _width == other._width &&
                       _height == other._height;
            }

            public override bool Equals(object obj)
            {
                return obj is GridMeasureKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(_node, _width, _height);
            }

            private static int NormalizeDimension(float value)
            {
                if (float.IsNaN(value))
                {
                    return int.MinValue;
                }

                if (float.IsPositiveInfinity(value))
                {
                    return int.MaxValue;
                }

                if (float.IsNegativeInfinity(value))
                {
                    return int.MinValue + 1;
                }

                return (int)MathF.Round(value * 100f);
            }
        }

        internal static void CollectNodeMappings(
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

            var children = box.Children;
            for (var index = 0; index < children.Count; index++)
            {
                CollectNodeMappings(children[index], nodeToBox, styles);
            }
        }

        private static Node FindFirstSourceNode(LayoutBox box)
        {
            foreach (var child in box.Children)
            {
                if (child.SourceNode != null)
                {
                    return child.SourceNode;
                }

                var descendant = FindFirstSourceNode(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }

            return null;
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

            var widthResolution = LayoutConstraintResolver.ResolveWidth(state, "Grid.ResolveContainerWidth");
            float rawAvailable = widthResolution.RawAvailable;
            bool widthUnconstrained = widthResolution.IsUnconstrained;
            float available = widthResolution.ResolvedAvailable;

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
