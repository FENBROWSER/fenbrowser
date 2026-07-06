using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// Test-only ILayoutComputer adapter that routes Measure/Arrange to the
    /// production box-tree pipeline via LayoutEngine.ComputeLayout. Lets existing
    /// tests exercise the active code path without per-test rewrites.
    /// </summary>
    public sealed class LayoutEngineComputer : ILayoutComputer
    {
        private readonly LayoutEngine _engine;
        private readonly float _viewportWidth;
        private readonly float _viewportHeight;
        private bool _layoutDone;
        private Node _laidOutRoot;

        public LayoutEngineComputer(IReadOnlyDictionary<Node, CssComputed> styles, float viewportWidth, float viewportHeight, string baseUri = null)
        {
            _viewportWidth = viewportWidth;
            _viewportHeight = viewportHeight;
            _engine = new LayoutEngine(styles, viewportWidth, viewportHeight, baseUri);
        }

        public LayoutEngine Engine => _engine;

        public LayoutMetrics Measure(Node node, SKSize availableSize)
        {
            var root = ResolveLayoutRoot(node);
            _engine.ComputeLayout(root, availableSize.Width, availableSize.Height);
            _layoutDone = true;
            _laidOutRoot = root;
            return ExtractMetrics(node);
        }

        public void Arrange(Node node, SKRect finalRect)
        {
            var root = ResolveLayoutRoot(node);
            if (!_layoutDone || !ReferenceEquals(_laidOutRoot, root))
            {
                _engine.ComputeLayout(root, finalRect.Width, finalRect.Height);
                _layoutDone = true;
                _laidOutRoot = root;
            }
        }

        private static Node ResolveLayoutRoot(Node node)
        {
            // Walk up to a parent whose semantics depend on its children for layout
            // (notably <picture> for responsive <img>). Without this, ComputeLayout
            // starts at the leaf and can't see the parent's <source> siblings.
            if (node is Element el)
            {
                string tag = el.TagName?.ToUpperInvariant();
                if (tag == "IMG" && node.ParentNode is Element parent &&
                    string.Equals(parent.TagName, "PICTURE", System.StringComparison.OrdinalIgnoreCase))
                {
                    return parent;
                }
            }
            return node;
        }

        public BoxModel GetBox(Node node)
        {
            return _engine.AllBoxes.TryGetValue(node, out var box) ? box : null;
        }

        public Node GetParent(Node node)
        {
            return node?.ParentNode;
        }

        public IEnumerable<KeyValuePair<Node, BoxModel>> GetAllBoxes()
        {
            return _engine.AllBoxes;
        }

        public LayoutMetrics MeasureBlock(Element element, SKSize availableSize, int depth) => Measure(element, availableSize);
        public LayoutMetrics MeasureFlex(Element element, SKSize availableSize, int depth) => Measure(element, availableSize);
        public LayoutMetrics MeasureGrid(Element element, SKSize availableSize, int depth) => Measure(element, availableSize);
        public LayoutMetrics MeasureText(Node node, SKSize availableSize) => Measure(node, availableSize);

        public void ArrangeBlock(Element element, SKRect finalRect, int depth) => Arrange(element, finalRect);
        public void ArrangeFlex(Element element, SKRect finalRect, int depth) => Arrange(element, finalRect);
        public void ArrangeGrid(Element element, SKRect finalRect, int depth) => Arrange(element, finalRect);
        public void ArrangeText(Node node, SKRect finalRect) => Arrange(node, finalRect);

        public void DumpLayoutTree(Node root) { }
        public int GetZeroSizedCount() => 0;

        private LayoutMetrics ExtractMetrics(Node node)
        {
            if (!_engine.AllBoxes.TryGetValue(node, out var box) || box == null)
            {
                return new LayoutMetrics();
            }

            float contentWidth = box.ContentBox.Width;
            // Tests expect ContentHeight to represent the box's total laid-out vertical
            // extent (margin box), matching what MinimalLayoutComputer previously returned.
            float contentHeight = box.MarginBox.Height;
            return new LayoutMetrics
            {
                ContentHeight = contentHeight,
                ActualHeight = contentHeight,
                MaxChildWidth = contentWidth,
                MinContentWidth = contentWidth,
                MaxContentWidth = contentWidth,
                Baseline = box.Baseline,
            };
        }
    }
}
