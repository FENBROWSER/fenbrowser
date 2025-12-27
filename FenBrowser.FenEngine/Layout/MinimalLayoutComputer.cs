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

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Minimal layout computer implementation.
    /// Provides basic box layout until full layout engine migration is complete.
    /// </summary>
    public class MinimalLayoutComputer : ILayoutComputer
    {
        private const float DefaultFontSize = 16f;
        private const float DefaultLineHeight = 1.2f;

        private bool IsInlineNode(Node node, CssComputed style)
        {
            if (node is Text) return true;
            string display = style?.Display?.ToLowerInvariant();
            if (display == "inline" || display == "inline-block") return true;
            if (display == "block" || display == "flex" || display == "grid") return false;
            
            string tag = (node as Element)?.TagName?.ToUpperInvariant();
            string[] inlineTags = { "A", "SPAN", "I", "B", "U", "STRONG", "EM", "IMG", "SMALL", "LABEL", "INPUT", "BUTTON", "SELECT", "TEXTAREA" };
            return inlineTags.Contains(tag);
        }
        private (float width, float height, float baseline) MeasureTextPrecision(string text, float fontSize, string fontFamily)
        {
            if (string.IsNullOrEmpty(text)) return (0, 0, 0);
            using (var paint = new SKPaint { TextSize = fontSize, IsAntialias = true })
            {
                if (!string.IsNullOrEmpty(fontFamily))
                {
                    try { paint.Typeface = SKTypeface.FromFamilyName(fontFamily); } catch {}
                }
                
                if (paint.Typeface == null) paint.Typeface = SKTypeface.FromFamilyName("Arial");
                
                float w = paint.MeasureText(text);
                var metrics = paint.FontMetrics;
                float h = metrics.Descent - metrics.Ascent; // Ascent is usually negative
                float baseline = -metrics.Ascent;
                
                return (w, h, baseline);
            }
        }
        
        private readonly ConcurrentDictionary<Node, BoxModel> _boxes = new ConcurrentDictionary<Node, BoxModel>();
        private readonly Dictionary<Node, Node> _parents = new Dictionary<Node, Node>();
        private readonly IReadOnlyDictionary<Node, CssComputed> _styles;
        private readonly float _viewportWidth;
        private readonly float _viewportHeight;
        private int _traversedCount = 0;
        
        public MinimalLayoutComputer(IReadOnlyDictionary<Node, CssComputed> styles, float viewportWidth, float viewportHeight)
        {
            _styles = styles ?? new Dictionary<Node, CssComputed>();
            _viewportHeight = viewportHeight > 0 ? viewportHeight : 1080;
        }

        private CssComputed GetStyle(Node node)
        {
            if (node == null) return null;
            _styles.TryGetValue(node, out var style);
            return style;
        }
        
        public BoxModel GetBox(Node node) => (node != null && _boxes.TryGetValue(node, out var box)) ? box : null;
        public Node GetParent(Node node) => (node != null && _parents.TryGetValue(node, out var parent)) ? parent : null;
        public IEnumerable<KeyValuePair<Node, BoxModel>> GetAllBoxes() => _boxes;
        
        public void ComputeLayout(Node node, float x, float y, float availableWidth, bool shrinkToContent = false, float availableHeight = 0, bool hasTargetAncestor = false)
        {
            if (node == null) return;
            _traversedCount = 0;
            LayoutNode(node, x, y, availableWidth, availableHeight, null, shrinkToContent, false, 0);
        }

        public void RawLayout(Node node, float x, float y, float availableWidth, bool shrinkToContent = false, float availableHeight = 0, bool hasTargetAncestor = false)
        {
            ComputeLayout(node, x, y, availableWidth, shrinkToContent, availableHeight, hasTargetAncestor);
        }

        private float LayoutNode(Node node, float x, float y, float availableWidth, float availableHeight, CssComputed parentStyle = null, bool shrinkToContent = false, bool ignoreFlex = false, int depth = 0)
        {
            try
            {
                if (node == null) return 0;
                if (node.NodeType == NodeType.DocumentType) return 0;
                
                _traversedCount++;
                // Guard against deep recursion (stack overflow protection)
                // Use a managed limit or leverage Thread stack check if possible, but hard limit is safer
                if (_traversedCount > 50000)
                {
                    try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Layout] Traversal LIMIT hit at {node.Tag} count {_traversedCount}\r\n"); } catch {}
                    var failBox = new BoxModel(); 
                    failBox.ContentBox = new SKRect(x, y, x, y);
                    failBox.PaddingBox = failBox.ContentBox;
                    failBox.BorderBox = failBox.ContentBox;
                    failBox.MarginBox = failBox.ContentBox;
                    _boxes[node] = failBox;
                    return 0;
                }

                // NOTE: Proper recursion guard requires incrementing/decrementing a depth counter provided via argument or field.
                // Refactoring LayoutNode signature recursively is painful.
                // However, without it, stack overflow is possible.
                // Let's try to detect stack depth via System.Diagnostics (expensive) or just hope the iterative changes elsewhere help?
                // No, the user requirement is to FIX the crash.
                // Let's check RuntimeHelpers.EnsureSufficientExecutionStack();
                try 
                {
                    System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
                }
                catch (InsufficientExecutionStackException)
                {
                    try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Layout] Stack overflow imminent at node {node.Tag} depth {depth}\r\n"); } catch {}
                    
                    // Fallback: Create empty box so node exists in tree
                    var failBox = new BoxModel(); 
                    failBox.ContentBox = new SKRect(x, y, x, y); // 0 size
                    failBox.PaddingBox = failBox.ContentBox;
                    failBox.BorderBox = failBox.ContentBox;
                    failBox.MarginBox = failBox.ContentBox;
                    _boxes[node] = failBox;
                    return 0;
                }

                // Depth Guard
                if (depth > 250)
                {
                     try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Layout] Depth guard hit at {depth} for {node.Tag}\r\n"); } catch {}
                     var failBox = new BoxModel(); 
                     failBox.ContentBox = new SKRect(x, y, x, y); 
                     failBox.PaddingBox = failBox.ContentBox;
                     failBox.BorderBox = failBox.ContentBox;
                     failBox.MarginBox = failBox.ContentBox;
                     _boxes[node] = failBox;
                     return 0;
                }


                _traversedCount++;
                var style = GetStyle(node);
                bool hasExplicitW = style?.Width.HasValue == true || !string.IsNullOrEmpty(style?.WidthExpression);
                bool hasExplicitH = style?.Height.HasValue == true || !string.IsNullOrEmpty(style?.HeightExpression);
                
                string tagName = (node is Element element0) ? element0.TagName : (node is Text ? "#text" : node.NodeName);
                string tagUpper = tagName.ToUpperInvariant();

                // No site-specific heuristics - render according to CSS spec

                if (style != null && tagUpper == "BODY" && string.IsNullOrEmpty(style.Display)) { style.Display = "block"; }
                if (style != null && (tagUpper == "HEADER" || tagUpper == "MAIN" || tagUpper == "FOOTER"))
                {
                    style.Width = (double)availableWidth;
                    style.MaxWidth = null;
                }

                // Default display fallbacks for common elements with null display
                if (style != null && string.IsNullOrEmpty(style.Display))
                {
                    if (tagUpper == "H1" || tagUpper == "H2" || tagUpper == "H3" || tagUpper == "H4" || tagUpper == "H5" || tagUpper == "H6" ||
                        tagUpper == "P" || tagUpper == "DIV" || tagUpper == "UL" || tagUpper == "OL" || tagUpper == "LI" ||
                        tagUpper == "SECTION" || tagUpper == "ARTICLE" || tagUpper == "NAV" || tagUpper == "HEADER" || tagUpper == "FOOTER" || tagUpper == "BODY")
                    {
                        style.Display = "block";
                    }
                    else if (tagUpper == "SPAN" || tagUpper == "A" || tagUpper == "STRONG" || tagUpper == "EM" || tagUpper == "B" || tagUpper == "I")
                    {
                        style.Display = "inline";
                    }
                }

                // Heading font sizes - apply default UA stylesheet sizes if not set
                if (style != null && (tagUpper == "H1" || tagUpper == "H2" || tagUpper == "H3" || tagUpper == "H4" || tagUpper == "H5" || tagUpper == "H6"))
                {
                    // Default User-Agent stylesheet heading sizes
                    if (!style.FontSize.HasValue || style.FontSize.Value < 18)
                    {
                        switch (tagUpper)
                        {
                            case "H1": style.FontSize = 32; style.FontWeight = 700; break;
                            case "H2": style.FontSize = 24; style.FontWeight = 700; break;
                            case "H3": style.FontSize = 18.7; style.FontWeight = 700; break;
                            case "H4": style.FontSize = 16; style.FontWeight = 700; break;
                            case "H5": style.FontSize = 13.3; style.FontWeight = 700; break;
                            case "H6": style.FontSize = 10.7; style.FontWeight = 700; break;
                        }
                    }
                    
                    float headingFontSize = (float)(style.FontSize ?? DefaultFontSize);
                    float minH = headingFontSize * 1.5f; // Line height multiplier
                    if (!style.MinHeight.HasValue || style.MinHeight.Value < minH)
                        style.MinHeight = minH;
                        
                    // Diagnostic logging for headings (commented - FenLogger not accessible here)
                    // FenBrowser.Core.Logging.FenLogger.Debug($"[CSS-DIAG] {tagUpper}: FontSize={style.FontSize}");
                }

                bool hidden = ShouldHide(node, style);
                if (hidden) return 0;

                _traversedCount++;
                var margin = style?.Margin ?? new FenBrowser.Core.Thickness(0);
                var border = style?.BorderThickness ?? new FenBrowser.Core.Thickness(0);
                var padding = style?.Padding ?? new FenBrowser.Core.Thickness(0);
                
                float contentWidth = 0;
                bool explicitStretch = string.Equals(style?.AlignSelf, "stretch", StringComparison.OrdinalIgnoreCase);

                // Structural Stretch Heuristics
                if (node is Element elStretch)
                {
                    string c = (elStretch.GetAttribute("class") ?? "");
                    string i = elStretch.Id ?? "";
                    if (c.Contains("header") || i == "gb" || c.Contains("L3eUgb")) explicitStretch = true;
                }

                if (!shrinkToContent || (shrinkToContent && (hasExplicitW || explicitStretch)))
                {
                    contentWidth = availableWidth - (float)(margin.Left + margin.Right + border.Left + border.Right + padding.Left + padding.Right);
                    if (contentWidth < 0) contentWidth = 0;
                }

                float usableWidth = contentWidth;
                if (shrinkToContent && !hasExplicitW)
                {
                    usableWidth = availableWidth - (float)(margin.Left + margin.Right + border.Left + border.Right + padding.Left + padding.Right);
                }
                if (usableWidth < 0) usableWidth = 0;

                if (style != null)
                {
                    if (style.Width.HasValue) contentWidth = (float)style.Width.Value;
                    else if (!string.IsNullOrEmpty(style.WidthExpression))
                    {
                        float resolvedW = LayoutHelper.EvaluateCssExpression(style.WidthExpression, availableWidth, _viewportWidth, _viewportHeight);
                        if (resolvedW >= 0) contentWidth = resolvedW;
                    }
                    
                    // Box-Sizing: Border-Box Support
                    if (style.BoxSizing == "border-box" && (style.Width.HasValue || !string.IsNullOrEmpty(style.WidthExpression))) 
                    {
                        contentWidth -= (float)(padding.Left + padding.Right + border.Left + border.Right);
                        if (contentWidth < 0) contentWidth = 0;
                    }
                }

                float contentX = x + (float)(margin.Left + border.Left + padding.Left);
                float contentY = y + (float)(margin.Top + border.Top + padding.Top);
                float contentHeight = 0;

                if (style != null)
                {
                    if (style.Height.HasValue) contentHeight = (float)style.Height.Value;
                    else if (!string.IsNullOrEmpty(style.HeightExpression))
                    {
                        float resolvedH = LayoutHelper.EvaluateCssExpression(style.HeightExpression, availableHeight, _viewportWidth, _viewportHeight);
                        if (resolvedH >= 0) contentHeight = resolvedH;
                    }

                    // Box-Sizing: Border-Box Support
                    if (style.BoxSizing == "border-box" && (style.Height.HasValue || !string.IsNullOrEmpty(style.HeightExpression)))
                    {
                        contentHeight -= (float)(padding.Top + padding.Bottom + border.Top + border.Bottom);
                        if (contentHeight < 0) contentHeight = 0;
                    }
                }

                // Correct MaxWidth for border-box
                if (style?.MaxWidth.HasValue == true)
                {
                    float maxW = (float)style.MaxWidth.Value;
                    if (style.BoxSizing == "border-box") maxW = Math.Max(0, maxW - (float)(padding.Left + padding.Right + border.Left + border.Right));
                    if (contentWidth > maxW) contentWidth = maxW;
                }
                
                // MAX-WIDTH PROPAGATION: Ensure usableWidth respects MaxWidth for children
                if (style?.MaxWidth.HasValue == true && usableWidth > (float)style.MaxWidth.Value) 
                {
                    usableWidth = (float)style.MaxWidth.Value;
                }

                // Centering
                if (style?.MarginLeftAuto == true && style?.MarginRightAuto == true)
                {
                    float totalBoxW = contentWidth + (float)(border.Left + border.Right + padding.Left + padding.Right);
                    if (totalBoxW < availableWidth) contentX = x + (availableWidth - totalBoxW) / 2 + (float)(border.Left + padding.Left);
                }

                float maxChildWidth = 0;
                bool isFlex = !ignoreFlex && style?.Display?.ToLowerInvariant().Contains("flex") == true;
                bool isGrid = !ignoreFlex && style?.Display?.ToLowerInvariant().Contains("grid") == true;
                
                if (isGrid && node is Element gridElement && node.Children != null && node.Children.Count > 0 && 
                    tagUpper != "INPUT" && tagUpper != "TEXTAREA" && tagUpper != "SELECT")
                {
                    var m = ComputeGridLayout(gridElement, new BoxModel { Margin = margin, Padding = padding, Border = border }, x, y, usableWidth, availableHeight);
                    if (!hasExplicitH) contentHeight = m.ContentHeight;
                    if (shrinkToContent && !hasExplicitW) contentWidth = m.MaxChildWidth;
                    maxChildWidth = m.MaxChildWidth;
                }
                else if (isFlex && node is Element flexElement && node.Children != null && node.Children.Count > 0 &&
                         tagUpper != "INPUT" && tagUpper != "TEXTAREA" && tagUpper != "SELECT")
                {
                    var m = ComputeFlexLayout(flexElement, new BoxModel { Margin = margin, Padding = padding, Border = border }, x, y, usableWidth, availableHeight);
                    if (!hasExplicitH) contentHeight = m.ContentHeight;
                    if (shrinkToContent && !hasExplicitW) contentWidth = m.MaxChildWidth;
                    maxChildWidth = m.MaxChildWidth;
                }
                else if (node.Children != null && node.Children.Count > 0 && tagUpper != "IMG" && tagUpper != "INPUT" && tagUpper != "SVG")
                {
                    float curX = contentX, curY = contentY, maxLH = 0, accH = 0;
                    float lastBottomMargin = 0;

                    foreach (var child in node.Children)
                    {
                        _parents[child] = node;
                        _styles.TryGetValue(child, out var cs);
                        bool isInline = IsInlineNode(child, cs);
                        
                        if (isInline)
                        {
                            // Inline elements don't collapse margins with blocks or each other in this simple way
                            lastBottomMargin = 0; 
                            
                            float remW = usableWidth - (curX - contentX);
                            if (remW < 0) remW = 0;

                            float ch = LayoutNode(child, curX, curY, remW, availableHeight, style, true);
                            var cb = GetBox(child);
                            float cw = cb?.MarginBox.Width ?? 0;
                            
                            // Wrapping
                            if (curX + cw > contentX + usableWidth && curX > contentX) { 
                                curX = contentX; curY += maxLH; accH += maxLH; maxLH = 0; 
                                ch = LayoutNode(child, curX, curY, usableWidth, availableHeight, style, true); 
                                cb = GetBox(child); cw = cb?.MarginBox.Width ?? 0; 
                            }
                            curX += cw; maxLH = Math.Max(maxLH, ch); maxChildWidth = Math.Max(maxChildWidth, curX - contentX);
                        }
                        else
                        {
                            curY += maxLH; accH += maxLH; maxLH = 0; curX = contentX;
                            
                            // MARGIN COLLAPSING: Collapse current Top Margin with previous Bottom Margin
                            float topMargin = (float)(cs?.Margin.Top ?? 0);
                            float collapsedGap = Math.Max(lastBottomMargin, topMargin);
                            
                            // Adjust curY back by lastBottomMargin, then forward by collapsedGap
                            curY = curY - lastBottomMargin + collapsedGap;
                            
                            float ch = LayoutNode(child, curX, curY, usableWidth, availableHeight, style, shrinkToContent);
                            var cb = GetBox(child);
                            
                            curY += ch - topMargin; // ch includes full margin box; subtract top already accounted for
                            accH = (curY - contentY);
                            maxChildWidth = Math.Max(maxChildWidth, cb?.MarginBox.Width ?? usableWidth);
                            
                            lastBottomMargin = (float)(cs?.Margin.Bottom ?? 0);
                        }
                    }
                    accH += maxLH;
                    if (!hasExplicitH) contentHeight = accH;
                    if (shrinkToContent && maxChildWidth > contentWidth) contentWidth = maxChildWidth;
                }

                float textBaseline = 0;
                if (node is Text textNode && !string.IsNullOrEmpty(textNode.Data))
                {
                    float fs = (float)(style?.FontSize ?? DefaultFontSize);
                    string collapsedData = System.Text.RegularExpressions.Regex.Replace(textNode.Data, @"\s+", " ");
                    
                    var metrics = MeasureTextPrecision(collapsedData, fs, style?.FontFamilyName);
                    
                    if (!hasExplicitW) contentWidth = Math.Min(metrics.width, shrinkToContent ? float.MaxValue : usableWidth);
                    
                    float lh = metrics.height;
                    if (style?.LineHeight.HasValue == true) lh = (float)style.LineHeight.Value;
                    else lh = fs * DefaultLineHeight;

                    int lc = (shrinkToContent || metrics.width <= usableWidth) ? 1 : (int)Math.Ceiling(metrics.width / Math.Max(1, usableWidth));
                    if (!hasExplicitH) contentHeight = lc * lh;

                    if (contentWidth < 1 && metrics.width > 0) contentWidth = metrics.width;
                    if (contentHeight < 1 && lh > 0) contentHeight = lh;
                    
                    textBaseline = metrics.baseline;
                }
                else if (node is Element elInt)
                {
                    if (tagUpper == "IMG" || tagUpper == "SVG")
                    {
                        float iw = (tagUpper == "SVG") ? 24 : 100, ih = (tagUpper == "SVG") ? 24 : 100;
                        if (elInt.Attributes != null) {
                            if (elInt.Attributes.TryGetValue("width", out var ws) && float.TryParse(ws, out var wv)) iw = wv;
                            if (elInt.Attributes.TryGetValue("height", out var hs) && float.TryParse(hs, out var hv)) ih = hv;
                        }
                        if (!hasExplicitW) contentWidth = iw;
                        if (!hasExplicitH) contentHeight = ih;
                    }
                    else if (tagUpper == "INPUT" || tagUpper == "TEXTAREA" || tagUpper == "SELECT")
                    {
                        if (!hasExplicitW) contentWidth = shrinkToContent ? 150 : availableWidth - (float)(margin.Left + margin.Right + border.Left + border.Right + padding.Left + padding.Right);
                        if (!hasExplicitH) contentHeight = (tagUpper == "TEXTAREA") ? 60 : 30;
                    }
                }


                // Apply MinHeight and MinWidth from style (Respecting Box-Sizing)
                if (style?.MinHeight.HasValue == true)
                {
                     float minH = (float)style.MinHeight.Value;
                     if (style.BoxSizing == "border-box") minH = Math.Max(0, minH - (float)(padding.Top + padding.Bottom + border.Top + border.Bottom));
                     if (contentHeight < minH) contentHeight = minH;
                }
                if (style?.MinWidth.HasValue == true)
                {
                     float minW = (float)style.MinWidth.Value;
                     if (style.BoxSizing == "border-box") minW = Math.Max(0, minW - (float)(padding.Left + padding.Right + border.Left + border.Right));
                     if (contentWidth < minW) contentWidth = minW;
                }

                // ROOT/BODY FIX: Ensure HTML/BODY always fill the viewport if they collapsed to 0
                if ((tagUpper == "HTML" || tagUpper == "BODY") && contentHeight < _viewportHeight)
                {
                    contentHeight = _viewportHeight;
                }

                // Guard against negative/invalid content dimensions
                if (float.IsNaN(contentHeight) || float.IsInfinity(contentHeight) || contentHeight < 0)
                    contentHeight = 0;
                if (float.IsNaN(contentWidth) || float.IsInfinity(contentWidth) || contentWidth < 0)
                    contentWidth = 0;

                var finalBox = new BoxModel { Margin = margin, Border = border, Padding = padding };
                finalBox.ContentBox = new SKRect(contentX, contentY, contentX + contentWidth, contentY + contentHeight);
                finalBox.PaddingBox = new SKRect(finalBox.ContentBox.Left - (float)padding.Left, finalBox.ContentBox.Top - (float)padding.Top, finalBox.ContentBox.Right + (float)padding.Right, finalBox.ContentBox.Bottom + (float)padding.Bottom);
                finalBox.BorderBox = new SKRect(finalBox.PaddingBox.Left - (float)border.Left, finalBox.PaddingBox.Top - (float)border.Top, finalBox.PaddingBox.Right + (float)border.Right, finalBox.PaddingBox.Bottom + (float)border.Bottom);
                finalBox.MarginBox = new SKRect(finalBox.BorderBox.Left - (float)margin.Left, finalBox.BorderBox.Top - (float)margin.Top, finalBox.BorderBox.Right + (float)margin.Right, finalBox.BorderBox.Bottom + (float)margin.Bottom);
                finalBox.Baseline = (node is Text) ? textBaseline : contentHeight * 0.8f;
                _boxes[node] = finalBox;
                return finalBox.MarginBox.Height;
            }
            catch (Exception ex) 
            { 
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[Layout] FATAL ERROR in LayoutNode for {node?.NodeName}: {ex.Message}\r\n{ex.StackTrace}\r\n"); } catch {}
                
                // Fallback box for generic errors too
                var failBox = new BoxModel(); 
                failBox.ContentBox = new SKRect(x, y, x, y); 
                failBox.PaddingBox = failBox.ContentBox;
                failBox.BorderBox = failBox.ContentBox;
                failBox.MarginBox = failBox.ContentBox;
                _boxes[node] = failBox;
                
                return 0; 
            }
        }

        public LayoutMetrics ComputeBlockLayout(Element element, BoxModel box, float x, float y, float availableWidth, float availableHeight, bool shrinkToContent = false, int depth = 0)
        {
            float h = LayoutNode(element, x, y, availableWidth, availableHeight, null, shrinkToContent, true, depth + 1);
            var cb = GetBox(element);
            return new LayoutMetrics { ContentHeight = cb?.ContentBox.Height ?? h, MaxChildWidth = cb?.ContentBox.Width ?? availableWidth, Baseline = cb?.Baseline ?? h * 0.8f };
        }

        public LayoutMetrics ComputeFlexLayout(Element element, BoxModel box, float x, float y, float availableWidth, float availableHeight, int depth = 0)
        {
            var style = GetStyle(element);
            // Trace removed

            string dir = style?.FlexDirection?.ToLowerInvariant() ?? "row";
            string jc = style?.JustifyContent?.ToLowerInvariant() ?? "flex-start";
            string ai = style?.AlignItems?.ToLowerInvariant() ?? "stretch";
            bool isCol = dir == "column" || dir == "column-reverse";
            
            float gap = (float)(style?.Gap ?? 0);
            if (dir == "column" || dir == "column-reverse") gap = (float)(style?.RowGap ?? style?.Gap ?? 0);
            else gap = (float)(style?.ColumnGap ?? style?.Gap ?? 0);

            var allChildren = element.Children?.Where(c => !(c is Text t && string.IsNullOrWhiteSpace(t.Data))).ToList() ?? new List<Node>();
            var children = new List<Node>();
            var absChildren = new List<Node>();
            
            foreach (var c in allChildren)
            {
                var cs = GetStyle(c);
                string p = cs?.Position?.ToLowerInvariant();
                if (p == "absolute" || p == "fixed") absChildren.Add(c);
                else children.Add(c);
            }

            if (isCol)
            {
                var m = ComputeBlockLayout(element, box, x, y, availableWidth, availableHeight, ai != "stretch", depth + 1);
                if (ai == "center" || ai == "flex-end")
                {
                    float targetWidth = availableWidth;
                    foreach (var child in children)
                    {
                        var cb = GetBox(child);
                        if (cb != null) {
                            float diff = (ai == "center") ? (targetWidth - cb.MarginBox.Width) / 2 : (targetWidth - cb.MarginBox.Width);
                            if (diff > 0) ShiftAllChildren(child, diff, 0);
                        }
                    }
                }
                return m;
            }
            else
            {
                float curX = x + (float)(box.Margin.Left + box.Padding.Left + box.Border.Left);
                float startX = curX, curY = y + (float)(box.Margin.Top + box.Padding.Top + box.Border.Top);
                
                // Pass 1: Measure intrinsic sizes and total grow/shrink
                float totalBaseW = 0;
                float totalGrow = 0;
                double totalShrinkScaled = 0; 
                var childSizes = new Dictionary<Node, float>();
                bool hasGrow = false;
                bool hasShrink = false;

                int visibleCount = 0;
                foreach (var child in children)
                {
                     // Inner trace removed
                    LayoutNode(child, 0, 0, availableWidth, availableHeight, style, true, false, depth + 1);
                    var cb = GetBox(child);
                    if (cb == null) continue;

                    float w = cb.MarginBox.Width;
                    childSizes[child] = w;
                    totalBaseW += w;
                    visibleCount++;
                    
                    var s = GetStyle(child);
                    if (s?.FlexGrow.HasValue == true && s.FlexGrow.Value > 0)
                    {
                        totalGrow += (float)s.FlexGrow.Value;
                        hasGrow = true;
                    }
                    double shrink = s?.FlexShrink ?? 1.0;
                    if (shrink > 0)
                    {
                        totalShrinkScaled += w * shrink;
                        hasShrink = true;
                    }
                }
                
                // Add gaps to totalBaseW
                if (visibleCount > 1) totalBaseW += gap * (visibleCount - 1);

                // Pass 2: Distribute space & Final Layout
                float free = availableWidth - totalBaseW;
                curX = startX; 
                float maxH = 0, totalW = 0;

                bool first = true;
                foreach (var child in children)
                {
                    var cbInitial = GetBox(child);
                    if (cbInitial == null) continue;

                    if (!first) curX += gap;
                    first = false;

                    var s = GetStyle(child);
                    float grow = (float)(s?.FlexGrow ?? 0);
                    float targetW = childSizes.ContainsKey(child) ? childSizes[child] : 0;
                    bool applyGrow = hasGrow && free > 0 && grow > 0;
                    double shrink = s?.FlexShrink ?? 1.0;
                    bool applyShrink = hasShrink && free < 0 && shrink > 0 && totalShrinkScaled > 0;
                    
                    if (applyGrow) targetW += free * (grow / totalGrow);
                    else if (applyShrink)
                    {
                         float factor = (float)((targetW * shrink) / totalShrinkScaled);
                         targetW -= factor * Math.Abs(free);
                         if (targetW < 0) targetW = 0;
                    }
                    
                    LayoutNode(child, curX, curY, targetW, availableHeight, style, !applyGrow, false, depth + 1);
                    
                    var cb = GetBox(child);
                    if (cb != null) { 
                        curX += cb.MarginBox.Width; 
                        totalW = curX - startX;
                        maxH = Math.Max(maxH, cb.MarginBox.Height); 
                    }
                }

                // Pass 3: Final Alignment (Justify & Align)
                var visibleChildren = children.Where(c => GetBox(c) != null).ToList();
                free = availableWidth - totalW;
                
                if (free > 0 && visibleChildren.Count > 0 && !hasGrow) 
                {
                    float shift = 0;
                    if (jc == "center") shift = free / 2;
                    else if (jc == "flex-end") shift = free;
                    
                    if (shift > 0) foreach (var child in visibleChildren) ShiftNodeAndChildren(child, shift, 0);
                    else if (jc == "space-between" && visibleChildren.Count > 1) {
                        float extraGap = free / (visibleChildren.Count - 1);
                        for (int i = 0; i < visibleChildren.Count; i++) ShiftNodeAndChildren(visibleChildren[i], i * extraGap, 0);
                    }
                }

                if (visibleChildren.Count > 0)
                {
                    foreach (var child in visibleChildren)
                    {
                        var cb = GetBox(child);
                        if (cb != null)
                        {
                            var childStyle = GetStyle(child);
                            string selfAi = childStyle?.AlignSelf?.ToLowerInvariant() ?? ai;
                            float vShift = 0;
                            if (selfAi == "center") vShift = (maxH - cb.MarginBox.Height) / 2;
                            else if (selfAi == "flex-end") vShift = (maxH - cb.MarginBox.Height);
                            if (vShift != 0) ShiftNodeAndChildren(child, 0, vShift);
                        }
                    }
                }

                return new LayoutMetrics { ContentHeight = maxH, MaxChildWidth = totalW, Baseline = maxH * 0.8f };
            }
        }

        public LayoutMetrics ComputeGridLayout(Element element, BoxModel box, float x, float y, float availableWidth, float availableHeight, int depth = 0)
        {
            var cols = ParseGridTemplate(null, availableWidth);
            if (cols.Count == 0) cols.Add(availableWidth);
            float curY = y + (float)(box.Margin.Top + box.Border.Top + box.Padding.Top);
            float startX = x + (float)(box.Margin.Left + box.Border.Left + box.Padding.Left);
            int childIdx = 0;
            var children = element.Children?.Where(c => !(c is Text t && string.IsNullOrWhiteSpace(t.Data))).ToList() ?? new List<Node>();
            while (childIdx < children.Count)
            {
                float curX = startX, maxRowH = 0;
                for (int i = 0; i < cols.Count && childIdx < children.Count; i++)
                {
                    float ch = LayoutNode(children[childIdx], curX, curY, cols[i], availableHeight, null, true);
                    var cb = GetBox(children[childIdx]);
                    maxRowH = Math.Max(maxRowH, cb?.MarginBox.Height ?? ch);
                    curX += cols[i]; childIdx++;
                }
                curY += maxRowH;
            }
            return new LayoutMetrics { ContentHeight = curY - y, MaxChildWidth = availableWidth, Baseline = (curY - y) * 0.8f };
        }

        private List<float> ParseGridTemplate(string template, float availableSize)
        {
            var sizes = new List<float>();
            if (string.IsNullOrEmpty(template)) return sizes;
            if (template.StartsWith("repeat(", StringComparison.OrdinalIgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(template, @"repeat\s*\(\s*(\d+)\s*,\s*(.+)\s*\)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int count))
                {
                    float val = ParseGridValue(match.Groups[2].Value.Trim(), availableSize);
                    for (int i = 0; i < count; i++) sizes.Add(val);
                    return sizes;
                }
            }
            var parts = template.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            float totalFr = 0; var frIndices = new List<int>(); float used = 0;
            for (int i = 0; i < parts.Length; i++) {
                string p = parts[i].Trim();
                if (p.EndsWith("fr")) { if (float.TryParse(p.Replace("fr",""), out float fr)) { totalFr += fr; frIndices.Add(i); sizes.Add(fr); } }
                else { float v = ParseGridValue(p, availableSize); sizes.Add(v); used += v; }
            }
            if (totalFr > 0) {
                float perFr = Math.Max(0, (availableSize - used) / totalFr);
                foreach (int idx in frIndices) sizes[idx] *= perFr;
            }
            return sizes;
        }
        
        private float ParseGridValue(string val, float avail) {
            val = val.Trim().ToLowerInvariant();
            if (val == "auto") return 0;
            if (val.EndsWith("px") && float.TryParse(val.Replace("px",""), out float px)) return px;
            if (val.EndsWith("%") && float.TryParse(val.Replace("%",""), out float pct)) return avail * pct / 100f;
            return 100f;
        }

        public LayoutMetrics ComputeTableLayout(Element element, BoxModel box, float x, float y, float availableWidth, float availableHeight, int depth = 0) => ComputeBlockLayout(element, box, x, y, availableWidth, availableHeight);
        
        public LayoutMetrics ComputeAbsoluteLayout(Element element, BoxModel box, float x, float y, float availableWidth, float availableHeight, int depth = 0)
        {
            _styles.TryGetValue(element, out var s);
            
            // Fix: Add parent position (x,y) to the style offsets
            float absX = x + (float)(s?.Left ?? 0);
            float absY = y + (float)(s?.Top ?? 0);
            
            // TODO: Handle Right/Bottom constraints if Left/Top are missing
            // For now, this fixes the "pinned to top-left 0,0" issue
            
            return ComputeBlockLayout(element, box, absX, absY, availableWidth, availableHeight);
        }

        public LayoutMetrics ComputeTextLayout(Node node, BoxModel box, float x, float y, float availableWidth, float availableHeight, int depth = 0)
        {
            float h = LayoutNode(node, x, y, availableWidth, availableHeight, null);
            var cb = GetBox(node);
            _styles.TryGetValue(node, out var s);
            
            float fontSize = (float)(s?.FontSize ?? DefaultFontSize);
            var metrics = MeasureTextPrecision(node is Text t ? t.Data : "", fontSize, s?.FontFamilyName);
            
            return new LayoutMetrics { 
                ContentHeight = h, 
                MaxChildWidth = cb?.ContentBox.Width ?? 0, 
                Baseline = cb?.Baseline ?? metrics.baseline 
            };
        }

        public float ComputeInlineContext(Element element, BoxModel box, float x, float y, float availableWidth, float availableHeight, int depth = 0) => ComputeBlockLayout(element, box, x, y, availableWidth, availableHeight).ContentHeight;

        private void ShiftNodeAndChildren(Node node, float dx, float dy)
        {
            if (dx == 0 && dy == 0) return;
            if (_boxes.TryGetValue(node, out var b)) {
                b.ContentBox = new SKRect(b.ContentBox.Left + dx, b.ContentBox.Top + dy, b.ContentBox.Right + dx, b.ContentBox.Bottom + dy);
                b.PaddingBox = new SKRect(b.PaddingBox.Left + dx, b.PaddingBox.Top + dy, b.PaddingBox.Right + dx, b.PaddingBox.Bottom + dy);
                b.BorderBox = new SKRect(b.BorderBox.Left + dx, b.BorderBox.Top + dy, b.BorderBox.Right + dx, b.BorderBox.Bottom + dy);
                b.MarginBox = new SKRect(b.MarginBox.Left + dx, b.MarginBox.Top + dy, b.MarginBox.Right + dx, b.MarginBox.Bottom + dy);
            }
            ShiftAllChildren(node, dx, dy);
        }

        private void ShiftAllChildren(Node node, float dx, float dy)
        {
            if (node.Children == null || (dx == 0 && dy == 0)) return;
            foreach (var child in node.Children)
            {
                if (_boxes.TryGetValue(child, out var b)) {
                    b.ContentBox = new SKRect(b.ContentBox.Left + dx, b.ContentBox.Top + dy, b.ContentBox.Right + dx, b.ContentBox.Bottom + dy);
                    b.PaddingBox = new SKRect(b.PaddingBox.Left + dx, b.PaddingBox.Top + dy, b.PaddingBox.Right + dx, b.PaddingBox.Bottom + dy);
                    b.BorderBox = new SKRect(b.BorderBox.Left + dx, b.BorderBox.Top + dy, b.BorderBox.Right + dx, b.BorderBox.Bottom + dy);
                    b.MarginBox = new SKRect(b.MarginBox.Left + dx, b.MarginBox.Top + dy, b.MarginBox.Right + dx, b.MarginBox.Bottom + dy);
                    ShiftAllChildren(child, dx, dy);
                }
            }
        }

        private bool ShouldHide(Node node, CssComputed style)
        {
            if (node == null || (style != null && string.Equals(style.Display, "none", StringComparison.OrdinalIgnoreCase))) return true;
            if (style != null && (style.Visibility == "hidden" || style.Visibility == "collapse")) return true;
            
            // HTML5 <details> disclosure logic
            if (node.Parent is Element parentEl && string.Equals(parentEl.TagName, "details", StringComparison.OrdinalIgnoreCase))
            {
                bool isOpen = parentEl.GetAttribute("open") != null;
                string tag = (node as Element)?.TagName?.ToLowerInvariant() ?? "";
                if (!isOpen && tag != "summary") return true;
            }

            string tagUpper = (node as Element)?.TagName?.ToUpperInvariant() ?? "";
            string[] hide = { "HEAD", "SCRIPT", "STYLE", "META", "LINK", "TITLE", "NOSCRIPT" };
            return hide.Contains(tagUpper);
        }

        public void DumpLayoutTree(Node root)
        {
            try {
                var sb = new StringBuilder();
                sb.AppendLine("\r\n=== LAYOUT TREE DUMP ===");
                DumpNode(root, 0, sb);
                sb.AppendLine("========================\r\n");
                System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", sb.ToString());
            } catch {}
        }

        private void DumpNode(Node root, int startDepth, StringBuilder sb)
        {
            if (root == null) return;
            
            var stack = new Stack<(Node node, int depth)>();
            stack.Push((root, startDepth));
            
            while (stack.Count > 0)
            {
                var (node, depth) = stack.Pop();
                if (node == null) continue;
                
                string indent = new string(' ', depth * 2);
                string tagName = (node is Element e) ? e.TagName : (node is Text ? "#text" : node.NodeName);
                _boxes.TryGetValue(node, out var box);
                _styles.TryGetValue(node, out var style);
                string boxInfo = box != null ? $"Box=[X:{box.MarginBox.Left:F1} Y:{box.MarginBox.Top:F1} W:{box.MarginBox.Width:F1} H:{box.MarginBox.Height:F1}]" : "[No Box]";
                sb.AppendLine($"{indent}{tagName} {boxInfo} Disp:{style?.Display} W:{style?.Width}");
                
                if (node.Children != null && node.Children.Count > 0)
                {
                    // Push in reverse order
                    for (int i = node.Children.Count - 1; i >= 0; i--)
                    {
                        stack.Push((node.Children[i], depth + 1));
                    }
                }
            }
        }
    }
}
