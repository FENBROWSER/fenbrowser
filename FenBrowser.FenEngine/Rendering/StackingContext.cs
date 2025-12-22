using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Represents a CSS Stacking Context (CSS 2.1 Appendix E).
    /// </summary>
    public class StackingContext
    {
        public Node Node { get; set; }
        public int ZIndex { get; set; }
        public bool IsRoot { get; set; }

        // Phase 2: Negative Z-Index Child Contexts
        public List<StackingContext> NegativeZContexts { get; } = new List<StackingContext>();

        // Phase 3-5: Normal Flow Descendants (Blocks, Floats, Inlines)
        // These are painted in tree order (DOM order), interleaved.
        // We store them as a linear list of "Layers" that are NOT Stacking Contexts.
        public List<Node> NormalFlowLayers { get; } = new List<Node>();

        // Phase 6: Positioned Descendants (Z-Index: auto) and Z-Index: 0 Contexts
        public List<Node> PositionedLayers { get; } = new List<Node>();
        public List<StackingContext> ZeroZContexts { get; } = new List<StackingContext>();

        // Phase 7: Positive Z-Index Child Contexts
        public List<StackingContext> PositiveZContexts { get; } = new List<StackingContext>();
        
        public StackingContext(Node node, int zIndex)
        {
            Node = node;
            ZIndex = zIndex;
        }
    }

    public class StackingContextBuilder
    {
        private readonly Dictionary<Node, CssComputed> _styles;
        private readonly HashSet<Node> _handledAsLayerRoot = new HashSet<Node>();

        public StackingContextBuilder(Dictionary<Node, CssComputed> styles)
        {
            _styles = styles;
        }

        public StackingContext BuildTree(Node root)
        {
            _handledAsLayerRoot.Clear();
            var rootContext = new StackingContext(root, 0) { IsRoot = true };
            _handledAsLayerRoot.Add(root);

            ProcessChildren(root, rootContext);
            return rootContext;
        }

        private void ProcessChildren(Node parent, StackingContext currentSC)
        {
            if (parent.Children == null) return;

            foreach (var child in parent.Children)
            {
                if (child is Element element)
                {
                    bool createsSC = false;
                    bool isPositioned = false;
                    int zIndex = 0;

                    if (_styles != null && _styles.TryGetValue(element, out var style))
                    {
                        string pos = style.Position?.ToLowerInvariant() ?? "static";
                        isPositioned = pos == "absolute" || pos == "relative" || pos == "fixed" || pos == "sticky";

                        if (style.ZIndex != null && int.TryParse(style.ZIndex.ToString(), out int z))
                        {
                            zIndex = z;
                        }

                        // Stacking Context Criteria
                        bool hasZIndex = style.ZIndex != null && style.ZIndex.ToString() != "auto";

                        if (isPositioned && hasZIndex) createsSC = true;
                        else if ((style.Opacity ?? 1.0f) < 1.0f) createsSC = true;
                        else if (!string.IsNullOrEmpty(style.Transform) && style.Transform != "none") createsSC = true;
                        else if (!string.IsNullOrEmpty(style.Filter) && style.Filter != "none") createsSC = true;
                        else if (style.Map != null)
                        {
                             if (style.Map.TryGetValue("isolation", out var iso) && iso == "isolate") createsSC = true;
                        }
                    }

                    if (createsSC)
                    {
                        var newSC = new StackingContext(element, zIndex);
                        _handledAsLayerRoot.Add(element);

                        if (zIndex < 0) currentSC.NegativeZContexts.Add(newSC);
                        else if (zIndex > 0) currentSC.PositiveZContexts.Add(newSC);
                        else currentSC.ZeroZContexts.Add(newSC);

                        ProcessChildren(element, newSC);
                    }
                    else if (isPositioned)
                    {
                        // Positioned (z-index: auto) -> Phase 6 Layer
                        currentSC.PositionedLayers.Add(element);
                        _handledAsLayerRoot.Add(element);
                        ProcessChildren(element, currentSC); // Children belong to current SC
                    }
                    else
                    {
                        // Normal Flow -> Phase 3-5
                        // We check if it HAS children that are layers.
                        // We do NOT add the element itself as a 'Layer' unless it needs atomic painting?
                        // 'DrawElement' loop naturally paints normal flow children recursively.
                        // BUT, if we rely on 'DrawStackingContext' to call 'DrawElementChildren',
                        // it iterates SC.Node.Children.
                        // So we don't need to add normal flow elements to a list,
                        // UNLESS we want to separate them from Stacking Context children?
                        // DrawElementChildren iterates DOM children.
                        // If it encounters a generic child, it recurses.
                        // If it encounters a child that IS a Layer Root (in _handledAsLayerRoot), it SKIPS.
                        // So we just need to populate _handledAsLayerRoot correctly.
                        
                        // We recurse into Normal Flow children to find nested Layers.
                        ProcessChildren(element, currentSC);
                    }
                }
            }
        }
        
        // Helper to expose handled roots if needed (or we just rely on SkiaDomRenderer recalculating or checking sc tree?)
        // Better: SkiaDomRenderer can check 'IsLayerRoot'?
        // Actually, SkiaDomRenderer iterates DOM. It needs to know "Skip this child".
        // We can expose the Set.
        public HashSet<Node> GetLayerRoots() => _handledAsLayerRoot;
    }
}
