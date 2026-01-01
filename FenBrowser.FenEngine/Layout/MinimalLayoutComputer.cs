using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
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
    public class MinimalLayoutComputer : ILayoutComputer
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
        
        // Track ancestors during Arrange for absolute positioning CB resolution
        private readonly Dictionary<Node, SKRect> _ancestorRects = new Dictionary<Node, SKRect>();
        private readonly Dictionary<Node, (float Top, float Bottom)> _effectiveMargins = new Dictionary<Node, (float Top, float Bottom)>();
        private readonly string _baseUri;

        public MinimalLayoutComputer(IReadOnlyDictionary<Node, CssComputed> styles, float viewportWidth, float viewportHeight, string baseUri = null)
        {
            _styles = styles ?? new Dictionary<Node, CssComputed>();
            _viewportWidth = viewportWidth > 0 ? viewportWidth : 1920;
            _viewportHeight = viewportHeight > 0 ? viewportHeight : 1080;
            _baseUri = baseUri;
        }

        // Get computed style for a node - UA defaults now handled by ua.css
        private CssComputed GetStyle(Node node)
        {
            if (node == null) return null;
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
            }
            
            // Inject defaults when CSS doesn't provide them (ua.css em parsing may fail)
            if (node is Element elem)
            {
                string tag = elem.TagName.ToUpperInvariant();
                
                // BODY defaults
                if (tag == "BODY")
                {
                    if (style.Display == null) style.Display = "block";
                    if (style.Margin.Top == 0 && style.Margin.Bottom == 0 && style.Margin.Left == 0 && style.Margin.Right == 0)
                        style.Margin = new Thickness(8);
                    if (style.FontSize == null || style.FontSize < 1) style.FontSize = 16.0;
                    if (style.ForegroundColor == null) style.ForegroundColor = SKColors.Black;
                    if (string.IsNullOrEmpty(style.FontFamilyName)) style.FontFamilyName = "sans-serif";
                    SetBackgroundColor(style, 0xFFFFFFFF);
                }
                // Headings with bold and proper sizes
                else if (tag == "H1")
                {
                    if (style.Display == null) style.Display = "block";
                    if (style.Margin.Top == 0 && style.Margin.Bottom == 0) style.Margin = new Thickness(0, 21, 0, 21);
                    if (style.FontWeight == null || style.FontWeight == 0) style.FontWeight = 700;
                    if (style.FontSize == null || style.FontSize < 1) style.FontSize = 32.0;
                }
                // [FIX] Ensure embedded content has display style (force inline-block for containers to ensure boxes)
                else if (tag == "PICTURE")
                {
                    if (style.Display == null) style.Display = "inline-block";
                }
                else if (tag == "IMG" || tag == "SVG" || tag == "VIDEO" || tag == "CANVAS")
                {
                    if (style.Display == null) style.Display = "inline-block";
                }
                else if (tag == "H2")
                {
                    if (style.Display == null) style.Display = "block";
                    if (style.Margin.Top == 0 && style.Margin.Bottom == 0) style.Margin = new Thickness(0, 20, 0, 20);
                    if (style.FontWeight == null || style.FontWeight == 0) style.FontWeight = 700;
                    if (style.FontSize == null || style.FontSize < 1) style.FontSize = 24.0;
                }
                else if (tag == "H3")
                {
                    if (style.Display == null) style.Display = "block";
                    if (style.Margin.Top == 0 && style.Margin.Bottom == 0) style.Margin = new Thickness(0, 18, 0, 18);
                    if (style.FontWeight == null || style.FontWeight == 0) style.FontWeight = 700;
                    if (style.FontSize == null || style.FontSize < 1) style.FontSize = 18.7;
                }
                else if (tag == "P")
                {
                    if (style.Display == null) style.Display = "block";
                    if (style.Margin.Top == 0 && style.Margin.Bottom == 0) style.Margin = new Thickness(0, 16, 0, 16);
                }
                // Form controls - default to inline-block
                else if (tag == "BUTTON" || tag == "INPUT" || tag == "SELECT" || tag == "TEXTAREA")
                {
                    if (style.Display == null) style.Display = "inline-block";
                }
                // Inline elements
                else if (tag == "SPAN" || tag == "A" || tag == "B" || tag == "I" || tag == "STRONG" || tag == "EM" || tag == "BR")
                {
                    if (style.Display == null) style.Display = "inline";
                }
                // Replaced elements
                else if (tag == "IMG" || tag == "VIDEO" || tag == "IFRAME" || tag == "CANVAS" || tag == "SVG")
                {
                    if (style.Display == null) style.Display = "inline";
                }
                // Metadata elements - Force hide if not specified
                else if (tag == "HEAD" || tag == "STYLE" || tag == "SCRIPT" || tag == "TITLE" || tag == "META" || tag == "LINK" || tag == "BASE")
                {
                    if (style.Display == null) style.Display = "none";
                }
                // Other block elements
                else if (style.Display == null)
                {
                    if (tag == "DIV" || tag == "SECTION" || tag == "ARTICLE" || tag == "HEADER" || tag == "FOOTER" || tag == "NAV" || tag == "MAIN" ||
                        tag == "UL" || tag == "OL" || tag == "LI" || tag == "DL" || tag == "DT" || tag == "DD" ||
                        tag == "FORM" || tag == "FIELDSET" || tag == "TABLE" || tag == "BLOCKQUOTE" || tag == "PRE" || tag == "FIGURE" || tag == "ADDRESS" || tag == "HR")
                    {
                        style.Display = "block";
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
            FenLogger.Debug($"[MinimalLayoutComputer] Measure node={node.Tag ?? "#text"} available={availableSize}", LogCategory.Rendering);
            
            _traversedCount = 0;
            _textLines.Clear(); 
            _inlineCache.Clear(); 
            var m = MeasureNode(node, availableSize, 1);
            return m; 
        }

        public void Arrange(Node node, SKRect finalRect)
        {
            if (node == null) return;
            FenLogger.Debug($"[MinimalLayoutComputer] Arrange node={node.Tag ?? "#text"} rect={finalRect}", LogCategory.Rendering);
            
            FenLogger.Debug($"[MinimalLayoutComputer] Arrange node={node.Tag ?? "#text"} rect={finalRect}", LogCategory.Rendering);
            
            _traversedCount = 0;
            _ancestorRects.Clear();
            ArrangeNode(node, finalRect, 1);
        }

        private LayoutMetrics MeasureNode(Node node, SKSize availableSize, int depth)
        {
            if (node == null) return new LayoutMetrics();
            if (depth > 100) return new LayoutMetrics();

            var style = GetStyle(node);
            bool isHidden = ShouldHide(node, style);
            
            // DIAGNOSTIC: Trace tree traversal
            if (depth < 10 && node is Element el)
            {
                 string tags = el.TagName;
                 string cls = el.GetAttribute("class") ?? "";
                 string ids = el.GetAttribute("id") ?? "";
                 string indent = new string(' ', depth * 2);
                 string hiddenStr = isHidden ? "[HIDDEN]" : "[VISIBLE]";
                 /* [PERF-REMOVED] */
            }

            if (isHidden) return new LayoutMetrics();

            float w = 0, h = 0;
            string tag = (node as Element)?.TagName?.ToUpperInvariant() ?? (node.IsText ? "#text" : "");
            
            bool isExplicitWidth = false;
            bool isExplicitHeight = false;

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
            }

            SKSize childConstraint = availableSize;
            if (float.IsInfinity(childConstraint.Width) || childConstraint.Width > 1e6f)
                childConstraint.Width = _viewportWidth > 0 ? _viewportWidth : 800;

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
                childConstraint = new SKSize(Math.Max(0, contentW), availableSize.Height);
            }

            LayoutMetrics m;
            
            bool shouldFillViewport = (tag == "HTML" || tag == "BODY" || depth == 1);
            string id = (node as Element)?.GetAttribute("id")?.ToLowerInvariant();
            if (tag == "IMG") {
                   var s = GetStyle(node as Element);
                   var p = node.Parent as Element;
                   FenLogger.Debug($"[IMG-TRACE] id={id} Opacity={s?.Opacity} Vis={s?.Visibility} Parent={p?.TagName} ParentClass={p?.GetAttribute("class")}");
                }
            bool isNewTab = (tag == "BODY" && id == "fen-newtab");

            string display = style?.Display?.ToLowerInvariant();
            if (isNewTab) display = "flex";

            var elem = node as Element;
            

            
            if (display == "flex" || display == "inline-flex") 
                m = MeasureFlexInternal(elem, childConstraint, (display == "flex" && elem.TagName == "BODY"), depth, node); 
            else if (display == "grid")
                m = MeasureGrid(elem, childConstraint);
            else if (tag == "IMG" || tag == "SVG") 
                m = MeasureImage(elem, childConstraint);
            else if (tag == "INPUT")
                m = MeasureInput(elem, childConstraint);
            else if (tag == "TEXTAREA") {
                m = MeasureInput(elem, childConstraint); 
            }
            else if (tag == "BUTTON")
                m = MeasureButton(elem, childConstraint, depth);
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
                m = MeasureBlockInternal(elem, childConstraint, depth, node);

            // DIAGNOSTIC: Log measurement result
            if (depth < 10 && elem != null)
            {
                string childInfo = "";
                if (elem.Children != null) childInfo = $"children={elem.Children.Count}";
                
                // Only log for interesting elements to reduce noise
                string cls = elem.GetAttribute("class") ?? "";
                if (tag == "A" || tag == "IMG" || tag == "BUTTON" || tag == "INPUT" || cls.Contains("gb"))
                {
                     /* [PERF-REMOVED] */
                }
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
                if (pt == 0 && bt_top == 0)
                {
                    mt = MarginCollapseComputer.CollapseMargin(mt, m.MarginTop);
                }
                else
                {
                    h += m.MarginTop;
                }

                if (pb == 0 && bb == 0 && !isExplicitHeight)
                {
                    mb = MarginCollapseComputer.CollapseMargin(mb, m.MarginBottom);
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
                if (h < availableSize.Height && !float.IsInfinity(availableSize.Height)) h = availableSize.Height;
                if (w < availableSize.Width && !float.IsInfinity(availableSize.Width)) w = availableSize.Width;
            }
            // ... (rest of blocking logic)
            else
            {
                bool isBlock = (display == "block" || display == null); 
                bool isFloat = style?.Float?.ToLowerInvariant() == "left";
                
                if (!isExplicitWidth && isBlock && !node.IsText && tag != "IMG" && !isFloat) 
                {
                     float usedWidth = childConstraint.Width - (ml + mr);
                     if (style != null && style.BoxSizing != "border-box")
                     {
                         // FIX: Subtract Horizontal Padding/Border (Left+Right)
                         float paddingH = (float)(style.Padding.Left + style.Padding.Right);
                         float borderH = (float)(style.BorderThickness.Left + style.BorderThickness.Right);
                         usedWidth -= (paddingH + borderH);
                     }

                     w = Math.Max(0, usedWidth);
                }
                else if (w <= 0 && !node.IsText) 
                {
                     bool isInlineBlock = (display == "inline-block" || display == "inline");
                     if (!isFloat && !isInlineBlock) {
                         float usedWidth = childConstraint.Width - (ml + mr);
                         if (style != null && style.BoxSizing != "border-box")
                         {
                             var p = style.Padding;
                             var b = style.BorderThickness;
                             usedWidth -= (float)(p.Left + p.Right + b.Left + b.Right);
                         }
                         w = Math.Max(0, usedWidth);
                     } 
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

        private void ArrangeNode(Node node, SKRect finalRect, int depth)
        {
            if (node == null) return;
            


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
            while (p != null)
            {
                var pStyle = GetStyle(p);
                string pos = pStyle?.Position?.ToLowerInvariant() ?? "static";
                if (pos != "static")
                {
                    if (_ancestorRects.TryGetValue(p, out var rect))
                    {
                        var border = pStyle.BorderThickness;
                        float cbX = rect.Left + (float)border.Left;
                        float cbY = rect.Top + (float)border.Top;
                        float cbW = Math.Max(0, rect.Width - (float)(border.Left + border.Right));
                        float cbH = Math.Max(0, rect.Height - (float)(border.Top + border.Bottom));

                        return new ContainingBlock
                        {
                            Node = p, X = cbX, Y = cbY, Width = cbW, Height = cbH,
                            IsInitial = false, PaddingBox = new SKRect(cbX, cbY, cbX + cbW, cbY + cbH)
                        };
                    }
                }
                p = p.Parent;
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
            if (node is Element eDebug && eDebug.TagName == "IMG")
            {
                FenLogger.Debug($"[ARRANGE-CORE-IMG] Id={eDebug.GetAttribute("id")} ShouldHide={ShouldHide(node, style)} Rect={finalRect} StyleDisp={style?.Display}");
            }

            if (ShouldHide(node, style)) return;

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
            
            box.PaddingBox = new SKRect(box.BorderBox.Left + bL, box.BorderBox.Top + bT, box.BorderBox.Right - bR, box.BorderBox.Bottom - bB);
            box.ContentBox = new SKRect(box.PaddingBox.Left + pL, box.PaddingBox.Top + pT, box.PaddingBox.Right - pR, box.PaddingBox.Bottom - pB);

            if (node.IsText && _textLines.TryGetValue(node, out var computedLines))
            {
                box.Lines = computedLines;
            }

            _boxes[node] = box;

            string tag = (node as Element)?.TagName?.ToUpperInvariant() ?? "";
            string id = (node as Element)?.GetAttribute("id")?.ToLowerInvariant();
            bool isNewTab = (tag == "BODY" && id == "fen-newtab");

            string display = style?.Display?.ToLowerInvariant();
            if (isNewTab) display = "flex"; 
            
 

            var elem = node as Element;
            if (display == "flex" || display == "inline-flex") 
                ArrangeFlex(elem, box.ContentBox, depth, style, elem.Children);
            else if (display == "grid")
                ArrangeGrid(elem, box.ContentBox);
            else if (node.IsText) 
                ArrangeText(node, box.ContentBox);
            else 
                ArrangeBlockInternal(elem, box.ContentBox, depth, node);
        }

        private LayoutMetrics MeasureBlockInternal(Element element, SKSize availableSize, int depth, Node fallbackNode)
        {
            var childrenSource = element?.Children ?? fallbackNode?.Children;
            if (childrenSource == null) return new LayoutMetrics();

            if (element != null && (element.TagName == "A" || element.GetAttribute("class") == "o3j99"))
            {
                // FenLogger.Debug($"[MEASURE-BLOCK-ENTRY] {element.TagName} id={element.GetAttribute("id")} class={element.GetAttribute("class")}");
            }

            // IFC CHECK
            bool useIFC = false;
            foreach (var c in childrenSource)
            {
                bool isInline = IsInlineLevel(c);
                if (element != null && (element.GetAttribute("class") == "o3j99" || element.TagName == "A"))
                {
                   // FenLogger.Debug($"[IFC-TRACE] Container={element.TagName}.{element.GetAttribute("class")} Child={c.NodeName} IsInline={isInline}");
                }
                
                if (isInline) 
                {
                     if (c is Text t && string.IsNullOrWhiteSpace(t.Data)) continue;
                     useIFC = true; 
                     break; 
                }
            }


            if (useIFC && element != null)
            {
                FenLogger.Debug($"[IFC-DECISION] Using Inline Layout for {element.TagName}", LogCategory.Rendering);
                return MeasureInlineContext(element, availableSize);
            }

            float curY = 0;
            float maxW = 0;
            float maxActualBottom = 0; // Track actual overflow
            float lastMB = 0;
            bool first = true;
            
            // Float tracking
            float floatX = 0;
            float currentFloatHeight = 0;
            float availableW = availableSize.Width;

            float internalMT = 0;
            float internalMB = 0;

            foreach (var child in childrenSource)
            {
                var childStyle = GetStyle(child);
                if (ShouldHide(child, childStyle)) continue;

                if (child is Text txt && string.IsNullOrWhiteSpace(txt.Data))
                {
                    continue;
                }
                
                // RESTORED: Position check
                string pos = childStyle?.Position?.ToLowerInvariant();
                bool isAbs = pos == "absolute" || pos == "fixed";

                if (isAbs)
                {
                     continue;
                }
                
                LayoutMetrics childSize;

                if (childStyle.Display == "block" || childStyle.Display == "flex" || childStyle.Display == "grid")
                {
                    // BLOCK/FLEX/GRID LAYOUT
                    // Calculate available width for child, considering margins
                    float childML = (float)(childStyle.Margin.Left);
                    float childMR = (float)(childStyle.Margin.Right);
                    float childWidthConstraints = availableW - childML - childMR;
                    if (childWidthConstraints < 0) childWidthConstraints = 0;

                    // Pass constrained width
                    childSize = MeasureNode(child, new SKSize(childWidthConstraints, availableSize.Height), depth + 1);

                    // Re-add margins for flow logic
                    float fullChildW = childSize.MaxChildWidth + childML + childMR;
                    maxW = Math.Max(maxW, fullChildW);
                }
                else
                {
                   // Other types (inline-block etc or fallback)
                   childSize = MeasureNode(child, availableSize, depth + 1);
                   maxW = Math.Max(maxW, childSize.MaxChildWidth);
                }

                // Restore original MeasureNode call for non-block logic if needed, but the above block covers most.
                // However, the original code inside the loop was:
                // var childSize = MeasureNode(child, availableSize, depth + 1);
                
                // Let's stick closer to the original structure but apply the constraint.
                // We need to fetch style first.
                // Moved logic to before MeasureNode call.
                
                bool isFloat = childStyle?.Float?.ToLowerInvariant() == "left";

                if (isFloat)
                {
                    // Float Logic: No margin collapsing
                    float mt = childSize.MarginTop;
                    float mb = childSize.MarginBottom;
                    float ml = (float)(childStyle?.Margin.Left ?? 0);
                    float mr = (float)(childStyle?.Margin.Right ?? 0);
                    
                    float fullChildW = childSize.MaxChildWidth + ml + mr;
                    float fullChildH = childSize.ContentHeight + mt + mb;
                    
                    if (floatX + fullChildW > availableW && floatX > 0)
                    {
                        curY += currentFloatHeight;
                        floatX = 0;
                        currentFloatHeight = 0;
                    }
                    
                    floatX += fullChildW;
                    currentFloatHeight = Math.Max(currentFloatHeight, fullChildH);
                    
                    float childActualH = childSize.ActualHeight + mt + mb;
                    maxActualBottom = Math.Max(maxActualBottom, (curY + childActualH)); 
                    maxW = Math.Max(maxW, floatX);
                }
                else
                {
                    // Block Logic
                    if (floatX > 0)
                    {
                        curY += currentFloatHeight;
                        floatX = 0;
                        currentFloatHeight = 0;
                        first = true;
                        lastMB = 0;
                    }

                    maxW = Math.Max(maxW, childSize.MaxChildWidth);
                    
                    float mt = childSize.MarginTop;
                    float mb = childSize.MarginBottom;
                    
                    if (first) {
                        internalMT = mt; // First child margin TOP
                        first = false;
                    } else {
                        float collapsedMargin = MarginCollapseComputer.CollapseMargin(lastMB, mt);
                        curY += collapsedMargin;
                    }
                    
                    float childActualBottom = curY + childSize.ActualHeight;
                    maxActualBottom = Math.Max(maxActualBottom, childActualBottom);

                    curY += childSize.ContentHeight; 
                    lastMB = mb;
                }
            }
            
            internalMB = lastMB; // Last child margin BOTTOM
            curY += currentFloatHeight; 
            
            maxActualBottom = Math.Max(maxActualBottom, curY);

            return new LayoutMetrics { 
                ContentHeight = curY, 
                ActualHeight = maxActualBottom, 
                MaxChildWidth = maxW,
                MarginTop = internalMT,
                MarginBottom = internalMB
            };
        }

        private void ArrangeBlockInternal(Element element, SKRect finalRect, int depth, Node fallbackNode)
        {
            var childrenSource = element?.Children ?? fallbackNode?.Children;
            if (childrenSource == null) return;

            // IFC CHECK
            // IFC CHECK
            if (element != null)
            {
                if (_inlineCache.ContainsKey(element))
            {
                // FenLogger.Debug($"[IFC-CACHE-HIT] Element={element.TagName} Hash={element.GetHashCode()}", LogCategory.Rendering);
                ArrangeInlineContext(element, finalRect);
                return;
            }
            else
            {
                 // Trace miss
                 // FenLogger.Debug($"[IFC-CACHE-MISS] Element={element.TagName} Hash={element.GetHashCode()} CacheCount={_inlineCache.Count}", LogCategory.Rendering);
            }    }
                else
                {
                    // Debug why it missed if we expected it
                   // FenLogger.Debug($"[IFC-CACHE-MISS] {element.TagName} falling back to Block Layout", LogCategory.Rendering);
                }
            
            float curY = finalRect.Top;
            float lastMB = 0;
            bool first = true;

            var parentStyle = element != null ? GetStyle(element) : null;
            
            // Debug Logging for Flexbox
            if (element != null && (element.GetAttribute("class")?.Contains("flex") == true))
            {
                 System.IO.File.AppendAllText(@"c:\Users\udayk\Videos\FENBROWSER\debug_flex.txt", 
                    $"Tag={element.TagName} Class={element.GetAttribute("class")} Display='{parentStyle?.Display}'\r\n");
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

            float pt = (float)(parentStyle?.Padding.Top ?? 0);
            float bt_top = (float)(parentStyle?.BorderThickness.Top ?? 0);
            float pl = (float)(parentStyle?.Padding.Left ?? 0);
            float bl = (float)(parentStyle?.BorderThickness.Left ?? 0);

            // Float tracking
            float floatX = finalRect.Left + pl + bl;
            float floatRowTop = curY;
            float currentFloatHeight = 0;
            float availableW = finalRect.Width;

            foreach (var child in childrenSource)
            {
                var childStyle = GetStyle(child);
                if (ShouldHide(child, childStyle)) continue;
                
                if (_desiredSizes.TryGetValue(child, out var size))
                {
                    string pos = childStyle?.Position?.ToLowerInvariant();
                    bool isAbs = pos == "absolute" || pos == "fixed";

                    if (isAbs)
                    {
                        ArrangeNode(child, finalRect, depth + 1);
                        continue;
                    }

                    bool isFloat = childStyle?.Float?.ToLowerInvariant() == "left";
                    
                    float mt = (float)childStyle.Margin.Top;
                    float mb = (float)childStyle.Margin.Bottom;
                    float ml = (float)childStyle.Margin.Left;
                    float mr = (float)childStyle.Margin.Right;

                    // Use effective margins if available (calculated during Measure)
                    if (_effectiveMargins.TryGetValue(child, out var eff))
                    {
                        mt = eff.Top;
                        mb = eff.Bottom;
                    }



                    if (child is Element el)
                    {
                         try { 
                             var fs = childStyle?.FontSize ?? 0;
                             var m = childStyle?.Margin;
                             var p = childStyle?.Padding;
                             // Simplified logging to avoid build errors
                             System.IO.File.AppendAllText(@"c:\Users\udayk\Videos\FENBROWSER\layout_compliance_log.txt", 
                             $"tag={el.Tag} fs={fs:F2} margin={m} padding={p} class='{el.GetAttribute("class")}'\r\n"); 
                         } catch {}
                    }





                    
                    if (isFloat)
                    {
                        float borderBoxW = size.Width;
                        float borderBoxH = size.Height;
                        float fullChildW = borderBoxW + ml + mr;
                        float fullChildH = borderBoxH + mt + mb;
                        
                        if (floatX + fullChildW > finalRect.Right + 0.1f && floatX > finalRect.Left + pl + bl)
                        {
                            curY += currentFloatHeight;
                            floatRowTop = curY;
                            floatX = finalRect.Left + pl + bl;
                            currentFloatHeight = 0;
                        }

                        float childX = floatX + ml;
                        float childY = floatRowTop + mt; 
                        
                        var childRect = new SKRect(childX, childY, childX + borderBoxW, childY + borderBoxH);
                        ArrangeNode(child, childRect, depth + 1);

                        floatX += fullChildW;
                        currentFloatHeight = Math.Max(currentFloatHeight, fullChildH);
                    }
                    else
                    {
                        // Block Logic
                        if (floatX > finalRect.Left + pl + bl)
                        {
                            curY += currentFloatHeight;
                            floatRowTop = curY;
                            floatX = finalRect.Left + pl + bl; 
                            currentFloatHeight = 0;
                            first = true;
                            lastMB = 0;
                        }

                        if (first) {
                            // If parent has padding/border, margin collapse is blocked for the first child (wrt parent top)
                            // The child should start at padding box top (+ margin if not collapsed)
                            bool canCollapse = (pt == 0 && bt_top == 0 && element?.TagName != "HTML");
                            
                            if (canCollapse) {
                                // Collapsed with parent - child starts at curY (which is BorderTop)
                                // But if collapsed, the margin bubbled up.
                            } else {
                                // Not collapsed. Child starts after padding/border + margin
                                curY += pt + bt_top + mt;
                            }
                            first = false;
                        } else {
                            float collapsedMargin = MarginCollapseComputer.CollapseMargin(lastMB, mt);
                            curY += collapsedMargin;
                        }

                        float childX = finalRect.Left + pl + bl + ml;
                        float borderBoxW = size.Width;
                        float borderBoxH = size.Height;
                        
                        var childRect = new SKRect(childX, curY, childX + borderBoxW, curY + borderBoxH);
                        // DEBUG: Trace large vertical gaps
                        if ((borderBoxH > 500 && child.NodeName != "BODY") && child is Element cEle)
                        {
                            // FenLogger.Debug($"[LAYOUT-GAP] Inside <{element?.TagName}> -> Child <{cEle.TagName} id='{cEle.GetAttribute("id")}' class='{cEle.GetAttribute("class")}'> H={borderBoxH} MT={mt} MB={mb} Y={curY}");
                        }
                        ArrangeNode(child, childRect, depth + 1);
                        
                        curY = childRect.Bottom;
                        lastMB = mb;
                        floatRowTop = curY + mb; 
                    }
                }
            }
            // End
        }
        
        private LayoutMetrics MeasureFlexInternal(Element element, SKSize availableSize, bool isCenteredRoot, int depth, Node fallbackNode = null)
        {
            var target = element ?? fallbackNode as Element; // Ensure Element
            if (target == null) return new LayoutMetrics();
            
            return CssFlexLayout.Measure(
                target, 
                availableSize, 
                MeasureNode, 
                GetStyle, 
                ShouldHide, 
                depth);
        }



        private LayoutMetrics MeasureButton(Element element, SKSize availableSize, int depth)
        {
            // Buttons are like inline-blocks but with default padding/border (handled in UA styles eventually)
            // But we need to ensure they measure their text content.
            // Treat as essentially a block for measurement purposes
            return MeasureBlockInternal(element, availableSize, depth, element);
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
                depth);
        }


        // --- INLINE FORMATTING CONTEXT ---

        private bool IsInlineLevel(Node node)
        {
            if (node is Text) return true;
            var style = GetStyle(node);
            string d = style?.Display?.ToLowerInvariant();
            return d == "inline" || d == "inline-block" || node.NodeName == "SPAN" || node.NodeName == "A" || node.NodeName == "B" || node.NodeName == "I" || node.NodeName == "IMG";
        }

        private LayoutMetrics MeasureInput(Element element, SKSize availableSize)
        {
             // Input needs intrinsic size if not specified
             // Default approximately 150x20
             // But for Google search bar, it relies on width: 100% usually?
             // Let's assume standard block measure first? No, inputs are atomic replaced elements generally.
             
             // Inspect style
             var style = GetStyle(element);
             float w = 150;
             float h = 20;
             
             if (element.TagName == "TEXTAREA") {
                 h = 40; // Default taller for textarea
                 w = 300;
             }
             
             if (style != null)
             {
                 if (style.Width.HasValue) w = (float)style.Width.Value;
                 if (style.Height.HasValue) h = (float)style.Height.Value;
             }
             
             return new LayoutMetrics { ContentHeight = h, MaxChildWidth = w };
        }

        private LayoutMetrics MeasureInlineContext(Element container, SKSize availableSize)
        {
            if (container.TagName == "A" || container.GetAttribute("class") == "o3j99")
            {
               // FenLogger.Debug($"[MEASURE-INLINE-ENTRY] {container.TagName}");
            }

            var result = InlineLayoutComputer.Compute(
                container, 
                availableSize, 
                GetStyle, 
                (elem, sz) => MeasureNode(elem, sz, 0) // Callback for atomic measuring
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

        private void ArrangeInlineContext(Element container, SKRect finalRect)
        {
            if (!_inlineCache.TryGetValue(container, out var result)) return;

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

                ArrangeNode(child, finalChildRect, 0); // Recursively arrange content of inline-blocks
            }
            // Text lines are already in _textLines, which ArrangeNode(Text) will pick up automatically
            // But wait, ArrangeNode is called for CHILDREN.
            // We need to iterate children and call ArrangeNode on them.
            
            // The issue: InlineLayoutComputer recurses logical children.
            // We need to ensure ArrangeNode is called for all those children so they get their Boxes created.
            
            // Simple approach: Iterate children of container. If found in ElementRects, arrange. 
            // If Text, call ArrangeText (which does nothing but is good for consistency).
            
            void ProcessChildren(Node n) {
                if (n is Element e && e.Children != null) {
                    foreach(var c in e.Children) {
                        if (result.ElementRects.TryGetValue(c, out var r)) {
                           // It's an atomic element we placed
                           var absR = new SKRect(finalRect.Left + r.Left, finalRect.Top + r.Top, finalRect.Left + r.Right, finalRect.Top + r.Bottom);
                           ArrangeNode(c, absR, 0);
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
                           ArrangeNode(c, finalRect, 0);
                        } else {
                           // Nested inline (span, etc).
                           // Ensure the inline container itself gets a box so PaintTreeBuilder can traverse it.
                           // We use the container's rect as a placeholder box.
                           ArrangeNode(c, finalRect, 0);
                           
                           // Recursively visit children to ensure they also get processed (if they are atomic or text)
                           ProcessChildren(c);
                        }
                    }
                }
            }
            ProcessChildren(container);
        }

        // ------------------------------------


        // Adapter methods for ILayoutComputer interface
        public LayoutMetrics MeasureFlex(Element element, SKSize s) 
        {
             return MeasureBlockInternal(element, s, 1, element);
        }

        public void ArrangeFlex(Element element, SKRect r) 
        {
             var style = GetStyle(element);
             var children = element.Children;
             ArrangeFlex(element, r, 1, style, children);
        }
        
        public LayoutMetrics MeasureBlock(Element element, SKSize s) 
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
                return MeasureInlineContext(element, s);
            }
            else {
                /* [PERF-REMOVED] */
                return MeasureBlockInternal(element, s, 0, element);
            }
        }

        public void ArrangeBlock(Element element, SKRect r) 
        {
            if (element != null && _inlineCache.ContainsKey(element))
                ArrangeInlineContext(element, r);
            else
                ArrangeBlockInternal(element, r, 0, element);
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
            if (elem != null)
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
            
            // CSS display:none and visibility:hidden always take precedence
            if (style?.Display == "none" || style?.Visibility == "hidden") 
            {
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
                
                // Never hide html/body
                if (tag == "html" || tag == "body") return false;
                
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
                bool isReplaced = tag == "input" || tag == "img" || tag == "button" || tag == "hr" || tag == "svg" || tag == "iframe";
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
                    // DIAGNOSTIC: Log elements hidden due to "no content/size/visuals" rule
                    if (tag == "a" || tag == "img" || tag == "video" || tag == "svg" || tag == "footer" || tag == "header" || (e.GetAttribute("class")?.Contains("gb") ?? false))
                    {
                         /* [PERF-REMOVED] */
                    }
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

        public LayoutMetrics MeasureGrid(Element e, SKSize constraints)
        {
            // [HACK CLEANUP] Using robust CssGridLayout from Rendering namespace
            if (e == null) return new LayoutMetrics();
            if (!_styles.TryGetValue(e, out var style)) return MeasureBlock(e, constraints);

            float width = constraints.Width;
            float height = 0;

            // 1. Resolve Container Dimensions
            bool isExplicitWidth = GetExplicitPixelValue(style.Width, _viewportWidth, out float explicitW);
            bool isExplicitHeight = GetExplicitPixelValue(style.Height, _viewportHeight, out float explicitH);
            
            if (isExplicitWidth) width = explicitW;
            
            // 2. Setup Grid Engine
            var grid = new CssGridLayout
            {
                ContainerWidth = width,
                ContainerHeight = isExplicitHeight ? explicitH : 0, // 0 for auto-height calculation
                ColumnGap = GetExplicitPixelValue(style.Gap, width, out float cGap) ? cGap : 0,
                RowGap = cGap,
                JustifyItems = style.Map.TryGetValue("justify-items", out var ji) ? ji : "stretch",
                AlignItems = style.Map.TryGetValue("align-items", out var ai) ? ai : "stretch",
                JustifyContent = style.Map.TryGetValue("justify-content", out var jc) ? jc : "start",
                AlignContent = style.Map.TryGetValue("align-content", out var ac) ? ac : "start"
            };

            // Setup measure callback for auto tracks
            grid.MeasureChild = (child) => 
            {
                // Measure with infinite constraints to get intrinsic size
                // We increment depth to avoid infinite recursion if structure is deep
                // Note: using 'constraints' width might be better than infinite if we want wrapped text size?
                // But specifically for 'auto' track sizing, we usually want max-content.
                // Using infinite width gives max-content.
                var m = MeasureNode(child, new SKSize(float.PositiveInfinity, float.PositiveInfinity), 1); // Use safe depth
                // MeasureGrid definition at line 955: public LayoutMetrics MeasureGrid(Element e, SKSize constraints)
                // It does NOT take depth. I should update signature or assume specific depth.
                // Let's assume depth is not critical or pass a safe value.
                // However, MeasureNode checks depth > 100.
                return new SKSize(m.MaxChildWidth, m.ContentHeight);
            };

            // 3. Configure Template
            if (style.Map.TryGetValue("grid-template-columns", out var cols)) grid.SetTemplateColumns(cols);
            else grid.SetTemplateColumns("none");
            
            if (style.Map.TryGetValue("grid-template-rows", out var rows)) grid.SetTemplateRows(rows);
            else grid.SetTemplateRows("none");

            if (style.Map.TryGetValue("grid-template-areas", out var areas)) grid.SetTemplateAreas(areas);

            // 4. Add Items
            if (e.Children != null)
            {
                foreach (var child in e.Children)
                {
                    if (child is Element childEl && _styles.TryGetValue(childEl, out var childStyle))
                    {
                        grid.AddItem(childEl, childStyle);
                    }
                }
            }

            // 5. Compute Layout
            // We pass Container Dimensions and axis info implicitly handled by ComputeLayout now calling CalculateTrackStarts
            var placements = grid.ComputeLayout(0, 0).ToList();
            
            if(isExplicitHeight)
            {
                height = explicitH;
            }
            else
            {
                // Auto-height: Find bottom-most item
                foreach(var p in placements)
                {
                    if (p.rect.Bottom > height) height = p.rect.Bottom;
                }
            }

            return new LayoutMetrics { ContentHeight = height, ActualHeight = height };
        }

        public void ArrangeGrid(Element e, SKRect rect)
        {
            if (e == null) return;
            if (!_styles.TryGetValue(e, out var style)) 
            {
                ArrangeBlock(e, rect);
                return;
            }

            // 1. Re-setup Grid (Logic duplicated)
             var grid = new CssGridLayout
            {
                ContainerWidth = rect.Width,
                ContainerHeight = rect.Height,
                ColumnGap = GetExplicitPixelValue(style.Gap, rect.Width, out float cGap) ? cGap : 0,
                RowGap = cGap,
                JustifyItems = style.Map.TryGetValue("justify-items", out var ji) ? ji : "stretch",
                AlignItems = style.Map.TryGetValue("align-items", out var ai) ? ai : "stretch"
            };
            
            grid.MeasureChild = (child) => 
            {
                var m = MeasureNode(child, new SKSize(float.PositiveInfinity, float.PositiveInfinity), 1);
                return new SKSize(m.MaxChildWidth, m.ContentHeight);
            };

            if (style.Map.TryGetValue("grid-template-columns", out var cols)) grid.SetTemplateColumns(cols);
            if (style.Map.TryGetValue("grid-template-rows", out var rows)) grid.SetTemplateRows(rows);
            if (style.Map.TryGetValue("grid-template-areas", out var areas)) grid.SetTemplateAreas(areas);

            if (e.Children != null)
            {
                foreach (var child in e.Children)
                {
                    if (child is Element childEl && _styles.TryGetValue(childEl, out var childStyle))
                    {
                        grid.AddItem(childEl, childStyle);
                    }
                }
            }

            // 2. Compute Final Positions relative to Grid Container (rect.Left, rect.Top)
            var placements = grid.ComputeLayout(rect.Left, rect.Top);

            // 3. Arrange Children
            foreach (var (childEl, childRect) in placements)
            {
                // Recursively arrange child
                // Note: We might need to call Measure on child again if its size depends on the grid cell?
                // CssGridLayout computed the cell 'childRect'.
                // If the child is 'stretch', it should fill that rect.
                
                // Store the calculated box
                var box = _boxes.GetOrAdd(childEl, new BoxModel());
                box.ContentBox = childRect; 
                // We're bypassing standard ArrangeBlock/MeasureBlock logic which sets PaddingBox/BorderBox...
                // Ideally, we should call 'Arrange' on the child type. 
                // But Arrange usually CALCULATES position. Here we are GIVEN position.
                // We just need to persist it.
                
                // Dispatch recursion for grandchildren
                ArrangeNode(childEl, childRect, 1);
            }
        }
        public LayoutMetrics ComputeTableLayout(Element e, BoxModel b, float x, float y, float w, float h, int d) => new LayoutMetrics();
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
             // 1. Setup Flex Context
             bool isRow = style.FlexDirection != "column"; // defaulting to row
             FenLogger.Debug($"[FLEX-ARRANGE] Tag={element.TagName} Children={children.Count()} IsRow={isRow} Wrap={style.FlexWrap}");
             bool wrap = style.FlexWrap == "wrap";
             
             // Padding / Border
             float pt = (float)style.Padding.Top;
             float pb = (float)style.Padding.Bottom;
             float pl = (float)style.Padding.Left;
             float pr = (float)style.Padding.Right;
             float bt = (float)style.BorderThickness.Top;
             float bb = (float)style.BorderThickness.Bottom;
             float bl = (float)style.BorderThickness.Left;
             float br = (float)style.BorderThickness.Right;

             float contentX = finalRect.Left + pl + bl;
             float contentY = finalRect.Top + pt + bt;
             float contentW = Math.Max(0, finalRect.Width - pl - pr - bl - br);
             float contentH = Math.Max(0, finalRect.Height - pt - pb - bt - bb); 

             // 2. Filter & Measure Items (using existing _desiredSizes)
             var items = new List<FlexItem>();
             foreach (var c in children)
             {
                 var cStyle = GetStyle(c);
                 if (ShouldHide(c, cStyle)) continue;
                 
                 var pos = cStyle.Position?.ToLowerInvariant();
                 if (pos == "absolute" || pos == "fixed") {
                      ArrangeNode(c, finalRect, depth + 1); 
                      continue;
                 }
                 
                 SKSize size;
                 if (!_desiredSizes.TryGetValue(c, out size)) {
                     continue;
                 }
                 
                 // Read flex-grow from style (default to 0)
                 double flexGrow = cStyle.FlexGrow ?? 0;
                 
                 // Calculate initial main size (width for row, height for column)
                 float initialMainSize = isRow ? 
                     (size.Width + (float)cStyle.Margin.Left + (float)cStyle.Margin.Right) :
                     (size.Height + (float)cStyle.Margin.Top + (float)cStyle.Margin.Bottom);
                 
                 items.Add(new FlexItem { 
                     Node = c, 
                     Style = cStyle, 
                     Size = size, 
                     FlexGrow = flexGrow,
                     ExpandedMainSize = initialMainSize
                 });
             }

             // 3. Line Breaking (Flex Wrap)
             var lines = new List<FlexLine>();
             var currentLine = new FlexLine();
             float mainPos = 0;
             
             foreach (var item in items)
             {
                 // Calculate item main size (width + margin)
                 float itemMainSize = isRow ? 
                      (item.Size.Width + (float)item.Style.Margin.Left + (float)item.Style.Margin.Right) :
                      (item.Size.Height + (float)item.Style.Margin.Top + (float)item.Style.Margin.Bottom);

                 if (wrap && mainPos + itemMainSize > (isRow ? contentW : contentH) && currentLine.Items.Count > 0)
                 {
                     lines.Add(currentLine);
                     currentLine = new FlexLine();
                     mainPos = 0;
                 }

                 currentLine.Items.Add(item);
                 currentLine.MainSize += itemMainSize;
                 
                 // Cross size
                 float itemCrossSize = isRow ?
                      (item.Size.Height + (float)item.Style.Margin.Top + (float)item.Style.Margin.Bottom) :
                      (item.Size.Width + (float)item.Style.Margin.Left + (float)item.Style.Margin.Right);
                 currentLine.CrossSize = Math.Max(currentLine.CrossSize, itemCrossSize);
                 
                 mainPos += itemMainSize;
             }
             if (currentLine.Items.Count > 0) lines.Add(currentLine);

             // 3.5. FLEX-GROW DISTRIBUTION: Distribute remaining space to items with flex-grow > 0
             float mainAxisSize = isRow ? contentW : contentH;
             foreach (var line in lines)
             {
                 double totalFlexGrow = 0;
                 foreach (var item in line.Items)
                 {
                     totalFlexGrow += item.FlexGrow;
                 }
                 
                 if (totalFlexGrow > 0)
                 {
                     float remainingSpace = mainAxisSize - line.MainSize;
                     if (remainingSpace > 0)
                     {
                         float totalExpansion = 0;
                         foreach (var item in line.Items)
                         {
                             if (item.FlexGrow > 0)
                             {
                                 float expansion = (float)(item.FlexGrow / totalFlexGrow * remainingSpace);
                                 item.ExpandedMainSize += expansion;
                                 totalExpansion += expansion;
                             }
                         }
                         // Update line.MainSize to reflect the expanded total
                         line.MainSize += totalExpansion;
                     }
                 }
             }

             // 4. Arrange Lines
             float crossPos = isRow ? contentY : contentX;
             
             foreach (var line in lines)
             {
                 // 5. Arrange Items in Line (Justify Content)
                 float spaceRemaining = (isRow ? contentW : contentH) - line.MainSize;
                 float startMain = isRow ? contentX : contentY;
                 float gap = 0;
                 
                 var justify = style.JustifyContent?.ToLowerInvariant() ?? "flex-start";
                 if (justify == "center")
                 {
                     startMain += spaceRemaining / 2;
                 }
                 else if (justify == "flex-end")
                 {
                     startMain += spaceRemaining;
                 }
                 else if (justify == "space-between" && line.Items.Count > 1)
                 {
                     gap = spaceRemaining / (line.Items.Count - 1);
                 }
                 else if (justify == "space-around" && line.Items.Count > 0)
                 {
                     float halfGap = (spaceRemaining / line.Items.Count) / 2;
                     startMain += halfGap;
                     gap = halfGap * 2;
                 }

                 // Align Items (Cross Axis per item)
                 var align = style.AlignItems?.ToLowerInvariant() ?? "stretch";

                 float lineMainPos = startMain;
                 foreach (var item in line.Items)
                 {
                      // Use ExpandedMainSize which includes flex-grow expansion
                      float itemMain = item.ExpandedMainSize;
                      
                      // Calculate the content size (without margins) - expanded if flex-grow was applied
                      float itemContentWidth = item.Size.Width;
                      float itemContentHeight = item.Size.Height;
                      
                      // If flex-grow expanded this item, increase the content dimension
                      if (item.FlexGrow > 0)
                      {
                          float originalMainSize = isRow ? 
                              (item.Size.Width + (float)item.Style.Margin.Left + (float)item.Style.Margin.Right) :
                              (item.Size.Height + (float)item.Style.Margin.Top + (float)item.Style.Margin.Bottom);
                          float expansion = item.ExpandedMainSize - originalMainSize;
                          if (expansion > 0)
                          {
                              if (isRow)
                                  itemContentWidth += expansion;
                              else
                                  itemContentHeight += expansion;
                          }
                      }

                      // Determine Cross Position
                      float itemCross = 0;
            
                      float myCrossIdx = isRow ? 
                          (item.Size.Height + (float)item.Style.Margin.Top + (float)item.Style.Margin.Bottom) :
                          (item.Size.Width + (float)item.Style.Margin.Left + (float)item.Style.Margin.Right);
                          
                      float availableCross = line.CrossSize; 
                      
                      float crossOffset = 0;
                      if (align == "center")
                      {
                          crossOffset = (availableCross - myCrossIdx) / 2;
                      }
                      else if (align == "flex-end")
                      {
                          crossOffset = availableCross - myCrossIdx;
                      }
                      
                      // Position Item
                      float finalX, finalY;
                      float ml = (float)item.Style.Margin.Left;
                      float mt = (float)item.Style.Margin.Top;
                      
                      if (isRow)
                      {
                          finalX = lineMainPos + ml;
                          finalY = crossPos + crossOffset + mt;
                      }
                      else
                      {
                          finalX = crossPos + crossOffset + ml;
                          finalY = lineMainPos + mt;
                      }
                      
                      var childRect = new SKRect(finalX, finalY, finalX + itemContentWidth, finalY + itemContentHeight);
                      ArrangeNode(item.Node, childRect, depth + 1);

                      lineMainPos += itemMain + gap;
                 }
                 
                 crossPos += line.CrossSize;
             }
        }

        private class FlexItem
        {
            public Node Node;
            public CssComputed Style;
            public SKSize Size;
            public double FlexGrow; // flex-grow value (0 by default)
            public float ExpandedMainSize; // Size after flex-grow distribution
        }

        private class FlexLine
        {
            public List<FlexItem> Items = new List<FlexItem>();
            public float MainSize = 0;
            public float CrossSize = 0;
        }

    }
}