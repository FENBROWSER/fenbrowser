using System;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.FenEngine.Layout.Coordinates;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Logging;
using SkiaSharp;
using System.Text;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.FenEngine.Layout;  // For MarginCollapseComputer, AbsolutePositionSolver

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Minimal layout computer implementation.
    /// REVISED: Added User Agent (UA) Default Styles to fix missing margins/padding.
    ///          Improved Heuristics for "Example Domain" centering.
    /// </summary>
    public partial class MinimalLayoutComputer : ILayoutComputer
    {
        private const float DefaultFontSize = 16f;
        private readonly ConcurrentDictionary<Node, BoxModel> _boxes = new ConcurrentDictionary<Node, BoxModel>();
        private readonly Dictionary<Node, SKSize> _desiredSizes = new Dictionary<Node, SKSize>();
        private readonly Dictionary<Node, Node> _parents = new Dictionary<Node, Node>();
        private readonly IReadOnlyDictionary<Node, CssComputed> _styles;
        private readonly float _viewportWidth;
        private readonly float _viewportHeight;
        private int _traversedCount = 0;
        
        // Cache for text lines computed during Measure
        private readonly Dictionary<Node, List<ComputedTextLine>> _textLines = new Dictionary<Node, List<ComputedTextLine>>();
        // Cache for inline layout results
        private readonly Dictionary<Element, InlineLayoutResult> _inlineCache = new Dictionary<Element, InlineLayoutResult>();
        
        // Cache for table states
        private readonly ConditionalWeakTable<Element, TableLayoutComputer.TableGridState> _tableStates = new ConditionalWeakTable<Element, TableLayoutComputer.TableGridState>();
        
        // Track ancestors during Arrange for absolute positioning CB resolution
        private readonly Dictionary<Node, SKRect> _ancestorRects = new Dictionary<Node, SKRect>();
        private readonly Dictionary<Node, (float Top, float Bottom)> _effectiveMargins = new Dictionary<Node, (float Top, float Bottom)>();
        
        // Stack of active Float Exclusions for each Block Formatting Context (BFC)
        // Root is pushed in constructor.
        private readonly Stack<List<FloatExclusion>> _activeBfcFloats = new Stack<List<FloatExclusion>>();
        
        private readonly string _baseUri;
        private int _zeroSizedCount = 0;

        public MinimalLayoutComputer(IReadOnlyDictionary<Node, CssComputed> styles, float viewportWidth, float viewportHeight, string baseUri = null)
        {
            _styles = styles ?? new Dictionary<Node, CssComputed>();
            _viewportWidth = viewportWidth > 0 ? viewportWidth : 1920;
            _viewportHeight = viewportHeight > 0 ? viewportHeight : 1080;
            _baseUri = baseUri;
            
            // Push Root BFC
            _activeBfcFloats.Push(new List<FloatExclusion>());
        }

        // --- INTERNAL WRAPPERS FOR ALGORITHMS ---
        internal LayoutMetrics MeasureInlineContextInternal(Element element, SKSize availableSize, int depth) => MeasureInlineContext(element, availableSize, depth);
        internal void ArrangeBlockInternalInternal(Element element, SKRect finalRect, int depth, Node fallbackNode) => ArrangeBlockInternal(element, finalRect, depth, fallbackNode);

        /// <summary>
        /// Gets the computed style for the given node.
        /// </summary>
        internal CssComputed GetStyleInternal(Node node) => GetStyle(node);
        // Get computed style for a node - UA defaults now handled by ua.css
        private CssComputed GetStyle(Node node)
        {
            if (node == null) return null;
            if (node is PseudoElement pe) return pe.ComputedStyle;
            if (!_styles.TryGetValue(node, out var style) || style == null)
            {
                if (node.IsText) return null; // Allow inheritance from parent
                style = new CssComputed();
            }
            
            // IMPORTANT: Read CSS properties from Map when typed properties are null
            if (style.Map != null)
            {
                if (style.Display == null && style.Map.TryGetValue("display", out var mapDisplay))
                    style.Display = mapDisplay;
                if (style.Visibility == null && style.Map.TryGetValue("visibility", out var mapVisibility))
                    style.Visibility = mapVisibility;
                if (style.FlexDirection == null && style.Map.TryGetValue("flex-direction", out var mapFlexDir))
                    style.FlexDirection = mapFlexDir;
                if (style.FlexWrap == null && style.Map.TryGetValue("flex-wrap", out var mapFlexWrap))
                    style.FlexWrap = mapFlexWrap;
                if (style.JustifyContent == null && style.Map.TryGetValue("justify-content", out var mapJustify))
                    style.JustifyContent = mapJustify;
                if (style.AlignItems == null && style.Map.TryGetValue("align-items", out var mapAlign))
                    style.AlignItems = mapAlign;
                if (style.AlignContent == null && style.Map.TryGetValue("align-content", out var mapAlignContent))
                    style.AlignContent = mapAlignContent;
                if (style.Position == null && style.Map.TryGetValue("position", out var mapPos))
                    style.Position = mapPos;
                    
                // ACID2 FIX: Sync overflow properties for clipping
                if (style.Overflow == null && style.Map.TryGetValue("overflow", out var mapOverflow))
                    style.Overflow = mapOverflow;
                if (style.OverflowX == null && style.Map.TryGetValue("overflow-x", out var mapOverflowX))
                    style.OverflowX = mapOverflowX;
                if (style.OverflowY == null && style.Map.TryGetValue("overflow-y", out var mapOverflowY))
                    style.OverflowY = mapOverflowY;
            }
            
            // (UA Defaults are now handled by ua.css loaded in CssLoader)
            // Note: Special handling for DETAILS/SUMMARY visibility might be needed here if CSS selectors 
            // for :not([open]) are not fully supported yet.
            if (node is Element elem)
            {
                 // Handle children of closed <details> - hide non-summary elements
                 // This is effectively: details:not([open]) > :not(summary) { display: none; }
                 if (elem.Parent is Element parentEl && string.Equals(parentEl.TagName, "DETAILS", StringComparison.OrdinalIgnoreCase))
                 {
                     bool isOpen = parentEl.HasAttribute("open");
                     bool isSummary = string.Equals(elem.TagName, "SUMMARY", StringComparison.OrdinalIgnoreCase);
                     
                     if (!isOpen && !isSummary && style.Display != "none")
                     {
                         style.Display = "none";
                     }
                 }
            }
            return style;
        }

        // Reflection helpers REMOVED - Direct assignment
        private void SetMargin(CssComputed style, double val) {
            style.Margin = new Thickness(val);
        }
        private void SetMarginVertical(CssComputed style, double val) {
             var current = style.Margin;
             style.Margin = new Thickness(current.Left, val, current.Right, val);
        }
        private void SetPadding(CssComputed style, double val) {
            style.Padding = new Thickness(val);
        }
        private void SetBackgroundColor(CssComputed style, uint color) {
            try {
                // Assuming it's SKColor property. If it's string or other, this might fail silently (catch block).
                // FenBrowser.Core likely uses SKColor.
                var prop = style.GetType().GetProperty("BackgroundColor");
                if (prop != null && prop.CanWrite) {
                     if (prop.PropertyType == typeof(SKColor))
                        prop.SetValue(style, new SKColor(color));
                     else if (prop.PropertyType == typeof(string))
                        prop.SetValue(style, $"#{color:X8}");
                }
            } catch {}
        }

        internal IEnumerable<Node> GetChildrenWithPseudosInternal(Element element, Node fallbackNode) => GetChildrenWithPseudos(element, fallbackNode);
        private IEnumerable<Node> GetChildrenWithPseudos(Element element, Node fallbackNode = null)
        {
            var children = element?.Children ?? fallbackNode?.Children;
            if (element == null) 
            {
                if (children != null) foreach (var c in children) yield return c;
                yield break;
            }

            var style = GetStyle(element);
            
            // Before
            if (style != null && style.Before != null && IsVisiblePseudo(style.Before))
            {
                 if (style.Before.PseudoElementInstance == null)
                 {
                     style.Before.PseudoElementInstance = new PseudoElement(element, "before", style.Before);
                     if (!string.IsNullOrEmpty(style.Before.Content) && style.Before.Content != "none" && !style.Before.Content.Contains("url("))
                     {
                         string text = style.Before.Content.Trim('"', '\'');
                         style.Before.PseudoElementInstance.AppendChild(new Text(text));
                     }
                 }
                 yield return style.Before.PseudoElementInstance;
            }

            if (children != null)
            {
                foreach(var c in children) yield return c;
            }

            // After
            if (style != null && style.After != null && IsVisiblePseudo(style.After))
            {
                 if (style.After.PseudoElementInstance == null)
                 {
                     style.After.PseudoElementInstance = new PseudoElement(element, "after", style.After);
                     if (!string.IsNullOrEmpty(style.After.Content) && style.After.Content != "none" && !style.After.Content.Contains("url("))
                     {
                         string text = style.After.Content.Trim('"', '\'');
                         style.After.PseudoElementInstance.AppendChild(new Text(text));
                     }
                 }
                 yield return style.After.PseudoElementInstance;
            }
        }

        private static bool IsVisiblePseudo(CssComputed pseudoStyle)
        {
            return !string.IsNullOrEmpty(pseudoStyle.Content) && pseudoStyle.Content != "none";
        }
        
        private void SetProperty(CssComputed style, string name, object val) {
             try {
                var prop = style.GetType().GetProperty(name);
                if (prop != null && prop.CanWrite) prop.SetValue(style, val);
            } catch {}
        }
        
        public BoxModel GetBox(Node node) => (node != null && _boxes.TryGetValue(node, out var box)) ? box : null;
        public Node GetParent(Node node) => (node != null && _parents.TryGetValue(node, out var parent)) ? parent : null;
        public IEnumerable<KeyValuePair<Node, BoxModel>> GetAllBoxes() => _boxes;
        
        public LayoutMetrics Measure(Node node, SKSize availableSize)
        {
            if (node == null) return new LayoutMetrics();
            
            _traversedCount = 0;
            _textLines.Clear(); 
            _inlineCache.Clear(); 
            return MeasureNode(node, availableSize, 0); 
        }

        public void Arrange(Node node, SKRect finalRect)
        {
            if (node == null) return;
            FenLogger.Debug($"[MinimalLayoutComputer] Arrange node={node.Tag ?? "#text"} rect={finalRect}", LogCategory.Rendering);
            
            _traversedCount = 0;
            _ancestorRects.Clear();
            ArrangeNode(node, finalRect, 0); // Start at 0
        }

        internal void SetDesiredSize(Node node, SKSize size)
        {
             _desiredSizes[node] = size;
        }

        public LayoutMetrics MeasureNodePublic(Node node, SKSize availableSize, int depth) => MeasureNode(node, availableSize, depth);
        private LayoutMetrics MeasureNode(Node node, SKSize availableSize, int depth, bool shrinkToFit = false)
        {
            try
            {
                if (node == null) return new LayoutMetrics();
                if (depth > 80) return new LayoutMetrics();

            var style = GetStyle(node);
            bool isHidden = ShouldHide(node, style);
            if (isHidden) return new LayoutMetrics();

            // (V6: Removed V5 logging block)

            float w = 0, h = 0;
            string tag = (node as Element)?.TagName?.ToUpperInvariant() ?? (node.IsText ? "#text" : "");
            
            bool isExplicitWidth = false;
            bool isExplicitHeight = false;
            
            // ========== ENGINE-LEVEL ICB INVARIANT ==========
            // The Initial Containing Block (ICB) is the viewport.
            // Root elements (HTML at depth 0) MUST resolve against viewport dimensions.
            // This is NOT optional - the layout engine enforces this.
            // ================================================
            bool isRootElement = (depth == 0 && tag == "HTML");
            bool isBodyElement = (depth == 1 && tag == "BODY");
            
            // Diagnostic: Log root element detection
            if (depth <= 1)
            {
                FenLogger.Log($"[ICB-DIAG] depth={depth} tag={tag} isRoot={isRootElement} viewport={_viewportHeight}", LogCategory.Layout);
            }
            
            // For root elements, ensure availableSize always includes viewport height
            if (isRootElement)
            {
                FenLogger.Log($"[ICB] Root element detected. Forcing availableSize to viewport: {_viewportWidth}x{_viewportHeight}", LogCategory.Layout);
                availableSize = new SKSize(
                    availableSize.Width > 0 ? availableSize.Width : _viewportWidth,
                    _viewportHeight // Root ALWAYS gets viewport height as its available height
                );
            }

            if (style != null)
            {
                if (style.Width.HasValue) 
                { 
                    w = (float)style.Width.Value; 
                    isExplicitWidth = true; 
                }
                else if (!string.IsNullOrEmpty(style.WidthExpression))
                {
                    w = LayoutHelper.EvaluateCssExpression(style.WidthExpression, availableSize.Width, _viewportWidth, _viewportHeight);
                    isExplicitWidth = true;
                }
                else if (style.WidthPercent.HasValue)
                {
                    w = (float)style.WidthPercent.Value / 100f * availableSize.Width;
                    isExplicitWidth = true;
                }

                if (style.Height.HasValue) 
                { 
                    h = (float)style.Height.Value; 
                    isExplicitHeight = true; 
                }
                else if (!string.IsNullOrEmpty(style.HeightExpression))
                {
                    // Height expression (e.g., calc(), vh, vmin, etc.)
                    h = LayoutHelper.EvaluateCssExpression(style.HeightExpression, availableSize.Height, _viewportWidth, _viewportHeight);
                    isExplicitHeight = true;
                }
                else if (style.HeightPercent.HasValue)
                {
                    // FIX: Process Height Percentage
                    // ROOT ELEMENT SPECIAL CASE: For <html> and <body>, resolve against viewport
                    float parentHeight = availableSize.Height;
                    
                    // Root elements (html/body) with height: 100% should ALWAYS get viewport height
                    if ((isRootElement || isBodyElement) && (float.IsInfinity(parentHeight) || parentHeight <= 0))
                    {
                        parentHeight = _viewportHeight;
                    }
                    
                    if (!float.IsInfinity(parentHeight) && parentHeight > 0)
                    {
                        h = (float)style.HeightPercent.Value / 100f * parentHeight;
                        isExplicitHeight = true;
                    }
                }
                // CRITICAL ICB RULE: Root element (HTML) height = viewport when auto
                // This is NOT optional. This is the Initial Containing Block invariant.
                // Without this, percentage heights on children (body, main content) fail.
                else if (isRootElement)
                {
                    // html.height is auto -> html.height = ICB.height (viewport)
                    h = _viewportHeight;
                    isExplicitHeight = true;
                    FenLogger.Log($"[ICB] HTML height auto -> forced to viewport: {_viewportHeight}px", LogCategory.Layout);
                }
            }

            // Min/Max Constraints
            float minW = 0, maxW = float.PositiveInfinity;
            float minH = 0, maxH = float.PositiveInfinity;
            
            if (style != null)
            {
                // FIX: Evaluate ALL min/max expressions (width and height)
                // MinWidth
                if (style.MinWidth.HasValue) minW = (float)style.MinWidth.Value;
                else if (style.MinWidthExpression != null) 
                    minW = LayoutHelper.EvaluateCssExpression(style.MinWidthExpression, availableSize.Width, _viewportWidth, _viewportHeight);
                
                // MaxWidth
                if (style.MaxWidth.HasValue) maxW = (float)style.MaxWidth.Value;
                else if (style.MaxWidthExpression != null) 
                    maxW = LayoutHelper.EvaluateCssExpression(style.MaxWidthExpression, availableSize.Width, _viewportWidth, _viewportHeight);

                // MinHeight - CRITICAL FIX: Was missing expression evaluation
                // This fixes body { min-height: 100% } not working
                if (style.MinHeight.HasValue) minH = (float)style.MinHeight.Value;
                else if (style.MinHeightExpression != null)
                {
                    // For percentage min-height, resolve against parent's height (availableSize.Height)
                    // If availableSize.Height is infinite, use viewport height as fallback
                    float resolveAgainst = float.IsInfinity(availableSize.Height) ? _viewportHeight : availableSize.Height;
                    minH = LayoutHelper.EvaluateCssExpression(style.MinHeightExpression, resolveAgainst, _viewportWidth, _viewportHeight);
                }
                
                // CRITICAL: Root element (HTML) must be at least viewport height
                // This is the ICB invariant - never allow root height to default to content-only
                if (isRootElement && minH < _viewportHeight)
                {
                    minH = _viewportHeight;
                }
                
                // MaxHeight - CRITICAL FIX: Was missing expression evaluation
                if (style.MaxHeight.HasValue) maxH = (float)style.MaxHeight.Value;
                else if (style.MaxHeightExpression != null)
                {
                    float resolveAgainst = float.IsInfinity(availableSize.Height) ? _viewportHeight : availableSize.Height;
                    maxH = LayoutHelper.EvaluateCssExpression(style.MaxHeightExpression, resolveAgainst, _viewportWidth, _viewportHeight);
                }
                
                // Clamp explicit (or implicit) width/height to constraints immediately
                // Note: If w is 0 (auto), we apply constraints later after measurement, 
                // BUT for childConstraint calculation, determining 'w' now helps.
                if (isExplicitWidth)
                {
                    w = Math.Max(minW, Math.Min(w, maxW));
                }
                
                if (isExplicitHeight)
                {
                    h = Math.Max(minH, Math.Min(h, maxH));
                }
            }

            SKSize childConstraint = availableSize;
            // Only cap to viewport if we are measuring (intrinsic sizing) with infinite constraint
            if (float.IsInfinity(childConstraint.Width) || childConstraint.Width > 1e6f)
                childConstraint.Width = _viewportWidth > 0 ? _viewportWidth : 800;

            // CRITICAL FIX: If THIS element has an explicit height (e.g. html { height: 100% }),
            // pass that resolved height to children so they can resolve THEIR percentage heights.
            // This establishes the CSS height chain: viewport → html → body → content
            if (isExplicitHeight && h > 0)
            {
                // Use the resolved height (after min/max constraints) for children
                float childAvailableHeight = h;
                
                // Subtract padding/border to get content box height for children
                if (style != null)
                {
                    var p = style.Padding;
                    var b = style.BorderThickness;
                    childAvailableHeight -= (float)(p.Top + p.Bottom + b.Top + b.Bottom);
                    if (childAvailableHeight < 0) childAvailableHeight = 0;
                }
                
                childConstraint = new SKSize(childConstraint.Width, childAvailableHeight);
            }

            if (w > 0) 
            {
                float contentW = w;
                if (style != null)
                {
                   var styleMargin = style.Margin;
                   var p = style.Padding;
                   var b = style.BorderThickness;
                   if (style.BoxSizing == "border-box")
                        contentW -= (float)(p.Left + p.Right + b.Left + b.Right);
                }
                childConstraint = new SKSize(Math.Max(0, contentW), childConstraint.Height);
            }


            LayoutMetrics m;
            
            // REMOVED: shouldFillViewport hack. Root elements should respect CSS.
            bool shouldFillViewport = false;
            string id = (node as Element)?.GetAttribute("id")?.ToLowerInvariant();
            if (tag == "IMG" || tag == "SVG" || tag.EndsWith(":SVG")) {
                   var s = GetStyle(node as Element);
                   var p = node.Parent as Element;
                   FenLogger.Debug($"[IMG-TRACE] Tag={tag} id={id} Opacity={s?.Opacity} Vis={s?.Visibility} Parent={p?.TagName} ParentClass={p?.GetAttribute("class")}");
                }
            // REMOVED: isNewTab hack - flexbox now works generically
            string display = style?.Display?.ToLowerInvariant();

            var elem = node as Element;
            
            if (tag == "IMG" || tag == "SVG" || tag.EndsWith(":SVG")) 
                m = MeasureImage(elem, childConstraint);
            else if (tag == "INPUT")
                m = MeasureInput(elem, childConstraint, depth + 1);
            else if (tag == "TEXTAREA") 
                m = MeasureInput(elem, childConstraint, depth + 1); 
            else if (tag == "BUTTON")
                m = MeasureButton(elem, childConstraint, depth + 1);
            else if (tag == "VIDEO")
                m = MeasureVideo(elem, childConstraint);
            else if (tag == "AUDIO")
                m = MeasureAudio(elem, childConstraint);
            else if (tag == "IFRAME")
                m = MeasureIframe(elem, childConstraint);
            else if (tag == "PROGRESS")
                m = MeasureProgress(elem, childConstraint);
            else if (tag == "METER")
                m = MeasureMeter(elem, childConstraint);
            else if (display == "flex" || display == "inline-flex") 
            {
                m = MeasureFlexInternal(elem, childConstraint, (display == "flex" && elem.TagName == "BODY"), depth + 1, node);
                
                // Fix: display:flex (block-level) should default to available width if not explicit, just like display:block
                if (display == "flex" && !isExplicitWidth && !float.IsInfinity(availableSize.Width))
                {
                    m.MaxChildWidth = Math.Max(m.MaxChildWidth, availableSize.Width);
                }
            } 
            else if (display == "grid")
                m = MeasureGrid(elem, childConstraint, depth + 1);
            else if (IsMultiColumn(style))
                m = MeasureMultiColumn(elem, childConstraint, depth + 1);
            else if (tag == "TABLE")
            {
                m = TableLayoutComputer.Measure(elem, childConstraint, _styles, (n, s, d) => MeasureNode(n, s, d), depth + 1, out var tableState);
                _tableStates.AddOrUpdate(elem, tableState);
            }

            else if (node.IsText) 
            {
                // DIAGNOSTIC: Trace text nodes containing 'ai'
                if (node is Text textNode)
                {
                    string txt = textNode.Data?.ToLowerInvariant() ?? "";
                    if (txt.Contains("ai"))
                    {
                         var parentEl = node.Parent as Element;
                         bool isAiMode = txt.Contains("ai mode");
                         FenLogger.Debug($"[AI-TRACE] Text='{txt}' Parent={parentEl?.TagName} Class={parentEl?.GetAttribute("class")}");
                    }
                }
                m = MeasureText(node, childConstraint);
            }
            else 
            {
                // Refactored to use BlockLayoutAlgorithm (Strategy Pattern)
                var context = new FenBrowser.FenEngine.Layout.Algorithms.LayoutContext
                {
                    Node = elem,
                    Style = style, // Available in scope
                    AvailableSize = childConstraint,
                    Depth = depth + 1,
                    Computer = this,
                    FallbackNode = node
                };
                m = new FenBrowser.FenEngine.Layout.Algorithms.BlockLayoutAlgorithm().Measure(context); 
            }

            // DIAGNOSTIC: Log measurement result
            if (depth < 10 && elem != null)
            {
                string childInfo = "";
                if (elem.Children != null) childInfo = $"children={elem.Children.Count}";
                // (V5: Removed logging block moved to start)
            }

            if (w <= 0 && !isExplicitWidth) w = m.MaxChildWidth;
            if (h <= 0 && !isExplicitHeight) h = m.ContentHeight;
            
            if (tag == "BODY" || tag == "HTML" || depth < 8) 
                FenLogger.Debug($"[MEASURE-TRACE] depth={depth} tag={tag} m.h={m.ContentHeight} res.w={w} res.h={h}", LogCategory.Rendering);

            // Add Padding/Border
            float pt = 0, pb = 0, bl = 0, br = 0, bt_top = 0, bb = 0;
            if (style != null)
            {
                pt = (float)style.Padding.Top;
                pb = (float)style.Padding.Bottom;
                bt_top = (float)style.BorderThickness.Top;
                bb = (float)style.BorderThickness.Bottom;
                
                if (style.BoxSizing != "border-box")
                {
                    w += (float)(style.Padding.Left + style.Padding.Right + style.BorderThickness.Left + style.BorderThickness.Right);
                    h += (float)(pt + pb + bt_top + bb);
                }
            }
            
            float mt = 0, mb = 0, ml = 0, mr = 0;
            if (style != null && !shouldFillViewport)
            {
                mt = (float)style.Margin.Top;
                mb = (float)style.Margin.Bottom;
                ml = (float)style.Margin.Left;
                mr = (float)style.Margin.Right;
            }

            // MARGIN COLLAPSE WITH CHILDREN
            // Vertical margins collapse with children if there's no border/padding
            // Root element (HTML) should NOT collapse with children, otherwise margins escape the document
            bool canCollapseWithChildren = (display == "block" || display == null) && tag != "HTML";
            
            if (canCollapseWithChildren)
            {
                // Top Margin Collapse
                // Note: MeasureBlockInternal should return effective (collapsed) top margin if it collapsed with first child.
                // But generally, MeasureBlock accumulates height assuming no collapse, then we check here.
                // ACTUALLY: The correct way is:
                // If we collapse with first child, the parent's effective top margin becomes Collapse(parent.marginTop, child.marginTop).
                // The child's margin is visually "swallowed" into the parent's.
                // Ideally, 'm.MarginTop' from MeasureBlock represents the child's top margin that bubble up.

                if (MarginCollapseComputer.ShouldCollapseParentChildTop(style))
                {
                    // Collapse parent's top with 1st child's top (m.MarginTop)
                    mt = MarginCollapseComputer.Collapse(mt, m.MarginTop);
                    // Child's margin is now part of 'mt', so don't add it to 'h'
                }
                else
                {
                    // No collapse (border/padding exists), so child margin contributes to content height
                    h += m.MarginTop;
                }

                // Bottom Margin Collapse
                if (MarginCollapseComputer.ShouldCollapseParentChildBottom(style))
                {
                    // Collapse parent's bottom with last child's bottom (m.MarginBottom)
                    mb = MarginCollapseComputer.Collapse(mb, m.MarginBottom);
                    // Child's margin is part of 'mb'
                }
                else
                {
                    h += m.MarginBottom;
                }
            }
            else
            {
                // Flex, Grid, etc do NOT collapse margins with children
                h += m.MarginTop;
                h += m.MarginBottom;
            }

            float naturalH = h;
            if (isExplicitHeight) 
            {
                 float borderPaddingH = 0;
                 if (style != null && style.BoxSizing != "border-box")
                 {
                     borderPaddingH = (float)(style.Padding.Top + style.Padding.Bottom + style.BorderThickness.Top + style.BorderThickness.Bottom);
                 }
                 naturalH = m.ContentHeight + m.MarginTop + m.MarginBottom + borderPaddingH;
            }

            if (shouldFillViewport)
            {
               // Removed forced expansion
            }
            else
            {
                bool isBlock = (display == "block" || display == null); 
                bool isFloat = style?.Float?.ToLowerInvariant() == "left";
                
                if (!isExplicitWidth && isBlock && !node.IsText && tag != "IMG" && !isFloat && 
                    tag != "BUTTON" && tag != "INPUT" && tag != "TEXTAREA" && tag != "SELECT" && tag != "LABEL") 
                {
                     // CRITICAL FIX for Flex Center Alignment:
                     // If shrinkToFit is true, we skip this eager block expansion.
                     // This allows blocks in column flex containers (with align-items:center) 
                     // to report their content size instead of stretching to container width.
                     if (!shrinkToFit)
                     {
                         // Use availableSize, not childConstraint (which already has padding subtracted)
                         float usedWidth = availableSize.Width - (ml + mr);
                         if (style != null && style.BoxSizing != "border-box")
                         {
                         // FIX: Subtract Horizontal Padding/Border (Left+Right)
                         float paddingH = (float)(style.Padding.Left + style.Padding.Right);
                         float borderH = (float)(style.BorderThickness.Left + style.BorderThickness.Right);
                         usedWidth -= (paddingH + borderH);
                     }

                     w = float.IsInfinity(usedWidth) ? m.MaxChildWidth : Math.Max(0, usedWidth);
                     }
                }
                else if (w <= 0 && !node.IsText) 
                {
                     bool isInlineBlock = (display == "inline-block" || display == "inline");
                     if (!isFloat && !isInlineBlock) {
                         // Use availableSize, not childConstraint (which already has padding subtracted)
                         float usedWidth = availableSize.Width - (ml + mr);
                         
                         // DEBUG: Trace width calc for specific elements
                         bool isDebugTarget = false;
                         string dbgTag = node is Element el ? el.TagName : "NODE";
                         string dbgClass = node is Element el2 ? el2.GetAttribute("class") : "";
                         string dbgStyle = node is Element el3 ? el3.GetAttribute("style") : "";
                         
                         if (dbgClass != null && dbgClass.Contains("test-case")) isDebugTarget = true;
                         if (dbgStyle != null && dbgStyle.Contains("#ffc")) isDebugTarget = true;
                         if (dbgClass != null && dbgClass.Contains("label")) isDebugTarget = true;


                         if (style != null && style.BoxSizing != "border-box")
                         {
                             var p = style.Padding;
                             var b = style.BorderThickness;
                             
                             if (isDebugTarget)
                             {
                                 FenLogger.Debug($"[WIDTH-TRACE] {dbgTag} class='{dbgClass}' style='{dbgStyle}' Available={availableSize.Width} Margins={ml}+{mr} Padding={p.Left}+{p.Right} Border={b.Left}+{b.Right}");
                             }
                             
                             usedWidth -= (float)(p.Left + p.Right + b.Left + b.Right);
                             
                             if (isDebugTarget)
                             {
                                 FenLogger.Debug($"[WIDTH-TRACE] FinalUsedWidth={usedWidth} (Rendered={usedWidth + p.Left + p.Right + b.Left + b.Right})");
                             }
                         }
                         w = float.IsInfinity(usedWidth) ? m.MaxChildWidth : Math.Max(0, usedWidth);
                     } 
                }
            }

            // Apply Min/Max Constraints on final calculated dimensions (on Content Box)
            w = Math.Max(minW, Math.Min(w, maxW));
            /* ActualHeight tracks overflow, but ContentHeight (h) should respect sizing constraints for block flow */
            h = Math.Max(minH, Math.Min(h, maxH));
            
            // FIX: If content-box, we must add padding/border back to w/h for the LayoutMetrics 
            // because ArrangeBlock expects the full BorderBox size to allocate space.
            // ALSO FIX: If !isExplicitWidth (Auto Width), 'w' is derived from content measurement (e.g. flex children).
            // We must add padding/border to get the physical BorderBox size, regardless of box-sizing.
            if (style != null && (style.BoxSizing != "border-box" || !isExplicitWidth))
            {
                 var p = style.Padding;
                 var b = style.BorderThickness;
                 
                 // If !isExplicitWidth, w is content width (from children). Add padding/border.
                 // If content-box, w is content width. Add padding/border.
                 // Result: w becomes BorderBox width.
                 
                 w += (float)(p.Left + p.Right + b.Left + b.Right);
                 h += (float)(p.Top + p.Bottom + b.Top + b.Bottom);
            }
            
            // ============================================================
            // CRITICAL ICB OVERRIDE: HTML and BODY MUST have height = viewport
            // This is non-negotiable. Without this, justify-content fails.
            // ============================================================
            if (depth == 0 && tag == "HTML")
            {
                if (h < _viewportHeight)
                {
                    FenLogger.Log($"[ICB] OVERRIDE: HTML height {h}px < viewport {_viewportHeight}px. Forcing to viewport.", LogCategory.Layout);
                    h = _viewportHeight;
                }
                if (w < _viewportWidth)
                {
                    w = _viewportWidth;
                }
            }
            // BODY must also stretch to viewport for flex centering to work
            // BODY can be at depth 1 or 2 depending on HEAD presence
            else if (depth <= 2 && tag == "BODY")
            {
                if (h < _viewportHeight)
                {
                    FenLogger.Log($"[ICB] OVERRIDE: BODY height {h}px < viewport {_viewportHeight}px. Forcing to viewport.", LogCategory.Layout);
                    h = _viewportHeight;
                }
                if (w < _viewportWidth)
                {
                    w = _viewportWidth;
                }
            }
            
            var size = new SKSize(w, h);
            _desiredSizes[node] = size;
            _effectiveMargins[node] = (mt, mb); // Store effective margins

            return new LayoutMetrics { 
                ContentHeight = h, 
                ActualHeight = Math.Max(Math.Max(h, naturalH), m.ActualHeight),
                MaxChildWidth = w, 
                Baseline = m.Baseline,
                MarginTop = mt,
                MarginBottom = mb
            };
            }
            catch (Exception ex)
            {
                 FenLogger.Error($"[CRASH-GUARD] MeasureNode crashed for {node?.Tag ?? "null"}: {ex.Message}", LogCategory.General);
                 return new LayoutMetrics();
            }
        }

        private void ArrangeNode(Node node, SKRect finalRect, int depth)
        {
            if (node == null) return;
            if (depth > 60) return; // Aggressive StackOverflow Guard
            
            if (FenBrowser.Core.Logging.DebugConfig.LogLayoutConstraints && depth < 20)
            {
                 string tag = (node as Element)?.TagName ?? node.NodeType.ToString();
                 var e = node as Element;
                 string cls = e?.GetAttribute("class");
                 // Filter spam
                 if (tag != "Text" && (string.IsNullOrEmpty(cls) || FenBrowser.Core.Logging.DebugConfig.ShouldLog(cls)))
                 {
                     global::FenBrowser.Core.FenLogger.Log($"[LAYOUT-BOX] {new string(' ', depth)}{tag} Rect={finalRect} {(cls != null ? "."+cls : "")}", LogCategory.Layout);
                 }
            }
            


            _ancestorRects[node] = finalRect;
            try
            {
                ArrangeNodeCore(node, finalRect, depth);
            }
            finally
            {
                _ancestorRects.Remove(node);
            }
        }

        private ContainingBlock ResolveContainingBlock(Node node)
    {
        var style = GetStyle(node);
        if (style?.Position?.ToLowerInvariant() == "fixed")
        {
             return new ContainingBlock
            {
                Node = null, Width = _viewportWidth, Height = _viewportHeight, X = 0, Y = 0,
                IsInitial = true, PaddingBox = new SKRect(0, 0, _viewportWidth, _viewportHeight)
            };
        }

        var p = node.Parent;
        int loopCount = 0;
        while (p != null)
        {
            var pStyle = GetStyle(p);
            string pos = pStyle?.Position?.ToLowerInvariant() ?? "static";
            if (pos != "static")
            {
                // _ancestorRects stores the element's BorderBox (passed as finalRect to ArrangeNode).
                // For absolute positioning, we need PaddingBox. 
                // PaddingBox = BorderBox inset by border.
                if (_ancestorRects.TryGetValue(p, out var borderRect))
                {
                    var border = pStyle?.BorderThickness ?? new Thickness(0);
                    float bl = (float)border.Left, bt = (float)border.Top;
                    float br = (float)border.Right, bb = (float)border.Bottom;
                    
                    // PaddingBox is BorderBox inset by border
                    float cbX = borderRect.Left + bl;
                    float cbY = borderRect.Top + bt;
                    float cbW = Math.Max(0, borderRect.Width - bl - br);
                    float cbH = Math.Max(0, borderRect.Height - bt - bb);

                    return new ContainingBlock
                    {
                        Node = p, X = cbX, Y = cbY, Width = cbW, Height = cbH,
                        IsInitial = false, PaddingBox = new SKRect(cbX, cbY, cbX + cbW, cbY + cbH)
                    };
                }
            }
            p = p.Parent;
            
            // Loop Guard
            loopCount++;
            if (loopCount > 1000)
            {
                FenLogger.Error($"[LAYOUT-LOOP] ResolveContainingBlock hit iteration limit for node {node?.Tag}", LogCategory.Layout);
                break;
            }
        }

        return new ContainingBlock
        {
            Node = null, Width = _viewportWidth, Height = _viewportHeight, X = 0, Y = 0,
            IsInitial = true, PaddingBox = new SKRect(0, 0, _viewportWidth, _viewportHeight)
        };
    }
        private void ArrangeNodeCore(Node node, SKRect finalRect, int depth)
        {
            if (node == null) return;
            if (depth > 100) return;

            var style = GetStyle(node);
             if (node is Element eDebug)
            {
                 if (eDebug.TagName == "IMG" || eDebug.TagName == "SVG" || eDebug.TagName.EndsWith(":SVG"))
                 {
                     FenLogger.Debug($"[ARRANGE-CORE] Tag={eDebug.TagName} Id={eDebug.GetAttribute("id")} ShouldHide={ShouldHide(node, style)} Rect={finalRect} StyleDisp={style?.Display}");
                 }
            }


            if (ShouldHide(node, style)) return;

            // Tracking for verification
            if (node is Element && (finalRect.Width <= 0 || finalRect.Height <= 0))
            {
                _zeroSizedCount++;
            }

            string position = style?.Position?.ToLowerInvariant();
            if (position == "absolute" || position == "fixed")
            {
                // Use AbsolutePositionSolver with CORRECT containing block from ancestors
                var containingBlock = ResolveContainingBlock(node);
                
                // Fixed positioning logic is handled by ResolveContainingBlock returning ICB
                
                // Get intrinsic size from measure pass
                float intrinsicWidth = 0, intrinsicHeight = 0;
                if (_desiredSizes.TryGetValue(node, out var desiredSize))
                {
                    intrinsicWidth = desiredSize.Width;
                    intrinsicHeight = desiredSize.Height;
                }
                
                // DEBUG: Log CSS position values
                FenLogger.Debug($"[ABS-POS] node={node.Tag} left={style.Left} right={style.Right} top={style.Top} bottom={style.Bottom} cb=({containingBlock.Width}x{containingBlock.Height})", LogCategory.Rendering);
                
                // Solve the 7-variable equation
                var result = AbsolutePositionSolver.Solve(style, containingBlock, intrinsicWidth, intrinsicHeight);
                
                // DEBUG: Log solver result
                FenLogger.Debug($"[ABS-POS] result: X={result.X} Y={result.Y} W={result.Width} H={result.Height}", LogCategory.Rendering);
                
                finalRect = new SKRect(
                    containingBlock.X + result.X - (float)style.Padding.Left - (float)style.BorderThickness.Left,
                    containingBlock.Y + result.Y - (float)style.Padding.Top - (float)style.BorderThickness.Top,
                    containingBlock.X + result.X + result.Width + (float)style.Padding.Right + (float)style.BorderThickness.Right,
                    containingBlock.Y + result.Y + result.Height + (float)style.Padding.Bottom + (float)style.BorderThickness.Bottom
                );
            }

            var box = new BoxModel { 
                Margin = style?.Margin ?? new Thickness(0),
                Border = style?.BorderThickness ?? new Thickness(0),
                Padding = style?.Padding ?? new Thickness(0),
                BorderBox = finalRect
            };
            
            float bL = (float)box.Border.Left, bT = (float)box.Border.Top, bR = (float)box.Border.Right, bB = (float)box.Border.Bottom;
            float pL = (float)box.Padding.Left, pT = (float)box.Padding.Top, pR = (float)box.Padding.Right, pB = (float)box.Padding.Bottom;
            
            // CRITICAL FIX: Clamp box dimensions to prevent negative sizes when padding/border exceeds available space
            // This fixes FLEX-FAIL and negative dimension SPEC-WARN errors
            float paddingRight = Math.Max(box.BorderBox.Left + bL, box.BorderBox.Right - bR);
            float paddingBottom = Math.Max(box.BorderBox.Top + bT, box.BorderBox.Bottom - bB);
            box.PaddingBox = new SKRect(box.BorderBox.Left + bL, box.BorderBox.Top + bT, paddingRight, paddingBottom);
            
            float contentRight = Math.Max(box.PaddingBox.Left + pL, box.PaddingBox.Right - pR);
            float contentBottom = Math.Max(box.PaddingBox.Top + pT, box.PaddingBox.Bottom - pB);
            box.ContentBox = new SKRect(box.PaddingBox.Left + pL, box.PaddingBox.Top + pT, contentRight, contentBottom);

            if (node.IsText && _textLines.TryGetValue(node, out var computedLines))
            {
                box.Lines = computedLines;
            }

            _boxes[node] = box;

    // DEBUG: Trace test-case and sibling positioning
    if (node is Element tcElem)
    {
        var cls = tcElem.GetAttribute("class");
        if (cls != null && (cls.Contains("test-case") || cls == "label" || cls == "sibling" || cls == "ref"))
        {
            System.Console.WriteLine($"[POSITION] Class={cls} BorderBox={box.BorderBox}");
        }
    }

            string tag = (node as Element)?.TagName?.ToUpperInvariant() ?? "";
            string id = (node as Element)?.GetAttribute("id")?.ToLowerInvariant();
            // REMOVED: isNewTab hack - flexbox now works generically
            string display = style?.Display?.ToLowerInvariant(); 
            
            var elem = node as Element;
            if (display == "flex" || display == "inline-flex") 
                ArrangeFlex(elem, box.ContentBox, depth + 1, style, elem.Children);
            else if (display == "grid")
                ArrangeGrid(elem, box.ContentBox, depth + 1);
            else if (IsMultiColumn(style) && elem != null)
                ArrangeMultiColumn(elem, box.ContentBox);
            else if (tag == "TABLE")
                ComputeTableLayout(elem, box.ContentBox, depth + 1);
            else if (node.IsText) 
                ArrangeText(node, box.ContentBox);
            else 
                ArrangeBlockInternal(elem, box.ContentBox, depth + 1, node);
        }

        private LayoutMetrics MeasureBlockInternal(Element element, SKSize availableSize, int depth, Node fallbackNode)
        {
            // PSEUDO-ELEMENT AWARENESS
            var childrenSource = GetChildrenWithPseudos(element, fallbackNode).ToList();
            if (childrenSource == null) return new LayoutMetrics();

            if (element != null && (element.TagName == "A" || element.GetAttribute("class") == "o3j99"))
            {
                // FenLogger.Debug($"[MEASURE-BLOCK-ENTRY] {element.TagName} id={element.GetAttribute("id")} class={element.GetAttribute("class")}");
            }

            // IFC CHECK
            if (depth < 6 && element != null)
            {
                 FenLogger.Debug($"[MEASURE-BLOCK-ENTRY] Tag={element.TagName} Class={element.GetAttribute("class")} Children={childrenSource.Count()}");
            }

            bool useIFC = false;
            bool hasBlock = false;
            bool hasInline = false;
            
            foreach (var c in childrenSource)
            {
                 if (element != null && element.GetAttribute("class") == "container")
                 {
                      FenLogger.Debug($"[MEASURE-BLOCK-PRE-CHECK] Container Child: {c.NodeType} {(c as Element)?.TagName}");
                 }

                 if (c is Text t && string.IsNullOrWhiteSpace(t.Data)) continue;
                 
                 bool isInline = IsInlineLevel(c);
                 if (isInline) hasInline = true;
                 else hasBlock = true;
            }
            
            // Only use IFC if we have inline content and NO block content
            useIFC = hasInline && !hasBlock;


            if (useIFC && element != null)
            {
                FenLogger.Debug($"[IFC-DECISION] Using Inline Layout for {element.TagName}", LogCategory.Rendering);
                return MeasureInlineContext(element, availableSize, depth);
            }

            string writingMode = GetStyle(element)?.WritingMode ?? "horizontal-tb";
            LogicalSize logicalAvailable = WritingModeConverter.ToLogical(availableSize, writingMode);

            float logicalCurBlock = 0;
            float logicalMaxInline = 0;
            float logicalMaxActualBlockEnd = 0; 
            float lastBlockMargin = 0;
            bool first = true;
            
            // Float tracking (Logical)
            float floatInlineCursor = 0; 
            float currentFloatBlockSize = 0;
            float logicalAvailableInline = logicalAvailable.Inline;

            // FIX: Subtract padding/border from available inline size for children
            // This ensures children are constrained by the parent's padding (e.g. body padding)
            var blockStyle = GetStyle(element ?? fallbackNode as Element);
            if (blockStyle != null)
            {
                 // Reuse ToLogicalMargin since Padding/BorderThickness are also Thickness objects
                 var logPadding = WritingModeConverter.ToLogicalMargin(blockStyle.Padding, writingMode);
                 var logBorder = WritingModeConverter.ToLogicalMargin(blockStyle.BorderThickness, writingMode);
                 
                 float inlineOffset = logPadding.InlineStart + logPadding.InlineEnd + 
                                      logBorder.InlineStart + logBorder.InlineEnd;
                                      
                 if (!float.IsInfinity(logicalAvailableInline))
                 {
                    logicalAvailableInline = Math.Max(0, logicalAvailableInline - inlineOffset);
                 }

                 // FIX: Respect Explicit Width/MaxWidth of the container itself
                 // If the container has max-width: 600px, children must be constrained to 600px, not 1920px.
                 bool isBorderBox = blockStyle.BoxSizing == "border-box";

                 if (blockStyle.Width.HasValue)
                 {
                      float contentW = (float)blockStyle.Width.Value;
                      if (isBorderBox) contentW -= inlineOffset;
                      logicalAvailableInline = contentW;
                 }
                 
                 if (blockStyle.MaxWidth.HasValue)
                 {
                      float contentMax = (float)blockStyle.MaxWidth.Value;
                      if (isBorderBox) contentMax -= inlineOffset;
                      if (!float.IsInfinity(logicalAvailableInline) && contentMax < logicalAvailableInline)
                      {
                          logicalAvailableInline = contentMax;
                      }
                      else if (float.IsInfinity(logicalAvailableInline))
                      {
                          // If available was infinite, but we have max-width, use it!
                          logicalAvailableInline = contentMax;
                      }
                 }
            }

            float internalBlockMarginStart = 0;
            float internalBlockMarginEnd = 0;
            
            var style = GetStyle(element);
            float measurePt = (float)(style?.Padding.Top ?? 0);
            float measureBt = (float)(style?.BorderThickness.Top ?? 0);
            var marginTracker = new MarginCollapseTracker { PreventParentCollapse = measurePt > 0 || measureBt > 0 };

            foreach (var child in childrenSource)
            {
                var childStyle = GetStyle(child);
                if (ShouldHide(child, childStyle)) continue;

                if (child is Text txt && string.IsNullOrWhiteSpace(txt.Data))
                {
                    continue;
                }
                
                // Position check
                string pos = childStyle?.Position?.ToLowerInvariant();
                bool isAbs = pos == "absolute" || pos == "fixed";

                if (isAbs)
                {
                     // Measure absolute elements against physical constraints (or unconstrained)
                     // They are removed from flow, so we don't convert result to logical flow.
                     // We store the 'intrinsic' size for the Arrange phase solver.
                     var absMetrics = MeasureNode(child, availableSize, depth + 1);
                     _desiredSizes[child] = new SKSize(absMetrics.MaxChildWidth, absMetrics.ContentHeight);
                     continue;
                }
                
                LayoutMetrics childMetrics;

                if (childStyle != null && (childStyle.Display == "block" || childStyle.Display == "flex" || childStyle.Display == "grid"))
                {
                    // BLOCK/FLEX/GRID LAYOUT
                    // Margins in Physical
                    float childML = (float)childStyle.Margin.Left;
                    float childMR = (float)childStyle.Margin.Right;
                    float childMT = (float)childStyle.Margin.Top;
                    float childMB = (float)childStyle.Margin.Bottom;
                    
                    // Convert margins to Logical to interact with flow
                    var logicalMargin = WritingModeConverter.ToLogicalMargin(childStyle.Margin, writingMode);
                    
                    float childInlineConstraint = logicalAvailableInline - logicalMargin.InlineSum;
                    if (childInlineConstraint < 0) childInlineConstraint = 0;

                    // Measure Child (Physical Constraint constructed from Logical)
                    var physicalConstraint = WritingModeConverter.ToPhysical(
                        new LogicalSize(childInlineConstraint, logicalAvailable.Block), 
                        writingMode);
                        
                    childMetrics = MeasureNode(child, physicalConstraint, depth + 1);

                    // Child returns Physical Metrics. Convert to Logical.
                    var childPhysicalSize = new SKSize(childMetrics.MaxChildWidth, childMetrics.ContentHeight);
                    var childLogicalSize = WritingModeConverter.ToLogical(childPhysicalSize, writingMode);
                    
                    float fullChildInline = childLogicalSize.Inline + logicalMargin.InlineSum;
                    logicalMaxInline = Math.Max(logicalMaxInline, fullChildInline);
                }
                else
                {
                   // Fallback / Inline-Block
                   childMetrics = MeasureNode(child, availableSize, depth + 1);
                   var childPhysicalSize = new SKSize(childMetrics.MaxChildWidth, childMetrics.ContentHeight);
                   var childLogicalSize = WritingModeConverter.ToLogical(childPhysicalSize, writingMode);
                   
                   logicalMaxInline = Math.Max(logicalMaxInline, childLogicalSize.Inline);
                }

                bool isFloat = childStyle?.Float?.ToLowerInvariant() == "left"; 

                // Determine Child Metrics in Logical Space
                var childPhysSize = new SKSize(childMetrics.MaxChildWidth, childMetrics.ContentHeight);
                var childLogSize = WritingModeConverter.ToLogical(childPhysSize, writingMode);
                var logMargin = WritingModeConverter.ToLogicalMargin(childStyle?.Margin ?? new Thickness(0), writingMode);

                if (isFloat)
                {
                    float mt = logMargin.BlockStart;
                    float mb = logMargin.BlockEnd;
                    float ml = logMargin.InlineStart;
                    float mr = logMargin.InlineEnd;
                    
                    float fullChildInline = childLogSize.Inline + ml + mr;
                    float fullChildBlock = childLogSize.Block + mt + mb;
                    
                    if (floatInlineCursor + fullChildInline > logicalAvailableInline && floatInlineCursor > 0)
                    {
                        logicalCurBlock += currentFloatBlockSize;
                        floatInlineCursor = 0;
                        currentFloatBlockSize = 0;
                    }
                    
                    floatInlineCursor += fullChildInline;
                    currentFloatBlockSize = Math.Max(currentFloatBlockSize, fullChildBlock);
                    
                    logicalMaxInline = Math.Max(logicalMaxInline, floatInlineCursor);
                }
                else
                {
                    // Block Logic
                    if (floatInlineCursor > 0)
                    {
                        logicalCurBlock += currentFloatBlockSize;
                        floatInlineCursor = 0;
                        currentFloatBlockSize = 0;
                        first = true;
                    }

                    logicalMaxInline = Math.Max(logicalMaxInline, childLogSize.Inline);
                    
                    float mt = logMargin.BlockStart;
                    float mb = logMargin.BlockEnd;
                    
                    // Box Model Phase 16.3: Use MarginCollapseTracker
                    bool childIsEmpty = (childLogSize.Block == 0 && childLogSize.Inline == 0 && !isFloat); 
                    
                    
                    float spacing = marginTracker.AddMargin(mt, mb, first, childIsEmpty);
                    
                    if (first) { 
                         first = false;
                    }

                    // Log layout progression
                    // Log layout progression
                    // Log layout progression
                    if (depth < 6 && element != null)
                    {
                         FenLogger.Debug($"[MEASURE-BLOCK-LOOP] Tag={element.TagName} Child={child.NodeType} H={childLogSize.Block} Spacing={spacing} CurBlock={logicalCurBlock}->{logicalCurBlock+spacing+childLogSize.Block}");
                    }
                    
                    logicalCurBlock += spacing;
                    logicalCurBlock += childLogSize.Block;
                }
            }
            
            marginTracker.Finish(out float startMargin, out float endMargin);
            internalBlockMarginStart = startMargin;
            internalBlockMarginEnd = endMargin;
            logicalCurBlock += currentFloatBlockSize; 
            
            // Map Logical results back to LayoutMetrics (Physical interpretation slots)
            // Note: LayoutMetrics fields are named poorly for vertical text, but we map strictly:
            // ContentHeight -> Block Size
            // MaxChildWidth -> Inline Size
            // MarginTop -> BlockStart Margin
            // MarginBottom -> BlockEnd Margin
            
            // If the caller expects Physical, we should arguably convert back. 
            // BUT: Margin collapsing happens recursively. 
            // If parent is also vertical, it expects 'ContentHeight' to be BlockSize (Width).
            // If parent is horizontal, it expects 'ContentHeight' to be Height.
            
            // DECISION: LayoutMetrics is CONTEXT-DEPENDENT. It returns "Size in Flow Axis" and "Size in Cross Axis"?
            // No, existing code uses .ContentHeight as Y-size.
            // If I return BlockSize as ContentHeight for vertical-rl, parent (horizontal) sees it as Height (Y).
            // But BlockSize for vertical-rl IS Width (X)!
            // ERROR: If I don't convert back, I break orthogonal flows.
            
            // FIX: Convert back to PHYSICAL dimensions.
            var finalLogSize = new LogicalSize(logicalMaxInline, logicalCurBlock);
            var finalPhysSize = WritingModeConverter.ToPhysical(finalLogSize, writingMode);
            
            // MarginTop/Bottom are problematic semantics. 
            // If vertical-rl, BlockStart margin is Right.
            // LayoutMetrics has no MarginRight field?
            // Existing ILayoutComputer definition has MarginTop/MarginBottom.
            // It seems the engine ONLY supports vertical margin collapsing.
            // I will map BlockStart/End to MarginTop/Bottom to preserve collapsing behavior *if* the parent understands it.
            // But if parent is horizontal, it will see MarginTop as Top.
            // If child is vertical-rl, its "BlockStart" is Right. 
            // Collapsing Right margin with Parent Top margin is WRONG.
            // Orthogonal flows should NOT collapse margins.
            
            // If orthogonal, we should probably zero out the collapsible margins returned or handle them carefully.
            // For this phase, I will return them as is, assuming homogeneous writing mode for deep trees.
            
            return new LayoutMetrics { 
                ContentHeight = finalPhysSize.Height, 
                ActualHeight = finalPhysSize.Height, 
                MaxChildWidth = finalPhysSize.Width,
                MarginTop = internalBlockMarginStart,
                MarginBottom = internalBlockMarginEnd
            };
        }

        private void ArrangeBlockInternal(Element element, SKRect finalRect, int depth, Node fallbackNode)
        {
            var childrenSource = GetChildrenWithPseudos(element, fallbackNode).ToList();
            if (childrenSource == null) return;

            // DEBUG: Trace BODY content box
            if (element?.TagName == "BODY")
            {
                System.Console.WriteLine($"[BODY-ARRANGE] ContentBox={finalRect}");
                var firstChild = childrenSource.FirstOrDefault(c => c is Element);
                if (firstChild is Element fe)
                {
                    var fStyle = GetStyle(fe);
                    System.Console.WriteLine($"[BODY-FIRST-CHILD] Tag={fe.TagName} Class={fe.GetAttribute("class")} MarginTop={fStyle?.Margin.Top}");
                }
            }
            
            var parentStyle = element != null ? GetStyle(element) : null;

            // BFC Logic: Determine if this block establishes a new BFC
            bool newBfc = false;
            if (element != null && parentStyle != null)
            {
                // Float, Absolute, Inline-Block, Table-Cell, Overflow!=visible
                if ((parentStyle.Float?.ToLowerInvariant() is string f && (f == "left" || f == "right")) ||
                    (parentStyle.Position?.ToLowerInvariant() is string p && (p == "absolute" || p == "fixed")) ||
                    (parentStyle.Display?.ToLowerInvariant() is string d && (d == "inline-block" || d == "table-cell")) ||
                    (parentStyle.Overflow?.ToLowerInvariant() is string o && o != "visible"))
                {
                    newBfc = true;
                }
            }
            
            IDisposable bfcScope = null;
            if (newBfc)
            {
                bfcScope = new BfcScope(this);
            }

            try 
            {

            // IFC CHECK
            if (element != null)
            {
                if (_inlineCache.ContainsKey(element))
                {
                    // FenLogger.Debug($"[IFC-CACHE-HIT] Element={element.TagName} Hash={element.GetHashCode()}", LogCategory.Rendering);
                    ArrangeInlineContext(element, finalRect, depth);
                    return; 
                }
                else
                {
                     // Trace miss
                     // FenLogger.Debug($"[IFC-CACHE-MISS] Element={element.TagName} Hash={element.GetHashCode()} CacheCount={_inlineCache.Count}", LogCategory.Rendering);
                }    
            }    else
                {
                    // Debug why it missed if we expected it
                   // FenLogger.Debug($"[IFC-CACHE-MISS] {element.TagName} falling back to Block Layout", LogCategory.Rendering);
                }
            



            


            // Check for Flexbox
            if (parentStyle != null && (parentStyle.Display == "flex" || parentStyle.Display == "inline-flex"))
            {
                FenLogger.Debug($"[FLEX-DISPATCH] Arranging <{element.TagName}> as FLEX. Direction={parentStyle.FlexDirection}");
                ArrangeFlex(element, finalRect, depth, parentStyle, childrenSource);
                return;
            }
            
            if (element != null) 
            {
                 // Trace block layout usage
                 // FenLogger.Debug($"[BLOCK-LAYOUT] Arranging <{element.TagName} class='{element.GetAttribute("class")}'> {childrenSource.Count()} children");
                 var cls = element.GetAttribute("class");
                 if (cls != null && cls.Contains("gb"))
                 {
                     FenLogger.Debug($"[HEADER-DEBUG] Tag={element.TagName} Class='{cls}' Display='{parentStyle?.Display}' Children={childrenSource.Count()}");
                 }
            }

            string writingMode = parentStyle?.WritingMode ?? "horizontal-tb";

            float pt = (float)(parentStyle?.Padding.Top ?? 0);
            float bt_top = (float)(parentStyle?.BorderThickness.Top ?? 0);
            
            // FIX: finalRect IS ALREADY the ContentBox (padding already subtracted in ArrangeNodeCore).
            // We should NOT subtract padding again here.
            // The contentBox IS finalRect.
            var contentBox = finalRect;
            
            var logicalContentSize = WritingModeConverter.ToLogical(contentBox.Size, writingMode);
            var logicalAvailableInline = logicalContentSize.Inline;
            
            float logicalCurBlock = 0;
            float floatInlineCursor = 0;
            float currentFloatBlockSize = 0;
            
            var marginTracker = new MarginCollapseTracker { PreventParentCollapse = pt > 0 || bt_top > 0 };
            bool first = true;

            foreach (var child in childrenSource)
            {
                var childStyle = GetStyle(child);
                if (ShouldHide(child, childStyle)) continue;

                if (child is Text txt && string.IsNullOrWhiteSpace(txt.Data)) continue;
                
                if (_desiredSizes.TryGetValue(child, out var size))
                {
                    string pos = childStyle?.Position?.ToLowerInvariant();
                    bool isAbs = pos == "absolute" || pos == "fixed";

                    if (isAbs)
                    {
                        // Absolute elements are positioned relative to their containing block.
                        // We delegate the resolution to ArrangeNodeCore which has access to the ancestor stack.
                        // We pass finalRect (parent content box) as a placeholder, but it will be ignored/overwritten.
                        ArrangeNode(child, finalRect, depth + 1);
                        continue;
                    }
                    
                    var childLogSize = WritingModeConverter.ToLogical(size, writingMode);
                    var childLogMargin = WritingModeConverter.ToLogicalMargin(childStyle?.Margin ?? new Thickness(0), writingMode);
                    
                    if (_effectiveMargins.TryGetValue(child, out var eff))
                    {
                        childLogMargin.BlockStart = eff.Top;
                        childLogMargin.BlockEnd = eff.Bottom;
                    }

                    bool isFloat = childStyle?.Float?.ToLowerInvariant() == "left";

                    LogicalRect childLogRect;
                    
                    if (isFloat)
                    {
                        float mt = childLogMargin.BlockStart;
                        float mb = childLogMargin.BlockEnd;
                        float ml = childLogMargin.InlineStart;
                        float mr = childLogMargin.InlineEnd;
                        
                        float fullChildInline = childLogSize.Inline + ml + mr;
                        float fullChildBlock = childLogSize.Block + mt + mb;
                        
                        if (floatInlineCursor + fullChildInline > logicalAvailableInline && floatInlineCursor > 0)
                        {
                            logicalCurBlock += currentFloatBlockSize;
                            floatInlineCursor = 0;
                            currentFloatBlockSize = 0;
                        }
                        
                        childLogRect = new LogicalRect(
                            floatInlineCursor + ml,
                            logicalCurBlock + mt,
                            childLogSize.Inline,
                            childLogSize.Block
                        );
                        
                        floatInlineCursor += fullChildInline;
                        currentFloatBlockSize = Math.Max(currentFloatBlockSize, fullChildBlock);
                        
                        // Register Float Exclusion
                        var childPhysRel = WritingModeConverter.ToPhysicalRect(childLogRect, contentBox.Size, writingMode);
                        var childPhysAbs = new SKRect(
                             childPhysRel.Left + contentBox.Left,
                             childPhysRel.Top + contentBox.Top,
                             childPhysRel.Right + contentBox.Left,
                             childPhysRel.Bottom + contentBox.Top
                        );
                        
                        ArrangeNode(child, childPhysAbs, depth + 1);

                        var exc = FloatExclusion.CreateFromStyle(childPhysAbs, isFloat, childStyle);
                        if (_activeBfcFloats.Count > 0) _activeBfcFloats.Peek().Add(exc);
                    }
                    else
                    {
                        if (floatInlineCursor > 0)
                        {
                            logicalCurBlock += currentFloatBlockSize;
                            floatInlineCursor = 0;
                            currentFloatBlockSize = 0;
                            // Preceding floats don't prevent margin collapse of 'real' blocks usually, 
                            // but for simplified layout we might want to say 'we have content'.
                            // However, strictly, floats are out of flow.
                            // We'll mimic MeasureBlockInternal logic:
                            first = true; 
                        }
                        
                        float mt = childLogMargin.BlockStart;
                        float mb = childLogMargin.BlockEnd;
                        float ml = childLogMargin.InlineStart;
                        
                        bool childIsEmpty = (childLogSize.Block == 0 && childLogSize.Inline == 0 && !isFloat);
                        float spacing = marginTracker.AddMargin(mt, mb, first, childIsEmpty);
                        if (first) first = false;
                        
                        logicalCurBlock += spacing;

                        if (element?.GetAttribute("class") == "container")
                        {
                             FenLogger.Debug($"[ARRANGE-BLOCK-LOOP] Child={childStyle?.Display} BoxH={childLogSize.Block} Spacing={spacing} CurBlock={logicalCurBlock}");
                        }
                        
                        // Implement margin: auto for block-level elements
                        float autoMarginOffset = 0;
                        if (!isFloat && (childStyle?.Display == "block" || childStyle?.Display == "table" || childStyle?.Display == "flex"))
                        {
                            bool leftAuto = childStyle?.MarginLeftAuto == true;
                            bool rightAuto = childStyle?.MarginRightAuto == true;
                            
                            if (leftAuto || rightAuto)
                            {
                                float freeSpace = logicalAvailableInline - childLogSize.Inline;
                                if (freeSpace > 0)
                                {
                                    if (leftAuto && rightAuto) autoMarginOffset = freeSpace / 2;
                                    else if (leftAuto) autoMarginOffset = freeSpace;
                                }
                            }
                        }

                        childLogRect = new LogicalRect(
                            ml + autoMarginOffset,
                            logicalCurBlock,
                            childLogSize.Inline,
                            childLogSize.Block
                        );



                        
                        var childPhysRel = WritingModeConverter.ToPhysicalRect(childLogRect, contentBox.Size, writingMode);
                        


                        var childPhysAbs = new SKRect(
                            childPhysRel.Left + contentBox.Left,
                            childPhysRel.Top + contentBox.Top,
                            childPhysRel.Right + contentBox.Left,
                            childPhysRel.Bottom + contentBox.Top
                        );
                        
                        ArrangeNode(child, childPhysAbs, depth + 1);
                        
                        if (element?.GetAttribute("class") == "container")
                        {
                             FenLogger.Debug($"[ARRANGE-BLOCK-LOOP-END] CurBlock={logicalCurBlock}->{logicalCurBlock+childLogSize.Block} ChildH={childLogSize.Block}");
                        }
                        logicalCurBlock += childLogSize.Block;
                    }
                }
            }
            // End










                    

            }
            finally
            {
                bfcScope?.Dispose();
            }
        }
        
        private LayoutMetrics MeasureFlexInternal(Element element, SKSize availableSize, bool isCenteredRoot, int depth, Node fallbackNode = null)
        {
            var target = element ?? fallbackNode as Element; // Ensure Element
            if (target == null) return new LayoutMetrics();
            
            // V8 DIAGNOSTICS
            string dbgCls = target.GetAttribute("class") ?? "";
            if (target.TagName == "DIV" && dbgCls.Contains("sites-grid"))
            {
                 FenLogger.Debug($"[FLEX-ENTRY-V8] Measuring .sites-grid Children={target.Children.Count} Avail={availableSize.Width}x{availableSize.Height}", LogCategory.Rendering);
                 foreach(var c in target.Children) {
                    var cEl = c as Element;
                    FenLogger.Debug($"  - Child <{cEl?.TagName} class='{cEl?.GetAttribute("class")}'>", LogCategory.Rendering);
                 }
            }

            return CssFlexLayout.Measure(
                target, 
                availableSize, 
                (n, s, d, shrink) => MeasureNode(n, s, d, shrink), 
                GetStyle, 
                ShouldHide, 
                depth,
                GetChildrenWithPseudos(target, fallbackNode));
        }



        private LayoutMetrics MeasureButton(Element element, SKSize availableSize, int depth)
        {
            // Buttons should use shrink-to-fit sizing (like inline-blocks)
            // Measure with unconstrained width to get intrinsic content size
            var shrinkConstraint = new SKSize(float.PositiveInfinity, availableSize.Height);
            var m = MeasureBlockInternal(element, shrinkConstraint, depth, element);
            
            // Ensure buttons have reasonable minimum dimensions
            if (m.MaxChildWidth < 10)
            {
                // Try to get text content width
                string textContent = element.TextContent?.Trim() ?? "";
                if (!string.IsNullOrEmpty(textContent))
                {
                    m.MaxChildWidth = Math.Max(m.MaxChildWidth, textContent.Length * 8f + 20); // Rough estimate
                }
                else
                {
                    m.MaxChildWidth = Math.Max(m.MaxChildWidth, 60); // Default min button width
                }
            }
            
            // Cap button width to available space if specified
            if (!float.IsInfinity(availableSize.Width) && m.MaxChildWidth > availableSize.Width)
            {
                m.MaxChildWidth = availableSize.Width;
            }
            
            // Ensure minimum height for buttons
            if (m.ContentHeight < 20)
            {
                m.ContentHeight = 36; // Typical button height
                m.ActualHeight = 36;
            }
            
            return m;
        }

        private void ArrangeFlexInternal(Element element, SKRect finalRect, bool isCenteredRoot, int depth, Node fallbackNode = null)
        {
            var target = element ?? fallbackNode as Element;
            if (target == null) return;
            

            
            CssFlexLayout.Arrange(
                target, 
                finalRect, 
                (n, r, d) => ArrangeNode(n, r, d), 
                GetStyle, 
                (n) => _desiredSizes.TryGetValue(n, out var s) ? s : SKSize.Empty,
                ShouldHide, 
                depth,
                GetChildrenWithPseudos(target, fallbackNode));
        }


        // --- INLINE FORMATTING CONTEXT ---

        internal bool IsInlineLevelInternal(Node node) => IsInlineLevel(node);
        private bool IsInlineLevel(Node node)
        {
            if (node is Text) return true;
            var style = GetStyle(node);
            string d = style?.Display?.ToLowerInvariant();
            
            // FIX: If display is explicitly set, use it. Do not fall back to Tag check.
            if (!string.IsNullOrEmpty(d))
            {
                return d == "inline" || d == "inline-block" || d == "inline-flex" || d == "inline-grid" || d == "inline-table";
            }
            
            // Fallback for default display
            string tag = (node as Element)?.TagName?.ToUpperInvariant() ?? "";
            return tag == "SPAN" || tag == "A" || tag == "B" || tag == "I" || tag == "IMG" || tag == "SVG" || tag == "INPUT" || tag == "BUTTON" || tag == "CANVAS" || tag == "SMALL" || tag == "CODE" || 
                   tag == "STRONG" || tag == "EM" || tag == "LABEL" || tag == "BR" || tag == "PICTURE" || tag == "TIME" || tag == "MARK" || tag == "KBD" || tag == "ABBR" || tag == "Q" || 
                   tag == "CITE" || tag == "SUB" || tag == "SUP" || tag == "DFN" || tag == "IFRAME";
        }

        private LayoutMetrics MeasureInput(Element element, SKSize availableSize, int depth)
        {
             if (depth > 80) return new LayoutMetrics();
             
             // Get input type
             string inputType = element.GetAttribute("type")?.ToLowerInvariant() ?? "text";
             
             // Inspect style
             var style = GetStyle(element);
             float w = 150;
             float h = 20;
             
             // Type-specific default sizes
             switch (inputType)
             {
                 case "checkbox":
                     w = 13; h = 13;
                     break;
                 case "radio":
                     w = 13; h = 13;
                     break;
                 case "color":
                     w = 44; h = 23;
                     break;
                 case "range":
                     w = 129; h = 21;
                     break;
                 case "submit":
                 case "button":
                 case "reset":
                     w = 80; h = 25;
                     LayoutHelper.MeasureInputButtonText(element, style, ref w, ref h);
                     break;
                 case "date":
                 case "datetime-local":
                     w = 200; h = 25; // Date picker with calendar icon
                     break;
                 case "time":
                     w = 120; h = 25; // Time picker
                     break;
                 case "number":
                     w = 100; h = 25; // Number with spinbox
                     break;
                 case "file":
                     w = 250; h = 25; // File input with button
                     break;
                 default:
                     // text, password, email, etc. - standard sizing
                     w = 150; h = 20;
                     break;
             }
             
             if (element.TagName == "TEXTAREA") {
                 h = 40;
                 w = 300;
             }
             
             // Override with explicit CSS if specified
             if (style != null)
             {
                 if (style.Width.HasValue) w = (float)style.Width.Value;
                 if (style.Height.HasValue) h = (float)style.Height.Value;
             }
             
             return new LayoutMetrics { ContentHeight = h, MaxChildWidth = w };
        }
        
        private LayoutMetrics MeasureVideo(Element element, SKSize availableSize)
        {
            // Default video size: 300x150 (HTML5 spec)
            var style = GetStyle(element);
            float w = 300;
            float h = 150;
            
            // Check for explicit width/height attributes
            string widthAttr = element.GetAttribute("width");
            string heightAttr = element.GetAttribute("height");
            
            if (!string.IsNullOrEmpty(widthAttr) && float.TryParse(widthAttr, out float aw)) w = aw;
            if (!string.IsNullOrEmpty(heightAttr) && float.TryParse(heightAttr, out float ah)) h = ah;
            
            // Override with CSS if specified
            if (style != null)
            {
                if (style.Width.HasValue) w = (float)style.Width.Value;
                if (style.Height.HasValue) h = (float)style.Height.Value;
            }
            
            return new LayoutMetrics { ContentHeight = h, MaxChildWidth = w };
        }
        
        private LayoutMetrics MeasureAudio(Element element, SKSize availableSize)
        {
            // Default audio controls size: 300x32
            var style = GetStyle(element);
            float w = 300;
            float h = 32;
            
            // If no controls attribute, audio is invisible
            if (!element.HasAttribute("controls"))
            {
                return new LayoutMetrics { ContentHeight = 0, MaxChildWidth = 0 };
            }
            
            // Override with CSS if specified
            if (style != null)
            {
                if (style.Width.HasValue) w = (float)style.Width.Value;
                if (style.Height.HasValue) h = (float)style.Height.Value;
            }
            
            return new LayoutMetrics { ContentHeight = h, MaxChildWidth = w };
        }
        
        private LayoutMetrics MeasureIframe(Element element, SKSize availableSize)
        {
            // Default iframe size: 300x150 (HTML5 spec)
            var style = GetStyle(element);
            float w = 300;
            float h = 150;
            
            // Check for explicit width/height attributes
            string widthAttr = element.GetAttribute("width");
            string heightAttr = element.GetAttribute("height");
            
            if (!string.IsNullOrEmpty(widthAttr) && float.TryParse(widthAttr, out float aw)) w = aw;
            if (!string.IsNullOrEmpty(heightAttr) && float.TryParse(heightAttr, out float ah)) h = ah;
            
            // Override with CSS if specified
            if (style != null)
            {
                if (style.Width.HasValue) w = (float)style.Width.Value;
                if (style.Height.HasValue) h = (float)style.Height.Value;
            }
            
            return new LayoutMetrics { ContentHeight = h, MaxChildWidth = w };
        }
        
        private LayoutMetrics MeasureProgress(Element element, SKSize availableSize)
        {
            // Default progress bar size: 150x15
            var style = GetStyle(element);
            float w = 150;
            float h = 15;
            
            if (style != null)
            {
                if (style.Width.HasValue) w = (float)style.Width.Value;
                if (style.Height.HasValue) h = (float)style.Height.Value;
            }
            
            return new LayoutMetrics { ContentHeight = h, MaxChildWidth = w };
        }
        
        private LayoutMetrics MeasureMeter(Element element, SKSize availableSize)
        {
            // Default meter size: 80x15
            var style = GetStyle(element);
            float w = 80;
            float h = 15;
            
            if (style != null)
            {
                if (style.Width.HasValue) w = (float)style.Width.Value;
                if (style.Height.HasValue) h = (float)style.Height.Value;
            }
            
            return new LayoutMetrics { ContentHeight = h, MaxChildWidth = w };
        }
        
        private LayoutMetrics MeasureDetails(Element element, SKSize availableSize, int depth)
        {
            // Details element: only show summary when closed, all children when open
            bool isOpen = element.HasAttribute("open");
            float totalHeight = 0;
            float maxWidth = 0;
            
            if (element.Children != null)
            {
                foreach (var child in element.Children)
                {
                    if (child is Element childEl)
                    {
                        bool isSummary = childEl.TagName?.ToUpperInvariant() == "SUMMARY";
                        
                        // Always show summary, only show other children if open
                        if (isSummary || isOpen)
                        {
                            var childMetrics = MeasureNode(child, availableSize, depth);
                            totalHeight += childMetrics.ContentHeight;
                            maxWidth = Math.Max(maxWidth, childMetrics.MaxChildWidth);
                        }
                    }
                }
            }
            
            return new LayoutMetrics { ContentHeight = totalHeight, MaxChildWidth = maxWidth };
        }

        private LayoutMetrics MeasureInlineContext(Element container, SKSize availableSize, int depth)
        {
            if (container.TagName == "A" || container.GetAttribute("class") == "o3j99")
            {
               // FenLogger.Debug($"[MEASURE-INLINE-ENTRY] {container.TagName}");
            }

            var style = GetStyle(container);
            float constrainedW = availableSize.Width;
            float constrainedH = availableSize.Height;

            if (style != null)
            {
                if (style.Width.HasValue) constrainedW = (float)style.Width.Value;
                if (style.Height.HasValue) constrainedH = (float)style.Height.Value;
                // If vertical-rl, and height is not set but max-height is, use that?
                // For now, explicit height is critical for the test case.
            }

            var result = InlineLayoutComputer.Compute(
                container, 
                new SKSize(constrainedW, constrainedH), 
                GetStyle, 
                (elem, sz, d) => {
                    var m = MeasureNode(elem, sz, d);
                    _desiredSizes[elem] = new SKSize(m.MaxChildWidth, m.ContentHeight);
                    // FenLogger.Debug($"[FLEX-CACHE] Stored {elem.TagName} Size={m.MaxChildWidth}x{m.ContentHeight}");
                    return m;
                },
                depth,
                (_activeBfcFloats.Count > 0 ? _activeBfcFloats.Peek() : null)
            );
            
            if (container.TagName == "A" || container.GetAttribute("class") == "o3j99")
            {
               // FenLogger.Debug($"[MEASURE-INLINE-DONE] {container.TagName} TextNodes={result.TextLines.Count} Height={result.Metrics.ContentHeight}");
            }

            // Cache everything!
            _inlineCache[container] = result;
             // /* [PERF-REMOVED] */
            FenLogger.Debug($"[IFC-CACHE-SET] Container={container.TagName} Id={container.GetAttribute("id")} Class={container.GetAttribute("class")} Lines={result.TextLines.Count} Rects={result.ElementRects.Count}", LogCategory.Rendering);
            
            // Merge text lines into global text cache locally so we don't need to pass InlineLayoutResult around
            foreach (var kvp in result.TextLines)
            {
                _textLines[kvp.Key] = kvp.Value;
            }

            return result.Metrics;
        }

        private void ArrangeInlineContext(Element container, SKRect finalRect, int depth)
        {
            if (!_inlineCache.TryGetValue(container, out var result)) return;

            // CRITICAL FIX: If finalRect.Width differs from what was used during Measure,
            // re-run inline layout with the actual width to get correct text-align positioning.
            // This is especially important for table cells measured with infinity width.
            float measureWidth = result.Metrics.MaxChildWidth; // The width used during measure
            float arrangeWidth = finalRect.Width;
            
            if (arrangeWidth > 0 && !float.IsInfinity(arrangeWidth) && Math.Abs(arrangeWidth - measureWidth) > 1)
            {
                // Re-run inline layout with actual width for proper text-align
                // Also pass current Height constraint (or positive infinity if unconstrained)
                // If vertical-rl, arrangeWidth is the Physical Width (Logical Height).
                // We need to preserve the Logical Width (Physical Height) constraint too.
                var newResult = InlineLayoutComputer.Compute(
                    container,
                    new SKSize(arrangeWidth, finalRect.Height),
                    GetStyle,
                    (elem, sz, d) => MeasureNode(elem, sz, d),
                    depth
                );
                _inlineCache[container] = newResult;
                result = newResult;
                
                // Also update _textLines with new positions
                foreach (var kvp in result.TextLines)
                {
                    _textLines[kvp.Key] = kvp.Value;
                }
            }

            // Arrange Atomic Elements
            foreach (var kvp in result.ElementRects)
            {
                var child = kvp.Key;
                var relativeRect = kvp.Value;
                
                // Offset by container position
                var finalChildRect = new SKRect(
                    finalRect.Left + relativeRect.Left,
                    finalRect.Top + relativeRect.Top,
                    finalRect.Left + relativeRect.Right,
                    finalRect.Top + relativeRect.Bottom
                );
                
                if ((child as Element)?.TagName == "IMG") 
                {
                     FenLogger.Debug($"[ARRANGE-INLINE-IMG] Container={container.TagName} Child=IMG Rect={finalChildRect}");
                }

                ArrangeNode(child, finalChildRect, depth + 1); // Recursively arrange content of inline-blocks
            }
            // Text lines are already in _textLines, which ArrangeNode(Text) will pick up automatically
            // But wait, ArrangeNode is called for CHILDREN.
            // We need to iterate children and call ArrangeNode on them.
            
            // The issue: InlineLayoutComputer recurses logical children.
            // We need to ensure ArrangeNode is called for all those children so they get their Boxes created.
            
            // Simple approach: Iterate children of container. If found in ElementRects, arrange. 
            // If Text, call ArrangeText (which does nothing but is good for consistency).
            
            void ProcessChildren(Node n, int currentDepth) {
                if (currentDepth > 80) return;
                if (n is Element e && e.Children != null) {
                    foreach(var c in e.Children) {
                        if (result.ElementRects.TryGetValue(c, out var r)) {
                           // It's an atomic element we placed
                           var absR = new SKRect(finalRect.Left + r.Left, finalRect.Top + r.Top, finalRect.Left + r.Right, finalRect.Top + r.Bottom);
                           ArrangeNode(c, absR, currentDepth + 1);
                        } else if (c is Text) {
                           // Text lines are relative to result.Origin (0,0). 
                           // In BoxModel, lines are relative to ContentBox.
                           // So if we just passed the lines to _textLines, they are relative to (0,0) of the IFC container?
                           
                           // Wait: ComputedTextLine.Origin IS relative to ContentBox in the definition.
                           // InlineLayoutComputer produced coords relative to the IFC start.
                           // So if 'c' is a direct child of 'container', its ContentBox IS the IFC container?
                           // No. Text nodes don't have boxes. They are just content.
                           // ArrangeNode for Text creates a BoxModel with ContentBox = finalRect.
                           
                           // Let's call ArrangeNode with the full container rect?
                           // Or dummy rect?
                           // Text paint node uses box.ContentBox.Left + line.Origin.X.
                           // If line.Origin is (10, 10) relative to IFC start.
                           // And box.ContentBox is the IFC container absolute rect.
                           // Then it works!
                           ArrangeNode(c, finalRect, currentDepth + 1);
                        } else {
                           // Nested inline (span, etc).
                           // Ensure the inline container itself gets a box so PaintTreeBuilder can traverse it.
                           // We use the container's rect as a placeholder box.
                           ArrangeNode(c, finalRect, currentDepth + 1);
                           
                           // Recursively visit children to ensure they also get processed (if they are atomic or text)
                           ProcessChildren(c, currentDepth + 1);
                        }
                    }
                }
            }
            ProcessChildren(container, depth);
        }

        // ------------------------------------


        // Adapter methods for ILayoutComputer interface
        public LayoutMetrics MeasureFlex(Element element, SKSize s, int depth) 
        {
             return MeasureFlexInternal(element, s, false, depth);
        }

        public void ArrangeFlex(Element element, SKRect r, int depth) 
        {
             var style = GetStyle(element);
             var children = element.Children;
             ArrangeFlex(element, r, depth, style, children);
        }
        
        public LayoutMetrics MeasureBlock(Element element, SKSize s, int depth) 
        {
            // Detect Inline Formatting Context
            // If any child is inline, we should use Inline Layout.
            // (Naive check: if first child is inline/text, use IFC. Else BFC.)
            // Real browser: anonymous block boxes. For now: All-or-Nothing.
            
            bool useIFC = false;
            if (element != null && element.Children != null)
            {
                foreach (var c in element.Children)
                {
                    if (IsInlineLevel(c) && !(c is Text t && string.IsNullOrWhiteSpace(t.Data))) 
                    {
                        useIFC = true; 
                        break; 
                    }
                }
            }
            if (element != null && element.GetAttribute("class") == "center-text")
            {
                FenLogger.Debug($"[IFC-DECISION] Element {element.TagName} .center-text -> UseIFC: {useIFC}", LogCategory.Rendering);
            }
            if (useIFC) {
                /* [PERF-REMOVED] */
                return MeasureInlineContext(element, s, depth);
            }
            else {
                /* [PERF-REMOVED] */
                return MeasureBlockInternal(element, s, depth, element);
            }
        }

        public void ArrangeBlock(Element element, SKRect r, int depth) 
        {
            if (element != null && _inlineCache.ContainsKey(element))
                ArrangeInlineContext(element, r, depth);
            else
                ArrangeBlockInternal(element, r, depth, element);
        }

        public void ArrangeText(Node node, SKRect finalRect) { }

        public LayoutMetrics MeasureText(Node node, SKSize availableSize)
        {
            // DELEGATE TO NEW TEXT LAYOUT ENGINE
            var style = GetStyle(node);
            
            // Note: Box model isn't available yet during measure, so we store result in a side-channel (_textLines)
            // that Arrange will read later.
            var result = TextLayoutComputer.ComputeTextLayout(node, style, availableSize, _viewportWidth);
            
            if (_textLines.ContainsKey(node))
            {
                 // Overwrite if duplicate measure
                _textLines[node] = result.Lines;
            }
            else
            {
                _textLines.Add(node, result.Lines);
            }
            
            return result.Metrics;
        }

        private LayoutMetrics MeasureImage(Element elem, SKSize availableSize)
        {
            float w = 0, h = 0;
            var style = GetStyle(elem);

            // 1. PRIORITIZE CSS DIMENSIONS
            // If CSS defines width/height, we MUST use it, regardless of the bitmap.
            bool hasCssW = false;
            bool hasCssH = false;

            if (style != null)
            {
                if (style.Width.HasValue) 
                { 
                    w = (float)style.Width.Value; 
                    hasCssW = true; 
                }
                else if (style.WidthPercent.HasValue)
                {
                    // Calculate from available size if possible
                    if (!float.IsInfinity(availableSize.Width))
                    {
                        w = (float)style.WidthPercent.Value / 100f * availableSize.Width;
                        hasCssW = true;
                    }
                }

                if (style.Height.HasValue) 
                { 
                    h = (float)style.Height.Value; 
                    hasCssH = true; 
                }
                // Note: Height percent is harder without explicit parent height, ignoring for now
            }

            // 2. Try HTML attributes if CSS is missing
            if ((!hasCssW || !hasCssH) && elem != null)
            {
                string widthAttr = elem.GetAttribute("width");
                string heightAttr = elem.GetAttribute("height");
                
                if (!hasCssW && !string.IsNullOrEmpty(widthAttr) && float.TryParse(widthAttr.Replace("px", ""), out float attrW))
                    w = attrW;
                
                if (!hasCssH && !string.IsNullOrEmpty(heightAttr) && float.TryParse(heightAttr.Replace("px", ""), out float attrH))
                    h = attrH;
            }

            // 3. Try Bitmap / SVG logic
            // Only strictly needed if we are missing dimensions, OR if we need to preserve aspect ratio.
            // For now, if we have explicit CSS W+H, we might skip this or just use it for rendering later.
            // But usually we want to know the intrinsic ratio if one dim is missing.
            
            float intW = 0, intH = 0;
            
            // Special handling for Inline SVG
            if (elem != null && elem.TagName == "SVG")
            {
                // SVGs default to 300x150 in browsers if no size is specified
                intW = 300;
                intH = 150;
                
                string viewBox = elem.GetAttribute("viewBox") ?? elem.GetAttribute("viewbox");
                if (!string.IsNullOrEmpty(viewBox))
                {
                    var parts = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    // viewBox="min-x min-y width height"
                    if (parts.Length == 4)
                    {
                        if (float.TryParse(parts[2], out float vbW) && float.TryParse(parts[3], out float vbH))
                        {
                            // Use abs() as some viewBox have negative coordinates for translation
                            intW = Math.Abs(vbW);
                            intH = Math.Abs(vbH);
                            FenLogger.Debug($"[SVG-VIEWBOX] Parsed viewBox '{viewBox}' => {intW}x{intH}");
                        }
                    }
                }
                
                // Apply width/height HTML attributes for SVGs if viewBox didn't yield good sizes
                if ((intW <= 0 || intH <= 0) || (intW > 900 || intH > 900))
                {
                    // Look for explicit width/height attributes which often contain smaller display size
                    string svgW = elem.GetAttribute("width");
                    string svgH = elem.GetAttribute("height");
                    
                    if (!string.IsNullOrEmpty(svgW) && float.TryParse(svgW.Replace("px", ""), out float aw))
                        intW = aw;
                    if (!string.IsNullOrEmpty(svgH) && float.TryParse(svgH.Replace("px", ""), out float ah))
                        intH = ah;
                }
                
                // Default for small icons if still no size
                if (intW <= 0) intW = 24;
                if (intH <= 0) intH = 24;
            }
            else if (elem != null)
            {
                string src = elem.GetAttribute("src");
                if (!string.IsNullOrEmpty(src))
                {
                    // Resolve Relative URLs
                    if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase) && 
                        !src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && 
                        !string.IsNullOrEmpty(_baseUri))
                    {
                        try 
                        {
                            if (src.StartsWith("//"))
                            {
                                var scheme = new Uri(_baseUri).Scheme;
                                src = scheme + ":" + src;
                            }
                            else if (src.StartsWith("/"))
                            {
                                var uri = new Uri(_baseUri);
                                src = $"{uri.Scheme}://{uri.Host}{src}";
                            }
                            else
                            {
                                var uri = new Uri(_baseUri);
                                src = $"{uri.Scheme}://{uri.Host}/{src}"; // Simplified
                            }
                        }
                        catch {}
                    }

                    var bitmap = Rendering.ImageLoader.GetImage(src);
                    if (bitmap != null)
                    {
                        intW = bitmap.Width;
                        intH = bitmap.Height;
                        // Console.WriteLine($"[MeasureImage] Loaded {src} ({intW}x{intH})");
                    }
                    else
                    {
                        // Placeholder / Loading state
                        // If we have no dimensions yet, we assume a standard placeholder size
                        // so the layout doesn't collapse to 0.
                        intW = 24; 
                        intH = 24;
                    }
                }
                
                // Parse SVG viewBox for inline SVGs (no src attribute)
                if (intW <= 0 && intH <= 0 && elem.TagName.Equals("SVG", StringComparison.OrdinalIgnoreCase))
                {
                    string viewBox = elem.GetAttribute("viewBox") ?? elem.GetAttribute("viewbox");
                    if (!string.IsNullOrEmpty(viewBox))
                    {
                        // viewBox format: "minX minY width height"
                        var parts = viewBox.Split(new[] {' ', ','}, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4)
                        {
                            if (float.TryParse(parts[2], out float vbW) && float.TryParse(parts[3], out float vbH))
                            {
                                intW = vbW;
                                intH = vbH;
                                FenLogger.Debug($"[SVG-VIEWBOX] Parsed viewBox '{viewBox}' => {intW}x{intH}");
                            }
                        }
                    }
                    
                    // Fallback for small SVGs without viewBox
                    if (intW <= 0 || intH <= 0)
                    {
                        intW = 24;
                        intH = 24;
                    }
                }
            }

            // 4. Apply Intrinsic Dimensions / Aspect Ratio
            if (w <= 0 && h <= 0)
            {
                // No CSS, No Attr. Use intrinsic.
                if (intW > 0 && intH > 0) { w = intW; h = intH; }
                else { w = 300; h = 150; } // HTML default for replaced elements
            }
            else if (w > 0 && h <= 0)
            {
                // Have W, miss H. Maintain ratio.
                if (intW > 0 && intH > 0) h = w * (intH / intW);
                else h = w * 0.5f; // Random guess if no intrinsic
            }
            else if (h > 0 && w <= 0)
            {
                 // Have H, miss W. Maintain ratio.
                 if (intW > 0 && intH > 0) w = h * (intW / intH);
                 else w = h * 2.0f;
            }

            // 5. Constrain
            if (w > availableSize.Width)
            {
                float ratio = w > 0 ? availableSize.Width / w : 1;
                w = availableSize.Width;
                h *= ratio;
            }

            // Ensure non-zero for visibility
            if (w < 1) w = 1;
            if (h < 1) h = 1;

            return new LayoutMetrics { ContentHeight = h, ActualHeight = h, MaxChildWidth = w };
        }

        private bool ShouldHide(Node node, CssComputed style)
        {
            if (node == null) return true;
            
            // CSS display:none always takes precedence (removes from layout tree entirely)
            // Note: visibility:hidden elements MUST still generate boxes and take up space, 
            // so we do NOT check visibility here. That is handled in PaintTreeBuilder.
            if (style?.Display == "none") 
            {
                // (V8 Removed)
                
                // DIAGNOSTIC: Log elements hidden via CSS
                if (node is Element el)
                {
                    string tag = el.TagName.ToLowerInvariant();
                    if (tag == "a" || tag == "img" || tag == "video" || tag == "svg" || tag == "footer" || tag == "header" || (el.GetAttribute("class")?.Contains("gb") ?? false))
                    {
                        /* [PERF-REMOVED] */
                    }
                }
                return true;
            }
            
            // Check element-specific hiding rules
        if (node is Element e)
        {
            string tag = e.TagName.ToLowerInvariant();
            
            // Hide elements used for screen-reader text or hidden by common UI utilities
            string cls = e.GetAttribute("class") ?? "";
            if (cls.Contains("sr-only") || cls.Contains("visually-hidden") || cls.Contains("v-hidden") || 
                cls.Contains("vH") || cls.Contains("inv") || cls.Contains("skip-to-content") || 
                cls.Contains("AppHeader-skipLink") || cls.Contains("show-on-focus") || cls.Contains("p-none-sr")) return true;
            
            // Handle aria-hidden
            if (e.GetAttribute("aria-hidden") == "true") return true;

            // Never hide html/body
            if (tag == "html" || tag == "body") return false;
                
                // Explicitly hide metadata/invisible tags
                if (tag == "head" || tag == "script" || tag == "style" || tag == "template" || tag == "link" || tag == "meta" || tag == "title" || tag == "noscript")
                    return true;
                
                // Hide hidden inputs
                if (tag == "input")
                {
                    string inputType = e.GetAttribute("type")?.ToLowerInvariant();
                    if (inputType == "hidden") return true;
                    
                    // DIAGNOSTIC: Log file inputs to debug visibility
                    if (inputType == "file")
                    {
                        string mapDisplay = style?.Map?.TryGetValue("display", out var d) == true ? d : "not-in-map";
                        /* [PERF-REMOVED] */
                    }
                }
                
                // Replaced elements (inputs, images, etc) are generally visible
                bool isReplaced = tag == "input" || tag == "img" || tag == "button" || tag == "hr" || tag == "svg" || tag == "iframe" || tag == "canvas" || tag == "video";
                if (isReplaced) return false;
                
                // Check for content or explicit styling
                bool hasContent = false;
                if (e.Children != null) 
                { 
                    foreach (var c in e.Children) 
                    { 
                        if (c is Element) { hasContent = true; break; } 
                        if (c is Text t && !string.IsNullOrWhiteSpace(t.Data)) { hasContent = true; break; } 
                    } 
                }
                
                bool hasExplicitSize = style != null && (style.Width.HasValue || style.Height.HasValue || style.MinHeight.HasValue || !string.IsNullOrEmpty(style.WidthExpression));
                bool hasVisuals = style != null && ((style.BackgroundColor.HasValue && style.BackgroundColor.Value.Alpha > 0) || style.BorderThickness.Top > 0 || style.BorderThickness.Bottom > 0 || style.BorderThickness.Left > 0 || style.BorderThickness.Right > 0);
                
                if (!hasContent && !hasExplicitSize && !hasVisuals) 
                {
                    // Never hide container-like spans/divs that might be flex/grid items with future content
                    if (tag == "div" || tag == "span" || tag == "section" || tag == "article" || tag == "nav" || tag == "header" || tag == "footer")
                        return false;

                    return true;
                }
            } 
            else if (node is Text t) 
            { 
                if (string.IsNullOrWhiteSpace(t.Data)) return true; 
            }
            else if (!(node is Element))
            {
                return true; // Hide comments, documents, etc.
            }
            
            return false;
        }

        // REMOVED: Old CssGridLayout-based MeasureGrid and ArrangeGrid stubs
        // Now using GridLayoutComputer at end of class

        public void ComputeTableLayout(Element element, SKRect contentRect, int depth)
        {
            if (_tableStates.TryGetValue(element, out var state))
            {
                TableLayoutComputer.Arrange(element, contentRect, state, _styles, _boxes, ArrangeNode, depth);
            }
        }
        public LayoutMetrics ComputeAbsoluteLayout(Element e, BoxModel b, float x, float y, float w, float h, int d)
        {
            if (e == null) return new LayoutMetrics();
            
            var style = GetStyle(e);
            if (style == null) return new LayoutMetrics();
            
            // Create containing block from parent dimensions
            var cb = new ContainingBlock
            {
                Node = e.Parent,
                Width = w,
                Height = h,
                X = x,
                Y = y,
                IsInitial = false,
                PaddingBox = new SKRect(x, y, x + w, y + h)
            };
            
            // Solve using AbsolutePositionSolver
            var result = AbsolutePositionSolver.Solve(style, cb);
            
            // Store the resolved box
            var box = new BoxModel
            {
                Margin = style.Margin,
                Border = style.BorderThickness,
                Padding = style.Padding,
                BorderBox = new SKRect(
                    x + result.X,
                    y + result.Y,
                    x + result.X + result.Width + (float)(style.Padding.Left + style.Padding.Right + style.BorderThickness.Left + style.BorderThickness.Right),
                    y + result.Y + result.Height + (float)(style.Padding.Top + style.Padding.Bottom + style.BorderThickness.Top + style.BorderThickness.Bottom)
                )
            };
            
            _boxes[e] = box;
            
            return new LayoutMetrics
            {
                MaxChildWidth = result.Width,
                ContentHeight = result.Height
            };
        }
        public float ComputeInlineContext(Element e, BoxModel b, float x, float y, float w, float h, int d) 
        {
             // Deprecated legacy hook, but let's map it if needed or leave stub.
             return 0;
        }
        public void DumpLayoutTree(Node root)
        {
            FenLogger.Info("[MinimalLayoutComputer] --- Layout Tree Dump ---", LogCategory.Rendering);
            DumpNode(root, 0);
            FenLogger.Info("[MinimalLayoutComputer] --- End Layout Tree Dump ---", LogCategory.Rendering);
        }

        private void DumpNode(Node node, int depth)
        {
            if (node == null) return;
            string indent = new string(' ', depth * 2);
            var box = GetBox(node);
            string boxInfo = box != null ? $" [{box.BorderBox.Left:F1},{box.BorderBox.Top:F1} {box.BorderBox.Width:F1}x{box.BorderBox.Height:F1}]" : " [No Box]";
            
            FenLogger.Info($"{indent}{node.Tag ?? "#text"}{boxInfo}", LogCategory.Rendering);

            if (node is Element element && element.Children != null)
            {
                foreach (var child in element.Children)
                {
                    DumpNode(child, depth + 1);
                }
            }
        }
        // --- Helpers ---

        private static bool GetExplicitPixelValue(double? cssVal, double refSize, out float result)
        {
            if (cssVal.HasValue)
            {
                result = (float)cssVal.Value;
                return true; 
            }
            // Add other CSSUnit logic if needed (e.g. from CssComputed wrappers)
            // For now, CssComputed mainly returns pixels in double?.
            result = 0;
            return false;
        }



        private void ArrangeFlex(Element element, SKRect finalRect, int depth, CssComputed style, IEnumerable<Node> children)
        {
             // CRITICAL FIX: Clamp infinite finalRect dimensions to prevent infinite child coordinates
             // Use viewport width/height as fallback instead of arbitrary large value
             var safeRect = finalRect;
             float safeWidth = float.IsInfinity(finalRect.Width) || float.IsNaN(finalRect.Width) ? _viewportWidth : finalRect.Width;
             float safeHeight = float.IsInfinity(finalRect.Height) || float.IsNaN(finalRect.Height) ? _viewportHeight : finalRect.Height;
             float safeLeft = float.IsInfinity(finalRect.Left) || float.IsNaN(finalRect.Left) ? 0f : finalRect.Left;
             float safeTop = float.IsInfinity(finalRect.Top) || float.IsNaN(finalRect.Top) ? 0f : finalRect.Top;
             safeRect = new SKRect(safeLeft, safeTop, safeLeft + safeWidth, safeTop + safeHeight);
             
             // Delegate to shared implementation (CssFlexLayout.cs)
             // finalRect here is actually calculation contentBox passed from ArrangeNodeCore
             CssFlexLayout.Arrange(
                 element,
                 safeRect,
                 (n, r, d) => ArrangeNode(n, r, d),
                 GetStyle,
                 (n) => _desiredSizes.TryGetValue(n, out var s) ? s : SKSize.Empty,
                 ShouldHide,
                 depth);
        }



        private class FlexItem
        {
            public Node Node;
            public CssComputed Style;
            public SKSize Size;
            public double FlexGrow; // flex-grow value (0 by default)
            public double FlexShrink; // flex-shrink value (1 by default)
            public float ExpandedMainSize; // Size after flex-grow/shrink distribution
        }

        private class FlexLine
        {
            public List<FlexItem> Items = new List<FlexItem>();
            public float MainSize = 0;
            public float CrossSize = 0;
        }

        // ===================================================================
        // CSS GRID LAYOUT
        // ===================================================================

        /// <summary>
        /// Measure a CSS Grid container
        /// </summary>
        public LayoutMetrics MeasureGrid(Element element, SKSize availableSize, int depth)
        {
            if (element == null) return new LayoutMetrics();
            return GridLayoutComputer.Measure(element, availableSize, _styles, depth, GetChildrenWithPseudos(element));
        }

        /// <summary>
        /// Arrange children within a CSS Grid container
        /// </summary>
        public void ArrangeGrid(Element element, SKRect bounds, int depth)
        {
            if (element == null) return;
            
            GridLayoutComputer.Arrange(
                element,
                bounds,
                _styles,
                _boxes,
                depth,
                (node, rect, d) => ArrangeNode(node, rect, d), // Delegate child arrangement
                GetChildrenWithPseudos(element)
            );
        }

        private struct BfcScope : IDisposable
        {
            private MinimalLayoutComputer _computer;
            public BfcScope(MinimalLayoutComputer computer)
            {
                _computer = computer;
                _computer._activeBfcFloats.Push(new List<FloatExclusion>());
            }
            public void Dispose()
            {
                if (_computer._activeBfcFloats.Count > 0)
                    _computer._activeBfcFloats.Pop();
            }
        }

        public int GetZeroSizedCount() => _zeroSizedCount;
    }
}

