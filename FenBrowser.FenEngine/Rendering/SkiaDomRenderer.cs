using SkiaSharp;
using SkiaSharp.HarfBuzz;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.Text.RegularExpressions;
using System.Linq;
using System.Globalization;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// A new experimental renderer built from scratch using SkiaSharp.
    /// This bypasses Avalonia's high-level layout system to give us pixel-perfect control.
    /// </summary>
    public class InputOverlayData
    {
        public LiteElement Node { get; set; }
        public SKRect Bounds { get; set; }
        public string Type { get; set; }
        public string InitialText { get; set; }
        public string Placeholder { get; set; }  // HTML placeholder attribute
        public List<string> Options { get; set; } = new List<string>();
        public int SelectedIndex { get; set; } = -1;
    }

    /// <summary>
    /// Parsed box-shadow value
    /// </summary>
    public class BoxShadowParsed
    {
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float BlurRadius { get; set; }
        public float SpreadRadius { get; set; }
        public SKColor Color { get; set; } = new SKColor(0, 0, 0, 80);
        public bool Inset { get; set; }
    }

    /// <summary>
    /// Parsed transform value
    /// </summary>
    public class TransformParsed
    {
        public float TranslateX { get; set; }
        public float TranslateY { get; set; }
        public float ScaleX { get; set; } = 1f;
        public float ScaleY { get; set; } = 1f;
        public float Rotate { get; set; } // degrees
        public float SkewX { get; set; }
        public float SkewY { get; set; }
    }

    /// <summary>
    /// Parsed text-decoration value
    /// </summary>
    public class TextDecorationParsed
    {
        public bool Underline { get; set; }
        public bool Overline { get; set; }
        public bool LineThrough { get; set; }
        public SKColor? Color { get; set; }
        public string Style { get; set; } = "solid"; // solid, dashed, dotted, wavy
    }

    public class SkiaDomRenderer
    {
        private const float DefaultFontSize = 16f;
        private const float DefaultLineHeightMultiplier = 1.2f;
        
        public List<InputOverlayData> CurrentOverlays { get; private set; } = new List<InputOverlayData>();

        // Box Model storage
        private class BoxModel
        {
            public SKRect MarginBox;
            public SKRect BorderBox;
            public SKRect PaddingBox;
            public SKRect ContentBox;
            public Avalonia.Thickness Margin;
            public Avalonia.Thickness Border;
            public Avalonia.Thickness Padding;
            public float LineHeight; // Computed line height for text
            public TransformParsed Transform; // Transform for this element
        }

        // Use ConcurrentDictionary for thread safety between render and hit test
        private readonly System.Collections.Concurrent.ConcurrentDictionary<LiteElement, BoxModel> _boxes = new System.Collections.Concurrent.ConcurrentDictionary<LiteElement, BoxModel>();
        private readonly Dictionary<LiteElement, LiteElement> _parents = new Dictionary<LiteElement, LiteElement>(); // Parent map
        private readonly Dictionary<LiteElement, List<TextLine>> _textLines = new Dictionary<LiteElement, List<TextLine>>(); // Text wrapping
        private Dictionary<LiteElement, CssComputed> _styles;
        private string _baseUrl;
        private float _viewportHeight; // Store viewport height for height:100% resolution
        private float _viewportWidth;  // Store viewport width for fixed positioning
        private SKRect _viewport;      // Full viewport rect for position:fixed
        
        // CSS Counters
        private Dictionary<string, int> _counters = new Dictionary<string, int>();

        // Text line for wrapping
        private class TextLine
        {
            public string Text;
            public float Width;
            public float Y;
        }

        public SkiaDomRenderer() { }

        public void Render(LiteElement root, SKCanvas canvas, Dictionary<LiteElement, CssComputed> styles, SKRect viewport, string baseUrl = null, Action<SKSize, List<InputOverlayData>> onLayoutUpdated = null)
        {
            _styles = styles;
            _boxes.Clear(); // ConcurrentDictionary.Clear is thread-safe
            _parents.Clear();
            _textLines.Clear();
            CurrentOverlays.Clear();
            _baseUrl = baseUrl; // Store for relative path resolution
            
            // Store viewport height for height:100% resolution
            _viewportHeight = viewport.Height;
            _viewportWidth = viewport.Width;
            _viewport = viewport;
            if (_viewportHeight <= 0) _viewportHeight = 1080; // Fallback
            if (_viewportWidth <= 0) _viewportWidth = 1920;   // Fallback
            
            // Clear CSS counters for new render
            _counters.Clear();
            
            Console.WriteLine($"[RENDER] viewport.Height={viewport.Height} viewport.Width={viewport.Width} _viewportHeight={_viewportHeight}");
            
            // Draw background only in the strict viewport of the control
            using (var paint = new SKPaint { Color = SKColors.White })
            {
                canvas.DrawRect(viewport, paint);
            }

            if (root == null) return;

            // 1. Layout Pass
            // Use viewport width for layout constraints
            float initialWidth = viewport.Width;
            if (initialWidth <= 0) initialWidth = 1920; // Fallback

            try
            {
                ComputeLayout(root, 0, 0, initialWidth, shrinkToContent: false, availableHeight: _viewportHeight);
                
                // Calculate Total Height
                float totalHeight = 0;
                if (_boxes.TryGetValue(root, out var rootBox))
                {
                    totalHeight = rootBox.MarginBox.Bottom;
                }
                
                // 2. Paint Pass (this is where overlays are collected)
                DrawLayout(root, canvas);
                
                // Invoke callback with Size AND Overlays AFTER DrawLayout populates CurrentOverlays
                var overlaysCopy = new List<InputOverlayData>(CurrentOverlays);
                onLayoutUpdated?.Invoke(new SKSize(initialWidth, totalHeight), overlaysCopy);
            }
            catch (Exception)
            {
                 // Ignore render errors to prevent crash
            }
        }

        // Added shrinkToContent and availableHeight parameters
        private void ComputeLayout(LiteElement node, float x, float y, float availableWidth, bool shrinkToContent = false, float availableHeight = 0)
        {
            // Get styles
            CssComputed style = null;
            if (_styles != null) _styles.TryGetValue(node, out style);

            // Process CSS counters
            ProcessCssCounters(style);

            // VISIBILITY CHECK
            if (ShouldHide(node, style)) return;

            // DEBUG: Trace CENTER element through ComputeLayout
            string nodeTag = node.Tag?.ToUpperInvariant();
            if (nodeTag == "CENTER")
            {
                string nodeClass = node.Attr != null && node.Attr.TryGetValue("class", out var cls) ? cls : "";
                FenLogger.Debug($"[ComputeLayout] CENTER passed visibility check, class='{nodeClass}' children={node.Children?.Count}", LogCategory.Layout);
            }

            // Apply User Agent (UA) styles for inputs if missing
            ApplyUserAgentStyles(node, ref style);

            // Calculate Box Model
            var box = new BoxModel();
            
            // Extract CSS values (default to 0)
            box.Margin = style?.Margin ?? new Avalonia.Thickness(0);
            box.Border = style?.BorderThickness ?? new Avalonia.Thickness(0);
            box.Padding = style?.Padding ?? new Avalonia.Thickness(0);

            // Width calculation
            float marginLeft = (float)box.Margin.Left;
            float marginRight = (float)box.Margin.Right;
            float borderLeft = (float)box.Border.Left;
            float borderRight = (float)box.Border.Right;
            float paddingLeft = (float)box.Padding.Left;
            float paddingRight = (float)box.Padding.Right;

            // Check for margin: auto (horizontal centering)
            bool marginLeftAuto = false, marginRightAuto = false;
            if (style?.Map != null)
            {
                style.Map.TryGetValue("margin-left", out string ml);
                style.Map.TryGetValue("margin-right", out string mr);
                style.Map.TryGetValue("margin", out string m);
                
                marginLeftAuto = ml?.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) == true;
                marginRightAuto = mr?.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) == true;
                
                // Parse margin shorthand
                if (!marginLeftAuto || !marginRightAuto)
                {
                    var parts = (m ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && parts[1].Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        marginLeftAuto = marginRightAuto = true;
                    }
                    else if (parts.Length == 3 && parts[1].Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        marginLeftAuto = marginRightAuto = true;
                    }
                    else if (parts.Length >= 4)
                    {
                        if (parts[1].Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) marginRightAuto = true;
                        if (parts[3].Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) marginLeftAuto = true;
                    }
                }
            }

            // Content width = available - (margin + border + padding)
            float contentWidth = availableWidth - (marginLeft + marginRight + borderLeft + borderRight + paddingLeft + paddingRight);
            if (contentWidth < 0) contentWidth = 0;

            // explicit width override (pixel value)
            bool hasExplicitWidth = style?.Width.HasValue == true;
            if (hasExplicitWidth)
            {
                contentWidth = (float)style.Width.Value;
            }
            // Also handle percentage width (e.g., width: 100%)
            else if (style?.WidthPercent.HasValue == true)
            {
                hasExplicitWidth = true; // Treat percentage as explicit for sizing decisions
                float percentValue = (float)style.WidthPercent.Value;
                // Calculate pixel width from percentage of available content width
                float availableContentWidth = availableWidth - (marginLeft + marginRight + borderLeft + borderRight + paddingLeft + paddingRight);
                contentWidth = (percentValue / 100f) * availableContentWidth;
                if (contentWidth < 0) contentWidth = 0;
            }
            
            // Apply max-width constraint
            bool hasMaxWidth = style?.MaxWidth.HasValue == true;
            if (hasMaxWidth)
            {
                float maxW = (float)style.MaxWidth.Value;
                FenLogger.Debug($"[ComputeLayout] max-width enforcement: tag={nodeTag} maxWidth={maxW} contentWidth(before)={contentWidth}", LogCategory.Layout);
                if (contentWidth > maxW)
                {
                    contentWidth = maxW;
                    FenLogger.Debug($"[ComputeLayout] max-width applied: contentWidth now={contentWidth}", LogCategory.Layout);
                }
            }
            
            // Apply min-width constraint
            if (style?.MinWidth.HasValue == true && contentWidth < (float)style.MinWidth.Value)
            {
                contentWidth = (float)style.MinWidth.Value;
            }

            // Position (relative to parent content box, passed as x,y)
            float currentX = x + marginLeft;
            float currentY = y + (float)box.Margin.Top;
            
            // Apply margin: auto centering (if element has explicit width OR max-width)
            // This allows elements with max-width and margin:0 auto to be centered
            if ((hasExplicitWidth || hasMaxWidth) && marginLeftAuto && marginRightAuto)
            {
                float totalBoxWidth = borderLeft + contentWidth + borderRight + paddingLeft + paddingRight;
                float remainingSpace = availableWidth - totalBoxWidth;
                FenLogger.Debug($"[Layout] margin:auto centering: tag={nodeTag} totalBoxWidth={totalBoxWidth} availableWidth={availableWidth} remainingSpace={remainingSpace}", LogCategory.Layout);
                if (remainingSpace > 0)
                {
                    currentX = x + remainingSpace / 2;
                    FenLogger.Debug($"[Layout] Centered: currentX now={currentX}", LogCategory.Layout);
                }
            }
            else if (nodeTag == "FORM" || nodeTag == "MAIN" || nodeTag == "HEADER")
            {
                // Log why centering wasn't applied
                FenLogger.Debug($"[Layout] margin:auto NOT applied: tag={nodeTag} hasExplicitWidth={hasExplicitWidth} hasMaxWidth={hasMaxWidth} marginLeftAuto={marginLeftAuto} marginRightAuto={marginRightAuto}", LogCategory.Layout);
            }

            // Initialize Boxes (Heights 0 initially)
            // If shrinkToContent is true and no explicit width, we use a temporary infinite width? 
            // Or use available but reset later.
            box.MarginBox = new SKRect(x, y, x + availableWidth, y); 
            box.BorderBox = new SKRect(currentX, currentY, currentX + borderLeft + contentWidth + borderRight, currentY); 
            
            box.PaddingBox = new SKRect(
                box.BorderBox.Left + borderLeft, 
                box.BorderBox.Top + (float)box.Border.Top,
                box.BorderBox.Right - borderRight, 
                box.BorderBox.Top + (float)box.Border.Top);
            
            box.ContentBox = new SKRect(
                box.PaddingBox.Left + paddingLeft,
                box.PaddingBox.Top + (float)box.Padding.Top,
                box.PaddingBox.Right - paddingRight,
                box.PaddingBox.Top + (float)box.Padding.Top);


            // --- LAYOUT CHILDREN ---
            if (node.Children != null)
            {
                foreach(var c in node.Children) _parents[c] = node;
            }

            string display = style?.Display?.ToLowerInvariant();
            
            // DEBUG: Log display for CENTER
            if (nodeTag == "CENTER")
            {
                FenLogger.Debug($"[ComputeLayout] CENTER initial display from CSS: '{display}'", LogCategory.Layout);
            }
            
            // Default display logic (Refactored to Block Whitelist)
            if (string.IsNullOrEmpty(display))
            {
                string tag = node.Tag?.ToUpperInvariant();
                var blocks = new HashSet<string> { 
                    "DIV", "P", "H1", "H2", "H3", "H4", "H5", "H6", 
                    "UL", "OL", "LI", "TR", "TABLE", "BODY", "HTML", 
                    "HEADER", "FOOTER", "NAV", "SECTION", "ARTICLE", "MAIN", 
                    "HR", "PRE", "BLOCKQUOTE", "FORM", "BR", "DL", "DT", "DD", "FIGURE", "FIGCAPTION", "FIELDSET", "DETAILS", "SUMMARY",
                    "CENTER" // CENTER is block-level and centers its content
                };
                
                if (blocks.Contains(tag))
                    display = "block";
                else
                    display = "inline-block"; // Default for SPAN, A, IMG, INPUT, TD, B, I, CODE, etc.
            }
            
            // CENTER element: This is a deprecated HTML element that centers content horizontally
            // CRITICAL FIX: CENTER should NEVER use flex layout - it's a block element with text-align: center
            // Override any CSS that sets display: flex on CENTER
            if (nodeTag == "CENTER")
            {
                if (style == null)
                {
                    style = new CssComputed();
                    _styles[node] = style;
                }
                
                // Get parent and classes for debugging
                string parentTag = node.Parent?.Tag?.ToUpperInvariant() ?? "NONE";
                string nodeClasses = (node as LiteElement)?.GetAttribute("class") ?? "";
                FenLogger.Debug($"[ComputeLayout] CENTER parent={parentTag} classes='{nodeClasses}' original_display='{display}' flexDir='{style.FlexDirection}'", LogCategory.Layout);
                
                // FORCE CENTER to use block layout, NOT flex
                // CENTER is semantically a block element that centers inline content via text-align
                if (display == "flex" || display == "inline-flex")
                {
                    FenLogger.Debug($"[ComputeLayout] CENTER: OVERRIDING display from '{display}' to 'block' - CENTER should NOT be flex container", LogCategory.Layout);
                    display = "block";
                    style.Display = "block";
                    style.FlexDirection = null;  // Clear flex properties
                }
                
                // Set text-align: center for proper content centering
                if (style.TextAlign == null)
                {
                    style.TextAlign = Avalonia.Media.TextAlignment.Center;
                }
            }

            // CRITICAL FIX: Inline/inline-block elements should ALWAYS shrink to content
            // This is the CSS box model behavior - inline elements don't expand to fill available width
            if (display == "inline" || display == "inline-block" || node.IsText)
            {
                shrinkToContent = true;
            }

            // --- REPLACED ELEMENTS SIZE ---
            // nodeTag already declared above
            bool isReplaced = nodeTag == "IMG" || nodeTag == "INPUT" || nodeTag == "BUTTON" || nodeTag == "TEXTAREA" || nodeTag == "SELECT" || nodeTag == "SVG";
            float intrinsicWidth = 0;
            float intrinsicHeight = 0;
            float aspectRatio = 0;

            if (isReplaced)
            {
                if (nodeTag == "INPUT" || nodeTag == "SELECT") {
                    // Check if hidden input - should have zero size
                    string inputType = null;
                    if (nodeTag == "INPUT" && node.Attr != null) 
                    {
                        node.Attr.TryGetValue("type", out inputType);
                        inputType = inputType?.ToLowerInvariant() ?? "text";
                    }
                    
                    if (inputType == "hidden")
                    {
                        intrinsicHeight = 0;
                        intrinsicWidth = 0;
                    }
                    else if (inputType == "submit" || inputType == "button" || inputType == "reset")
                    {
                        // Submit/Button/Reset inputs should size based on their value attribute
                        intrinsicHeight = 30;
                        string btnValue = node.Attr != null && node.Attr.TryGetValue("value", out var v) ? v : "";
                        if (!string.IsNullOrEmpty(btnValue))
                        {
                            using (var paint = new SKPaint { TextSize = style?.FontSize != null ? (float)style.FontSize.Value : DefaultFontSize })
                            {
                                var bounds = new SKRect();
                                paint.MeasureText(btnValue, ref bounds);
                                intrinsicWidth = bounds.Width + 24; // Add padding
                            }
                        }
                        else
                        {
                            intrinsicWidth = 80; // Default for empty button
                        }
                        // Minimum width
                        if (intrinsicWidth < 60) intrinsicWidth = 60;
                    }
                    else
                    {
                        intrinsicHeight = 30;
                        intrinsicWidth = 150;
                    }
                }
                if (nodeTag == "BUTTON") 
                { 
                    intrinsicHeight = 30; 
                    // Calculate button width based on text content
                    string btnText = GetTextContent(node);
                    if (!string.IsNullOrEmpty(btnText))
                    {
                        // FIX: Trim the button text and limit to reasonable length
                        // Some buttons contain lots of hidden content (like Google's search buttons)
                        btnText = btnText.Trim();
                        if (btnText.Length > 100) 
                        {
                            btnText = btnText.Substring(0, 100);
                        }
                        
                        using (var paint = new SKPaint { TextSize = style?.FontSize != null ? (float)style.FontSize.Value : DefaultFontSize })
                        {
                            var bounds = new SKRect();
                            paint.MeasureText(btnText, ref bounds);
                            intrinsicWidth = bounds.Width + 20; // Add padding
                        }
                        
                        // FIX: Cap button width to a reasonable maximum
                        if (intrinsicWidth > 300) 
                        {
                            intrinsicWidth = 300;
                        }
                    }
                    else
                    {
                        intrinsicWidth = 80;
                    }
                }
                if (nodeTag == "TEXTAREA") { intrinsicHeight = 40; intrinsicWidth = 150; }
                if (nodeTag == "SVG")
                {
                    // Parse width/height from attributes
                    intrinsicWidth = 100; // Default
                    intrinsicHeight = 100;
                    if (node.Attr != null)
                    {
                        if (node.Attr.TryGetValue("width", out var w)) float.TryParse(w, out intrinsicWidth);
                        if (node.Attr.TryGetValue("height", out var h)) float.TryParse(h, out intrinsicHeight);
                    }
                }
                if (nodeTag == "IMG") 
                {
                    intrinsicHeight = 50; 
                    intrinsicWidth = 50; 
                    
                    // Attempt to fetch actual size from cache
                    string src = node.Attr?.ContainsKey("src") == true ? node.Attr["src"] : null;
                    string originalSrc = src; // Keep original for logging
                    if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(_baseUrl))
                    {
                         try 
                         {
                             // Simple resolution (mirrors DrawLayout logic)
                             if (!src.StartsWith("http") && !src.StartsWith("data:"))
                             {
                                 var baseUri = new Uri(_baseUrl);
                                 var resolved = new Uri(baseUri, src);
                                 src = resolved.AbsoluteUri;
                                 
                                 // Debug: Log relative URL resolution
                                 try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", 
                                     $"[SkiaRenderer] IMG URL resolved: '{originalSrc}' + base '{_baseUrl}' => '{src}'\r\n"); } catch {}
                             }
                             
                             var bmp = ImageLoader.GetImage(src);
                             if (bmp != null)
                             {
                                 intrinsicWidth = bmp.Width;
                                 intrinsicHeight = bmp.Height;
                             }
                         }
                         catch {}
                    }
                }
                
                if (intrinsicHeight > 0 && intrinsicWidth > 0)
                    aspectRatio = intrinsicWidth / intrinsicHeight;
            }

            // Determine Content Width
            // If explicit width, use it.
            // If replaced element and auto width, use intrinsic.
            // otherwise use available (block) or 0 (inline?) - wait block uses available.
            
            if (!hasExplicitWidth)
            {
                if (isReplaced)
                {
                    // FIX: Replaced elements default to intrinsic width.
                    // If intrinsic is 0 (loading), we use 0 (or updated intrinsic), NOT availableWidth.
                    // This prevents images stretching to full screen width before load.
                    contentWidth = (intrinsicWidth > 0) ? intrinsicWidth : 0;
                    
                    if (contentWidth > availableWidth) contentWidth = availableWidth;
                    
                    FenLogger.Debug($"[ComputeLayout] Replaced element {nodeTag}: intrinsicWidth={intrinsicWidth} contentWidth={contentWidth} availableWidth={availableWidth}", LogCategory.Layout);
                    
                    // FIX: Rebuild boxes with the corrected intrinsic width
                    // Without this, MarginBox stays at availableWidth (full screen width)
                    float totalWidth = marginLeft + borderLeft + paddingLeft + contentWidth + paddingRight + borderRight + marginRight;
                    box.MarginBox = new SKRect(x, y, x + totalWidth, y);
                    box.BorderBox = new SKRect(currentX, currentY, currentX + borderLeft + paddingLeft + contentWidth + paddingRight + borderRight, currentY);
                    box.PaddingBox = new SKRect(
                        box.BorderBox.Left + borderLeft,
                        box.BorderBox.Top + (float)box.Border.Top,
                        box.BorderBox.Right - borderRight,
                        box.BorderBox.Top + (float)box.Border.Top);
                    box.ContentBox = new SKRect(
                        box.PaddingBox.Left + paddingLeft,
                        box.PaddingBox.Top + (float)box.Padding.Top,
                        box.PaddingBox.Right - paddingRight,
                        box.PaddingBox.Top + (float)box.Padding.Top);
                }
                else if (display == "inline" || display == "inline-block")
                {
                     // Inline elements should shrink to content, not take full available width
                     // We start with available width but will shrink after measuring children
                     // The key is that contentWidth is a MAX constraint, not the actual width
                     // After layout, we use maxChildWidth to determine actual size
                }
            }
            
            float contentHeight = 0;
            float maxChildWidth = 0;

            // Calculate container height for flex-grow (only for explicit heights, not percentage)
            float flexContainerHeight = 0;
            if (style?.Height.HasValue == true)
            {
                flexContainerHeight = (float)style.Height.Value;
            }
            // For height:100%, only apply if this is the root flex container (class L3eUgb)
            else if (style?.HeightPercent.HasValue == true && node.Attr != null && node.Attr.TryGetValue("class", out var classAttr) && classAttr.Contains("L3eUgb"))
            {
                float percentValue = (float)style.HeightPercent.Value;
                flexContainerHeight = _viewportHeight * (percentValue / 100f);
            }

            if (display == "flex" || display == "inline-flex")
            {
                contentHeight = ComputeFlexLayout(node, box.ContentBox, style, out maxChildWidth, flexContainerHeight);
            }
            else if (display == "grid" || display == "inline-grid")
            {
                contentHeight = ComputeGridLayout(node, box.ContentBox, style, out maxChildWidth);
            }
            else if (display == "table" || node.Tag?.ToUpperInvariant() == "TABLE")
            {
                contentHeight = ComputeTableLayout(node, box.ContentBox, style, out maxChildWidth);
            }
            else
            {
                // Pass the potentially constrained contentWidth (e.g. for IMG resizing)
                // If IMG, it has no children, so this returns 0 height usually.
                
                // DEBUG: Log when CENTER is about to enter ComputeBlockLayout
                if (nodeTag == "CENTER")
                {
                    FenLogger.Debug($"[ComputeLayout] CENTER about to call ComputeBlockLayout, display={display}", LogCategory.Layout);
                }
                
                contentHeight = ComputeBlockLayout(node, box.ContentBox, contentWidth, out maxChildWidth);
            }
            
            // For inline/inline-block without explicit width, shrink to content
            // ALSO shrink when shrinkToContent is true (used by flex layout for flex items)
            bool shouldShrinkToContent = shrinkToContent || display == "inline" || display == "inline-block";
            if (!hasExplicitWidth && !isReplaced && shouldShrinkToContent)
            {
                
                if (maxChildWidth > 0 && maxChildWidth < contentWidth)
                {
                    contentWidth = maxChildWidth;
                    
                    // Rebuild boxes with new width
                    float totalWidth = marginLeft + borderLeft + paddingLeft + contentWidth + paddingRight + borderRight + marginRight;
                    box.MarginBox = new SKRect(x, y, x + totalWidth, y);
                    box.BorderBox = new SKRect(currentX, currentY, currentX + borderLeft + paddingLeft + contentWidth + paddingRight + borderRight, currentY);
                    box.PaddingBox = new SKRect(
                        box.BorderBox.Left + borderLeft,
                        box.BorderBox.Top + (float)box.Border.Top,
                        box.BorderBox.Right - borderRight,
                        box.BorderBox.Top + (float)box.Border.Top);
                    box.ContentBox = new SKRect(
                        box.PaddingBox.Left + paddingLeft,
                        box.PaddingBox.Top + (float)box.Padding.Top,
                        box.PaddingBox.Right - paddingRight,
                        box.PaddingBox.Top + (float)box.Padding.Top);
                }
            }

            // Fix Height for Replaced Elements with Aspect Ratio preservation
            if (isReplaced)
            {
                // For form elements (INPUT, TEXTAREA, SELECT), use intrinsic height 
                // unless an explicit PIXEL height is set in CSS
                // Percentage heights should NOT stretch these elements to container height
                bool isFormElement = nodeTag == "INPUT" || nodeTag == "TEXTAREA" || nodeTag == "SELECT";
                
                // If CSS didn't set an explicit pixel height
                if (!style?.Height.HasValue == true)
                {
                    if (isFormElement && intrinsicHeight > 0)
                    {
                        // Form elements use their intrinsic height regardless of percentage heights
                        contentHeight = intrinsicHeight;
                    }
                    // If we have an aspect ratio and a determinstic width (either explicit or intrinsic limited by max)
                    else if (aspectRatio > 0 && contentWidth > 0)
                    {
                        contentHeight = contentWidth / aspectRatio;
                    }
                    else if (intrinsicHeight > 0)
                    {
                        contentHeight = intrinsicHeight;
                    }
                }
                
                // Update Max Child Width reported
                 if (maxChildWidth < contentWidth) maxChildWidth = contentWidth;
            }

            // TEXT CONTENT
            if (node.IsText)
            {
                // Get white-space and word-wrap from parent
                var textParent = node;
                _parents.TryGetValue(node, out textParent);
                CssComputed parentStyle = null;
                if (textParent != null && _styles != null) _styles.TryGetValue(textParent, out parentStyle);
                
                string whiteSpace = parentStyle?.WhiteSpace?.ToLowerInvariant() ?? "normal";
                bool shouldWrap = whiteSpace != "nowrap" && whiteSpace != "pre";
                
                // Measure text with proper wrapping
                using (var paint = new SKPaint())
                {
                    float fontSize = style?.FontSize != null ? (float)style.FontSize.Value : DefaultFontSize;
                    paint.TextSize = fontSize;
                    
                    var text = node.Text ?? "";
                    float textHeight = 0;
                    float maxTextWidth = 0;
                    
                    // Get line-height
                    float lineHeight = fontSize * DefaultLineHeightMultiplier;
                    if (style?.LineHeight.HasValue == true)
                    {
                        var lh = style.LineHeight.Value;
                        if (lh > 3) lineHeight = (float)lh; // Pixel value
                        else lineHeight = fontSize * (float)lh; // Multiplier
                    }
                    else if (parentStyle?.LineHeight.HasValue == true)
                    {
                        var lh = parentStyle.LineHeight.Value;
                        if (lh > 3) lineHeight = (float)lh;
                        else lineHeight = fontSize * (float)lh;
                    }
                    
                    try
                    {
                        string ff = style?.FontFamily?.ToString() ?? parentStyle?.FontFamily?.ToString();
                        paint.Typeface = ResolveTypeface(ff, text);
                        
                        if (shouldWrap && contentWidth > 0)
                        {
                            // Word wrap the text with hyphens support
                            string hyphens = parentStyle?.Hyphens?.ToLowerInvariant() ?? "none";
                            var lines = WrapText(text, paint, contentWidth, whiteSpace, hyphens);
                            _textLines[node] = lines;
                            
                            foreach (var line in lines)
                            {
                                textHeight += lineHeight;
                                if (line.Width > maxTextWidth) maxTextWidth = line.Width;
                            }
                            
                            if (lines.Count == 0) textHeight = lineHeight;
                        }
                        else
                        {
                            // No wrapping - single line
                            using (var shaper = new SKShaper(paint.Typeface))
                            {
                                var result = shaper.Shape(text, paint);
                                maxTextWidth = result.Width;
                                var metrics = paint.FontMetrics;
                                textHeight = lineHeight;
                            }
                            
                            _textLines[node] = new List<TextLine> { new TextLine { Text = text, Width = maxTextWidth, Y = 0 } };
                        }
                    }
                    catch (Exception ex)
                    {
                        System.IO.File.AppendAllText("debug_log.txt", $"HarfBuzz Measure Error: {ex.Message}\n");
                        
                        // Fallback measurement
                        var bounds = new SKRect();
                        paint.MeasureText(text, ref bounds);
                        maxTextWidth = bounds.Width;
                        textHeight = lineHeight;
                        _textLines[node] = new List<TextLine> { new TextLine { Text = text, Width = maxTextWidth, Y = 0 } };
                    }

                    textHeight += 5; // Add buffer 
                    contentHeight += textHeight;
                    
                    // Always track text width for inline shrinking
                    if (maxTextWidth > maxChildWidth)
                        maxChildWidth = maxTextWidth + 2;
                    
                    // If text width > contentWidth, expand
                    if (shrinkToContent && !hasExplicitWidth)
                    {
                        if (maxTextWidth > contentWidth) 
                        {
                            contentWidth = maxTextWidth + 2;
                            
                            box.BorderBox = new SKRect(currentX, currentY, currentX + borderLeft + contentWidth + borderRight, currentY); 
                            box.PaddingBox = new SKRect(
                                box.BorderBox.Left + borderLeft, 
                                box.BorderBox.Top + (float)box.Border.Top,
                                box.BorderBox.Right - borderRight, 
                                box.BorderBox.Top + (float)box.Border.Top);
                        
                            box.ContentBox = new SKRect(
                                box.PaddingBox.Left + paddingLeft,
                                box.PaddingBox.Top + (float)box.Padding.Top,
                                box.PaddingBox.Right - paddingRight,
                                box.PaddingBox.Top + (float)box.Padding.Top);
                        }
                    }
                    
                    box.LineHeight = lineHeight;
                    
                    // CRITICAL FIX: Shrink text node box to actual text width
                    // This is essential for inline element width calculation
                    if (shrinkToContent && maxTextWidth > 0)
                    {
                        
                        contentWidth = maxTextWidth + 2; // Small padding
                        
                        // Update boxes to match shrunk width
                        box.BorderBox = new SKRect(currentX, currentY, currentX + borderLeft + contentWidth + borderRight, currentY); 
                        box.PaddingBox = new SKRect(
                            box.BorderBox.Left + borderLeft, 
                            box.BorderBox.Top + (float)box.Border.Top,
                            box.BorderBox.Right - borderRight, 
                            box.BorderBox.Top + (float)box.Border.Top);
                        box.ContentBox = new SKRect(
                            box.PaddingBox.Left + paddingLeft,
                            box.PaddingBox.Top + (float)box.Padding.Top,
                            box.PaddingBox.Right - paddingRight,
                            box.PaddingBox.Top + (float)box.Padding.Top);
                        box.MarginBox = new SKRect(x, y, x + borderLeft + contentWidth + borderRight, y);
                    }
                }
            }

            // Finalize Heights
            if (style?.Height.HasValue == true) contentHeight = (float)style.Height.Value;
            // Handle height: 100% - only apply for root-level flex containers (L3eUgb class) to avoid breaking nested layouts
            // DISABLED FOR NOW - needs more investigation to work properly with flex-grow
            // else if (style?.HeightPercent.HasValue == true)
            // {
            //     float percentValue = (float)style.HeightPercent.Value;
            //     float resolvedHeight = (availableHeight > 0 ? availableHeight : _viewportHeight) * (percentValue / 100f);
            //     if (resolvedHeight > 0) contentHeight = resolvedHeight;
            // }
            
            // Apply min-height constraint
            if (style?.MinHeight.HasValue == true)
            {
                float minHeight = (float)style.MinHeight.Value;
                if (contentHeight < minHeight)
                {
                    contentHeight = minHeight;
                }
            }
            
            // Apply max-height constraint
            if (style?.MaxHeight.HasValue == true)
            {
                float maxHeight = (float)style.MaxHeight.Value;
                if (contentHeight > maxHeight)
                {
                    contentHeight = maxHeight;
                }
            }

            box.ContentBox.Bottom = box.ContentBox.Top + contentHeight;
            box.PaddingBox.Bottom = box.ContentBox.Bottom + (float)box.Padding.Bottom;
            box.BorderBox.Bottom = box.PaddingBox.Bottom + (float)box.Border.Bottom;
            box.MarginBox.Bottom = box.BorderBox.Bottom + (float)box.Margin.Bottom;
            
            // Ensure MarginBox.Right matches BorderBox for inline elements  
            if (display == "inline" || display == "inline-block")
            {
                box.MarginBox.Right = box.BorderBox.Right + marginRight;
            }

            // Debug log for replaced/overlay elements
            if (isReplaced && (nodeTag == "INPUT" || nodeTag == "TEXTAREA" || nodeTag == "SELECT" || nodeTag == "BUTTON"))
            {
                FenLogger.Debug($"[ComputeLayout] Final box for {nodeTag}: Left={box.MarginBox.Left} Top={box.MarginBox.Top} Width={box.MarginBox.Width} Height={box.MarginBox.Height} contentWidth={contentWidth} intrinsicWidth={intrinsicWidth}", LogCategory.Layout);
            }

            _boxes[node] = box;
        }

        // Float tracking for block layout
        private class FloatRect
        {
            public float Left;
            public float Right;
            public float Top;
            public float Bottom;
            public bool IsLeft; // true = float:left, false = float:right
        }

        private float ComputeBlockLayout(LiteElement node, SKRect contentBox, float availableWidth, out float maxChildWidth)
        {
            float childY = contentBox.Top;
            float startY = childY;
            maxChildWidth = 0;

            // Float tracking
            var leftFloats = new List<FloatRect>();
            var rightFloats = new List<FloatRect>();

            // Get text-align for centering inline elements
            CssComputed nodeStyle = null;
            if (_styles != null) _styles.TryGetValue(node, out nodeStyle);
            string textAlign = nodeStyle?.TextAlign?.ToString()?.ToLowerInvariant() ?? "left";
            
            // DEBUG: Log layout for CENTER and FPdoLc to trace button layout
            string nodeTag = node.Tag?.ToUpperInvariant();
            string nodeClass = node.Attr != null && node.Attr.TryGetValue("class", out var c) ? c : "";
            if (nodeTag == "CENTER" || nodeClass.Contains("FPdoLc"))
            {
                FenLogger.Debug($"[ComputeBlockLayout] Processing {nodeTag} class='{nodeClass}' textAlign={textAlign} children={node.Children?.Count}", LogCategory.Layout);
            }

            // Helper to get available X range at a given Y position, accounting for floats
            Func<float, (float left, float right)> getAvailableRangeAtY = (y) =>
            {
                float left = contentBox.Left;
                float right = contentBox.Right;
                
                foreach (var f in leftFloats)
                {
                    if (y >= f.Top && y < f.Bottom)
                        left = Math.Max(left, f.Right);
                }
                foreach (var f in rightFloats)
                {
                    if (y >= f.Top && y < f.Bottom)
                        right = Math.Min(right, f.Left);
                }
                
                return (left, right);
            };
            
            // Helper to clear floats (get Y position below all floats)
            Func<string, float> getClearY = (clearType) =>
            {
                float clearY = childY;
                if (clearType == "left" || clearType == "both")
                {
                    foreach (var f in leftFloats)
                        clearY = Math.Max(clearY, f.Bottom);
                }
                if (clearType == "right" || clearType == "both")
                {
                    foreach (var f in rightFloats)
                        clearY = Math.Max(clearY, f.Bottom);
                }
                return clearY;
            };

            if (node.Children != null)
            {
                // Inline Formatting Context state - track line items for alignment
                var currentLineItems = new List<(LiteElement element, float width, float height)>();
                float currentLineX = contentBox.Left;
                float currentLineHeight = 0;
                float lineStartY = childY;
                
                // Helper to shift an element and all its children
                Action<LiteElement, float> shiftElementRecursive = null;
                shiftElementRecursive = (elem, shift) =>
                {
                    if (_boxes.TryGetValue(elem, out var box))
                    {
                        box.MarginBox.Left += shift;
                        box.MarginBox.Right += shift;
                        box.BorderBox.Left += shift;
                        box.BorderBox.Right += shift;
                        box.PaddingBox.Left += shift;
                        box.PaddingBox.Right += shift;
                        box.ContentBox.Left += shift;
                        box.ContentBox.Right += shift;
                        _boxes[elem] = box;
                    }
                    
                    // Recursively shift children
                    if (elem.Children != null)
                    {
                        foreach (var child in elem.Children)
                        {
                            shiftElementRecursive(child, shift);
                        }
                    }
                };
                
                // Helper to shift an element and all its children vertically
                Action<LiteElement, float> shiftElementVertical = null;
                shiftElementVertical = (elem, shift) =>
                {
                    if (_boxes.TryGetValue(elem, out var box))
                    {
                        box.MarginBox.Top += shift;
                        box.MarginBox.Bottom += shift;
                        box.BorderBox.Top += shift;
                        box.BorderBox.Bottom += shift;
                        box.PaddingBox.Top += shift;
                        box.PaddingBox.Bottom += shift;
                        box.ContentBox.Top += shift;
                        box.ContentBox.Bottom += shift;
                        _boxes[elem] = box;
                    }
                    
                    // Recursively shift children
                    if (elem.Children != null)
                    {
                        foreach (var child in elem.Children)
                        {
                            shiftElementVertical(child, shift);
                        }
                    }
                };
                
                // Helper to finalize a line with text-align and vertical-align
                Action flushLine = () =>
                {
                    if (currentLineItems.Count == 0) return;
                    
                    float lineWidth = currentLineX - contentBox.Left;
                    float offsetX = 0;
                    
                    if (textAlign == "center")
                        offsetX = (availableWidth - lineWidth) / 2;
                    else if (textAlign == "right")
                        offsetX = availableWidth - lineWidth;
                    
                    // DEBUG: Log flush line for CENTER
                    if (nodeTag == "CENTER")
                    {
                        FenLogger.Debug($"[flushLine] CENTER: textAlign={textAlign} lineWidth={lineWidth} availableWidth={availableWidth} offsetX={offsetX} items={currentLineItems.Count}", LogCategory.Layout);
                    }
                    
                    // Apply horizontal alignment and vertical-align
                    foreach (var item in currentLineItems)
                    {
                        // Horizontal text-align
                        if (offsetX > 0)
                        {
                            shiftElementRecursive(item.element, offsetX);
                        }
                        
                        // Vertical-align within line box
                        CssComputed itemStyle = null;
                        if (_styles != null) _styles.TryGetValue(item.element, out itemStyle);
                        string vAlign = itemStyle?.VerticalAlign?.ToLowerInvariant() ?? "baseline";
                        
                        float verticalShift = 0;
                        float itemHeight = item.height;
                        
                        switch (vAlign)
                        {
                            case "top":
                                // Already aligned to top
                                break;
                            case "bottom":
                                verticalShift = currentLineHeight - itemHeight;
                                break;
                            case "middle":
                                verticalShift = (currentLineHeight - itemHeight) / 2;
                                break;
                            case "text-top":
                                // Approximate as top
                                break;
                            case "text-bottom":
                                verticalShift = currentLineHeight - itemHeight;
                                break;
                            case "super":
                                // Move up by ~1/3 of line height (superscript)
                                verticalShift = -currentLineHeight * 0.33f;
                                break;
                            case "sub":
                                // Move down by ~1/3 of line height (subscript)
                                verticalShift = currentLineHeight * 0.33f;
                                break;
                            case "baseline":
                            default:
                                // Default baseline alignment - align to bottom of line minus descent
                                // For simplicity, align to bottom with slight offset
                                verticalShift = currentLineHeight - itemHeight;
                                break;
                        }
                        
                        if (verticalShift != 0)
                        {
                            shiftElementVertical(item.element, verticalShift);
                        }
                    }
                    
                    currentLineItems.Clear();
                };
                
                // We process children. If child is block, we break line. 
                // If child is inline/inline-block, we add to current line until full.

                foreach (var child in node.Children)
                {
                    // DEBUG: Log children of FPdoLc to trace CENTER
                    if (nodeClass.Contains("FPdoLc"))
                    {
                        string dbgChildTag = child.Tag?.ToUpperInvariant() ?? "(TEXT)";
                        FenLogger.Debug($"[ComputeBlockLayout] FPdoLc child: tag={dbgChildTag}", LogCategory.Layout);
                    }
                    
                    // Skip hidden inputs entirely - they shouldn't take any space in layout
                    if (child.Tag?.ToUpperInvariant() == "INPUT")
                    {
                        string inputType = child.Attr != null && child.Attr.TryGetValue("type", out var t) ? t : "(none)";
                        string inputName = child.Attr != null && child.Attr.TryGetValue("name", out var n) ? n : "(none)";
                        
                        if (inputType.ToLowerInvariant() == "hidden")
                        {
                            // Skip hidden inputs - don't create box, don't take space
                            continue;
                        }
                        
                        FenLogger.Debug($"[ComputeBlockLayout] Processing INPUT type={inputType} name={inputName} from parent={node.Tag}", LogCategory.Layout);
                    }
                    
                    // DETAILS/SUMMARY Logic
                    if (node.Tag?.ToUpperInvariant() == "DETAILS" && !node.Attr.ContainsKey("open"))
                    {
                        if (child.Tag?.ToUpperInvariant() != "SUMMARY") continue;
                    }

                    // Check absolute/fixed positioning
                    CssComputed childStylePos = null;
                    if (_styles != null) _styles.TryGetValue(child, out childStylePos);
                    string posVal = childStylePos?.Position?.ToLowerInvariant();
                    if (posVal == "absolute" || posVal == "fixed")
                    {
                        SKRect posContext = posVal == "fixed" ? _viewport : contentBox;
                        ComputeAbsoluteLayout(child, posContext);
                        continue;
                    }
                    
                    // Check for clear property
                    string clearVal = childStylePos?.Map?.ContainsKey("clear") == true 
                        ? childStylePos.Map["clear"]?.ToLowerInvariant() : null;
                    if (!string.IsNullOrEmpty(clearVal) && clearVal != "none")
                    {
                        childY = getClearY(clearVal);
                        currentLineX = contentBox.Left;
                        currentLineHeight = 0;
                    }
                    
                    // Check for float property
                    string floatVal = childStylePos?.Float?.ToLowerInvariant();
                    if (floatVal == "left" || floatVal == "right")
                    {
                        // Flush current line first
                        flushLine();
                        
                        // Measure floated element
                        var (rangeLeft, rangeRight) = getAvailableRangeAtY(childY);
                        float floatAvailWidth = rangeRight - rangeLeft;
                        ComputeLayout(child, rangeLeft, childY, floatAvailWidth, shrinkToContent: true);
                        
                        if (_boxes.TryGetValue(child, out var floatBox))
                        {
                            float floatW = floatBox.MarginBox.Width;
                            float floatH = floatBox.MarginBox.Height;
                            
                            // Position the float
                            float floatX;
                            if (floatVal == "left")
                            {
                                floatX = rangeLeft;
                                leftFloats.Add(new FloatRect
                                {
                                    Left = floatX,
                                    Right = floatX + floatW,
                                    Top = childY,
                                    Bottom = childY + floatH,
                                    IsLeft = true
                                });
                            }
                            else // right
                            {
                                floatX = rangeRight - floatW;
                                rightFloats.Add(new FloatRect
                                {
                                    Left = floatX,
                                    Right = rangeRight,
                                    Top = childY,
                                    Bottom = childY + floatH,
                                    IsLeft = false
                                });
                            }
                            
                            // Re-layout at correct position
                            ComputeLayout(child, floatX, childY, floatAvailWidth, shrinkToContent: true);
                            
                            // Update maxChildWidth
                            if (floatBox.MarginBox.Width > maxChildWidth)
                                maxChildWidth = floatBox.MarginBox.Width;
                        }
                        continue;
                    }

                    // Determine display type
                    CssComputed childStyle = childStylePos;
                    
                    // Logic from ComputeLayout default:
                    string childDisplay = childStyle?.Display?.ToLowerInvariant();
                    
                    // FIX: Text nodes MUST always be inline! Override any inherited display
                    // Text nodes can have Tag == null, Tag == "", or Tag == "#text" depending on parser
                    bool isTextNode = string.IsNullOrEmpty(child.Tag) || child.Tag == "#text";
                    if (isTextNode)
                    {
                        childDisplay = "inline-block";
                        // Collapse pure whitespace text nodes (standard HTML behavior)
                        bool preserve = node.Tag?.ToUpperInvariant() == "PRE" || node.Tag?.ToUpperInvariant() == "CODE";
                        if (!preserve && string.IsNullOrWhiteSpace(child.Text)) continue;
                    }
                    
                    if (string.IsNullOrEmpty(childDisplay))
                    {
                        string t = child.Tag?.ToUpperInvariant();
                        
                        // Text nodes already handled above
                        if (t == "IMG" || t=="SPAN" || t == "A" || t == "INPUT" || t == "BUTTON" || t == "SELECT" || t == "TEXTAREA" || t=="B" || t=="STRONG" || t=="I" || t=="EM" || t=="SMALL" || t=="LABEL" || t=="CODE" || t=="TIME" || t=="ABBR" || t=="Q" || t=="VAR" || t=="KBD" || t=="SAMP" || t=="SUB" || t=="SUP" || t=="AUDIO" || t=="VIDEO" || t=="CANVAS" || t=="NOBR" || t=="BR") 
                            childDisplay = "inline-block";
                        else
                            childDisplay = "block";
                    }
                    
                    // FIX: SPAN elements that only contain form elements should be treated as inline
                    // This fixes Google's button wrapper pattern: <span class="lsbb" style="display:block"><input type="submit"></span>
                    // The CSS says display:block but for usability, button wrappers should stay inline
                    if (childDisplay == "block" && child.Tag?.ToUpperInvariant() == "SPAN")
                    {
                        bool onlyContainsFormElements = true;
                        bool hasFormChild = false;
                        if (child.Children != null && child.Children.Count > 0)
                        {
                            foreach (var grandchild in child.Children)
                            {
                                string gt = grandchild.Tag?.ToUpperInvariant();
                                // Allow text nodes, form elements, and nested spans
                                if (string.IsNullOrEmpty(gt) || gt == "#text")
                                    continue;
                                if (gt == "INPUT" || gt == "BUTTON" || gt == "SELECT" || gt == "TEXTAREA" || gt == "SPAN" || gt == "LABEL")
                                {
                                    hasFormChild = true;
                                    continue;
                                }
                                onlyContainsFormElements = false;
                                break;
                            }
                        }
                        if (onlyContainsFormElements && hasFormChild)
                        {
                            childDisplay = "inline-block";
                        }
                    }
                    
                    // NOTE: inline-flex/inline-grid should NOT be converted to inline-block
                    // They participate in inline flow (like inline-block) BUT use flex/grid layout for their own children
                    // The display value is preserved so ComputeLayout can use the correct layout algorithm

                    // FIX: Handle Position: Absolute/Fixed
                    // They should be removed from normal flow
                    string pos = childStyle?.Position?.ToLowerInvariant();
                    bool isAbs = pos == "absolute";
                    bool isFixed = pos == "fixed";
                    
                    if (isAbs || isFixed)
                    {
                        // position:fixed uses viewport, position:absolute uses nearest positioned ancestor (simplified to contentBox)
                        SKRect positioningContext = isFixed ? _viewport : contentBox;
                        ComputeAbsoluteLayout(child, positioningContext);
                        continue; // Skip flow layout
                    }

                    // inline-flex and inline-grid participate in inline flow like inline-block
                    bool isInline = childDisplay == "inline" || childDisplay == "inline-block" || 
                                    childDisplay == "inline-flex" || childDisplay == "inline-grid";
                    
                    // DEBUG: Log display type for INPUTs in CENTER/FPdoLc to trace button layout
                    string childTag = child.Tag?.ToUpperInvariant();
                    if (childTag == "INPUT" && (nodeTag == "CENTER" || nodeClass.Contains("FPdoLc")))
                    {
                        string inputType = child.Attr != null && child.Attr.TryGetValue("type", out var it) ? it : "(none)";
                        string inputVal = child.Attr != null && child.Attr.TryGetValue("value", out var iv) ? iv : "";
                        FenLogger.Debug($"[ComputeBlockLayout] INPUT in {nodeTag}: type={inputType} value='{inputVal}' childDisplay={childDisplay} isInline={isInline}", LogCategory.Layout);
                    }
                    
                    if (isInline)
                    {
                        // Get available range accounting for floats
                        var (rangeLeft, rangeRight) = getAvailableRangeAtY(childY);
                        float lineAvailWidth = rangeRight - rangeLeft;
                        
                        // If currentLineX hasn't been adjusted to float region, do it now
                        if (currentLineX < rangeLeft)
                            currentLineX = rangeLeft;
                        
                        // Measure child first (shrinkToContent = true) to know its width
                        // available width for this item is rest of line? Or full width?
                        // Ideally we measure with infinite width (or full available) then decide to place.
                        
                        // We can compute at (currentLineX, childY) and check width.
                        // FIX: Ensure shrinkToContent is TRUE for inline/inline-block to calculate proper intrinsic size
                        
                        ComputeLayout(child, currentLineX, childY, rangeRight - currentLineX, shrinkToContent: true);
                        
                        if (_boxes.TryGetValue(child, out var childBox))
                        {
                            float childW = childBox.MarginBox.Width;
                            float childH = childBox.MarginBox.Height;
                            
                            // Check for overflow/wrap
                            // Only wrap if we aren't at start of line and it doesn't fit
                            // FIX: Use availableWidth for wrap calculation, not contentBox.Right which may have accumulated positions
                            float wrapCheckWidth = currentLineX - rangeLeft;
                            if (currentLineX > rangeLeft && (wrapCheckWidth + childW) > lineAvailWidth)
                            {
                                // Flush current line with alignment before wrapping
                                flushLine();
                                
                                // Wrap!
                                childY += currentLineHeight;
                                
                                // Recalculate available range at new Y
                                (rangeLeft, rangeRight) = getAvailableRangeAtY(childY);
                                currentLineX = rangeLeft;
                                currentLineHeight = 0;
                                lineStartY = childY;
                                
                                // Re-layout at new position
                                ComputeLayout(child, currentLineX, childY, rangeRight - currentLineX, shrinkToContent: true);
                                if (_boxes.TryGetValue(child, out childBox)) // Refresh box
                                {
                                     childW = childBox.MarginBox.Width;
                                     childH = childBox.MarginBox.Height;
                                }
                            }
                            
                            // Track item for line alignment
                            currentLineItems.Add((child, childW, childH));
                            
                            // Add to line
                            currentLineX += childW;
                            if (childH > currentLineHeight) currentLineHeight = childH;
                            
                            // Track max width
                            float lineWidth = currentLineX - contentBox.Left;
                            if (lineWidth > maxChildWidth) maxChildWidth = lineWidth;
                        }
                    }
                    else // Block
                    {
                        // Flush previous line if any with alignment
                        if (currentLineX > contentBox.Left)
                        {
                             flushLine();
                             childY += currentLineHeight;
                             currentLineX = contentBox.Left;
                             currentLineHeight = 0;
                        }

                        // Get available range accounting for floats
                        var (rangeLeft, rangeRight) = getAvailableRangeAtY(childY);
                        float blockAvailWidth = rangeRight - rangeLeft;

                        // Layout Block - inherit text-align if not explicitly set
                        // This helps center inline content inside nested block elements
                        if (textAlign == "center" && childStyle != null && childStyle.TextAlign == null)
                        {
                            childStyle.TextAlign = Avalonia.Media.TextAlignment.Center;
                        }
                        
                        // Pass shrinkToContent for inline-block elements that happen to be in block path
                        // (shouldn't normally happen, but just in case)
                        bool shouldShrink = childDisplay == "inline-block" || childDisplay == "inline";
                        ComputeLayout(child, rangeLeft, childY, blockAvailWidth, shrinkToContent: shouldShrink);
                        
                        if (_boxes.TryGetValue(child, out var childBox))
                        {
                            childY += childBox.MarginBox.Height;
                            if (childBox.MarginBox.Width > maxChildWidth) maxChildWidth = childBox.MarginBox.Width;
                        }
                    }
                }
                
                // Flush final line with alignment
                flushLine();
                childY += currentLineHeight;
                
                // Ensure childY is below all floats (clearance)
                childY = Math.Max(childY, getClearY("both"));
            }
            return childY - startY;
        }

        private float ComputeFlexLayout(LiteElement node, SKRect contentBox, CssComputed style, out float maxChildWidth, float containerHeight = 0)
        {
            string dir = style?.FlexDirection?.ToLowerInvariant() ?? "row";
            bool isRow = dir.Contains("row");
            bool isReverse = dir.Contains("reverse");
            string justifyContent = style?.JustifyContent?.ToLowerInvariant() ?? "flex-start";
            string alignItems = style?.AlignItems?.ToLowerInvariant() ?? "stretch";
            string alignContent = style?.AlignContent?.ToLowerInvariant() ?? "stretch";
            string flexWrap = style?.FlexWrap?.ToLowerInvariant() ?? "nowrap";
            bool shouldWrap = flexWrap == "wrap" || flexWrap == "wrap-reverse";
            bool wrapReverse = flexWrap == "wrap-reverse";
            
            // DEBUG: Log flex properties for key container elements
            string nodeTag = node.Tag?.ToUpperInvariant();
            string nodeClass = node.Attr != null && node.Attr.TryGetValue("class", out var nc) ? nc : "";
            if (nodeTag == "FORM" || nodeTag == "MAIN" || nodeTag == "HEADER" || nodeTag == "NAV" || 
                nodeClass.Contains("container") || nodeClass.Contains("wrapper") || nodeClass.Contains("layout"))
            {
                FenLogger.Debug($"[FlexLayout] Processing {nodeTag} class='{nodeClass}' " +
                    $"dir={dir} justify={justifyContent} align={alignItems} wrap={flexWrap} " +
                    $"width={contentBox.Width} height={contentBox.Height}", LogCategory.Layout);
            }
            if (nodeTag == "CENTER")
            {
                FenLogger.Debug($"[ComputeFlexLayout] CENTER: justifyContent={justifyContent} alignItems={alignItems} dir={dir} wrap={flexWrap}", LogCategory.Layout);
            }
            
            // Parse gap (supports 'gap', 'row-gap', 'column-gap')
            float gapValue = 0;
            float rowGap = 0;
            if (style?.Gap.HasValue == true) 
            {
                gapValue = (float)style.Gap.Value;
                rowGap = gapValue;
            }
            if (style?.ColumnGap.HasValue == true) gapValue = (float)style.ColumnGap.Value;
            if (style?.RowGap.HasValue == true) rowGap = (float)style.RowGap.Value;
            else if (style?.Map != null)
            {
                string gapStr = null;
                if (style.Map.TryGetValue("gap", out gapStr))
                {
                    gapStr = gapStr.Replace("px", "").Trim();
                    if (float.TryParse(gapStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var g))
                    {
                        gapValue = g;
                        rowGap = g;
                    }
                }
                if (isRow && style.Map.TryGetValue("column-gap", out gapStr))
                {
                    gapStr = gapStr.Replace("px", "").Trim();
                    float.TryParse(gapStr, NumberStyles.Float, CultureInfo.InvariantCulture, out gapValue);
                }
                if (style.Map.TryGetValue("row-gap", out gapStr))
                {
                    gapStr = gapStr.Replace("px", "").Trim();
                    float.TryParse(gapStr, NumberStyles.Float, CultureInfo.InvariantCulture, out rowGap);
                }
            }
            
            maxChildWidth = 0;
            
            float cursorX = contentBox.Left;
            float cursorY = contentBox.Top;
            
            if (node.Children == null) return 0;
            
            // Collect items and measure
            List<LiteElement> flexItems = new List<LiteElement>();
            foreach(var c in node.Children)
            {
                 CssComputed cStyle = null;
                 if (_styles != null) _styles.TryGetValue(c, out cStyle);
                 string cPos = cStyle?.Position?.ToLowerInvariant();
                 if (cPos == "absolute" || cPos == "fixed")
                 {
                     SKRect posCtx = cPos == "fixed" ? _viewport : contentBox;
                     ComputeAbsoluteLayout(c, posCtx);
                 }
                 else
                 {
                     flexItems.Add(c);
                 }
            }

            if (flexItems.Count == 0) return 0;

            // Sort flex items by CSS 'order' property (default is 0)
            flexItems = flexItems.OrderBy(c =>
            {
                CssComputed cStyle = null;
                if (_styles != null) _styles.TryGetValue(c, out cStyle);
                return cStyle?.Order ?? 0;
            }).ToList();

            if (isRow)
            {
                // Flex row layout with wrapping support + flex-grow/flex-shrink
                var lines = new List<List<(LiteElement element, float width, float height, float grow, float shrink, float basis)>>();
                var currentLine = new List<(LiteElement, float, float, float, float, float)>();
                float currentLineWidth = 0;
                
                // First pass: measure all items and organize into lines
                foreach (var child in flexItems)
                {
                    // Get flex properties
                    CssComputed childStyle = null;
                    if (_styles != null) _styles.TryGetValue(child, out childStyle);
                    float grow = (float)(childStyle?.FlexGrow ?? 0);
                    float shrink = (float)(childStyle?.FlexShrink ?? 1); // Default is 1
                    float basis = (float)(childStyle?.FlexBasis ?? -1); // -1 means auto
                    
                    // Measure child
                    ComputeLayout(child, 0, 0, contentBox.Width, shrinkToContent: true);
                    
                    float childWidth = 0, childHeight = 0;
                    if (_boxes.TryGetValue(child, out var childBox))
                    {
                        childWidth = childBox.MarginBox.Width;
                        childHeight = childBox.MarginBox.Height;
                    }
                    
                    // Use flex-basis if set, otherwise use measured width
                    if (basis > 0) childWidth = basis;
                    
                    // Check if we need to wrap
                    // FIX: Also force wrap when items would exceed container width, even with nowrap
                    // This prevents items from being positioned way off-screen
                    if (currentLine.Count > 0)
                    {
                        float testWidth = currentLineWidth + gapValue + childWidth;
                        if (shouldWrap && testWidth > contentBox.Width)
                        {
                            // Standard CSS wrap
                            lines.Add(currentLine);
                            currentLine = new List<(LiteElement, float, float, float, float, float)>();
                            currentLineWidth = 0;
                        }
                        else if (!shouldWrap && testWidth > contentBox.Width * 1.5f)
                        {
                            // Safety wrap: Force wrap if items would go significantly beyond container bounds
                            // This catches cases where hidden elements (like style tags) are flex items
                            // or where elements designed for different layouts would position way off-screen
                            lines.Add(currentLine);
                            currentLine = new List<(LiteElement, float, float, float, float, float)>();
                            currentLineWidth = 0;
                        }
                    }
                    
                    currentLine.Add((child, childWidth, childHeight, grow, shrink, basis));
                    currentLineWidth += (currentLine.Count > 1 ? gapValue : 0) + childWidth;
                }
                
                if (currentLine.Count > 0)
                    lines.Add(currentLine);
                
                // Reverse lines if wrap-reverse
                if (wrapReverse)
                    lines.Reverse();
                
                // Calculate total lines height for align-content
                float totalLinesHeight = 0;
                var lineHeights = new List<float>();
                foreach (var line in lines)
                {
                    float lh = line.Max(item => item.height);
                    lineHeights.Add(lh);
                    totalLinesHeight += lh;
                }
                totalLinesHeight += rowGap * (lines.Count - 1); // Add gaps
                
                // Apply align-content for multi-line flex containers
                float effectiveContainerH = containerHeight > 0 ? containerHeight : contentBox.Height;
                float crossAxisFreeSpace = effectiveContainerH - totalLinesHeight;
                float alignContentStartOffset = 0;
                float alignContentGap = 0;
                
                if (shouldWrap && lines.Count > 1 && crossAxisFreeSpace > 0)
                {
                    switch (alignContent)
                    {
                        case "center":
                            alignContentStartOffset = crossAxisFreeSpace / 2;
                            break;
                        case "flex-end":
                            alignContentStartOffset = crossAxisFreeSpace;
                            break;
                        case "space-between":
                            if (lines.Count > 1)
                                alignContentGap = crossAxisFreeSpace / (lines.Count - 1);
                            break;
                        case "space-around":
                            alignContentGap = crossAxisFreeSpace / lines.Count;
                            alignContentStartOffset = alignContentGap / 2;
                            break;
                        case "space-evenly":
                            alignContentGap = crossAxisFreeSpace / (lines.Count + 1);
                            alignContentStartOffset = alignContentGap;
                            break;
                        case "stretch":
                            // Distribute extra space evenly among lines
                            if (lines.Count > 0)
                            {
                                float extraPerLine = crossAxisFreeSpace / lines.Count;
                                for (int i = 0; i < lineHeights.Count; i++)
                                    lineHeights[i] += extraPerLine;
                            }
                            break;
                        // "flex-start" is default - no offset
                    }
                }
                
                // Second pass: position items with flex-grow/flex-shrink
                float totalHeight = 0;
                float lineY = cursorY + alignContentStartOffset;
                
                for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                {
                    var line = lines[lineIndex];
                    if (isReverse) line.Reverse();
                    
                    // Use pre-calculated line height (may be stretched by align-content)
                    float lineHeight = lineHeights[lineIndex];
                    float lineWidth = line.Sum(item => item.width) + gapValue * (line.Count - 1);
                    
                    // Calculate free space (can be negative if overflow)
                    float freeSpace = contentBox.Width - lineWidth;
                    
                    // Apply flex-grow if there's extra space
                    float totalGrow = line.Sum(item => item.grow);
                    var adjustedWidths = new List<float>();
                    
                    if (freeSpace > 0 && totalGrow > 0)
                    {
                        // Distribute extra space according to flex-grow
                        foreach (var (child, childWidth, childHeight, grow, shrink, basis) in line)
                        {
                            float extraWidth = grow > 0 ? freeSpace * (grow / totalGrow) : 0;
                            adjustedWidths.Add(childWidth + extraWidth);
                        }
                    }
                    else if (freeSpace < 0)
                    {
                        // Apply flex-shrink if there's overflow
                        float totalShrinkWeighted = line.Sum(item => item.shrink * item.width);
                        if (totalShrinkWeighted > 0)
                        {
                            foreach (var (child, childWidth, childHeight, grow, shrink, basis) in line)
                            {
                                float shrinkRatio = (shrink * childWidth) / totalShrinkWeighted;
                                float shrinkAmount = Math.Abs(freeSpace) * shrinkRatio;
                                adjustedWidths.Add(Math.Max(0, childWidth - shrinkAmount));
                            }
                        }
                        else
                        {
                            // No shrinking allowed, use original widths
                            foreach (var item in line)
                                adjustedWidths.Add(item.width);
                        }
                    }
                    else
                    {
                        // No adjustment needed
                        foreach (var item in line)
                            adjustedWidths.Add(item.width);
                    }
                    
                    // Recalculate line width after flex adjustments
                    float adjustedLineWidth = adjustedWidths.Sum() + gapValue * (line.Count - 1);
                    
                    // Calculate justify-content offset based on adjusted widths
                    float remainingSpace = contentBox.Width - adjustedLineWidth;
                    float startOffset = 0;
                    float extraGap = 0;
                    
                    switch (justifyContent)
                    {
                        case "center":
                            startOffset = remainingSpace / 2;
                            break;
                        case "flex-end":
                            startOffset = remainingSpace;
                            break;
                        case "space-between":
                            if (line.Count > 1) extraGap = remainingSpace / (line.Count - 1);
                            break;
                        case "space-around":
                            extraGap = remainingSpace / line.Count;
                            startOffset = extraGap / 2;
                            break;
                        case "space-evenly":
                            extraGap = remainingSpace / (line.Count + 1);
                            startOffset = extraGap;
                            break;
                    }
                    
                    // Position items in this line
                    float itemX = cursorX + startOffset;
                    
                    for (int i = 0; i < line.Count; i++)
                    {
                        var (child, childWidth, childHeight, grow, shrink, basis) = line[i];
                        float finalWidth = adjustedWidths[i];
                        
                        // Get align-self for this specific item
                        CssComputed itemStyle = null;
                        if (_styles != null) _styles.TryGetValue(child, out itemStyle);
                        string itemAlign = !string.IsNullOrEmpty(itemStyle?.AlignSelf) ? itemStyle.AlignSelf : alignItems;
                        
                        // Calculate align-items/align-self offset
                        float itemY = lineY;
                        switch (itemAlign)
                        {
                            case "center":
                                itemY = lineY + (lineHeight - childHeight) / 2;
                                break;
                            case "flex-end":
                                itemY = lineY + lineHeight - childHeight;
                                break;
                            case "baseline":
                                // Simplified: same as flex-start
                                break;
                            case "stretch":
                                // For stretch, we'd need to re-layout with fixed height
                                break;
                        }
                        
                        // Re-layout at final position with adjusted width
                        string childTagDbg = child.Tag?.ToUpperInvariant() ?? "TEXT";
                        if (childTagDbg == "TEXTAREA" || childTagDbg == "INPUT")
                        {
                            FenLogger.Debug($"[ComputeFlexLayout] Row reposition: childTag={childTagDbg} itemX={itemX} itemY={itemY} finalWidth={finalWidth}", LogCategory.Layout);
                        }
                        ComputeLayout(child, itemX, itemY, finalWidth, shrinkToContent: true);
                        
                        itemX += finalWidth + gapValue + extraGap;
                    }
                    
                    if (adjustedLineWidth > maxChildWidth) maxChildWidth = adjustedLineWidth;
                    // Add align-content gap between lines
                    lineY += lineHeight + rowGap + alignContentGap;
                    totalHeight += lineHeight + rowGap + alignContentGap;
                }
                
                return totalHeight;
            }
            else
            {
                // Column Layout with wrapping
                if (shouldWrap)
                {
                    var columns = new List<List<(LiteElement element, float width, float height)>>();
                    var currentColumn = new List<(LiteElement, float, float)>();
                    float currentColumnHeight = 0;
                    
                    foreach (var child in flexItems)
                    {
                        ComputeLayout(child, 0, 0, contentBox.Width, shrinkToContent: true);
                        
                        float childWidth = 0, childHeight = 0;
                        if (_boxes.TryGetValue(child, out var childBox))
                        {
                            childWidth = childBox.MarginBox.Width;
                            childHeight = childBox.MarginBox.Height;
                        }
                        
                        float testHeight = currentColumnHeight + rowGap + childHeight;
                        if (currentColumn.Count > 0 && testHeight > contentBox.Height)
                        {
                            columns.Add(currentColumn);
                            currentColumn = new List<(LiteElement, float, float)>();
                            currentColumnHeight = 0;
                        }
                        
                        currentColumn.Add((child, childWidth, childHeight));
                        currentColumnHeight += (currentColumn.Count > 1 ? rowGap : 0) + childHeight;
                    }
                    
                    if (currentColumn.Count > 0)
                        columns.Add(currentColumn);
                    
                    // Position columns
                    float columnX = cursorX;
                    float maxHeight = 0;
                    
                    foreach (var column in columns)
                    {
                        if (isReverse) column.Reverse();
                        
                        float columnWidth = column.Max(item => item.width);
                        float itemY = cursorY;
                        
                        foreach (var (child, childWidth, childHeight) in column)
                        {
                            // For column layout, align-items controls horizontal positioning
                            float itemX = columnX;
                            switch (alignItems)
                            {
                                case "center":
                                    // Center item horizontally within container
                                    itemX = contentBox.Left + (contentBox.Width - childWidth) / 2;
                                    break;
                                case "flex-end":
                                    // Align to right edge
                                    itemX = contentBox.Right - childWidth;
                                    break;
                                case "stretch":
                                    // For stretch, item width should match container - handled at measurement
                                    break;
                                // flex-start is default - itemX stays at columnX
                            }
                            
                            ComputeLayout(child, itemX, itemY, childWidth, shrinkToContent: true);
                            itemY += childHeight + rowGap;
                        }
                        
                        float colHeight = itemY - cursorY - rowGap;
                        if (colHeight > maxHeight) maxHeight = colHeight;
                        columnX += columnWidth + gapValue;
                    }
                    
                    maxChildWidth = columnX - cursorX - gapValue;
                    return maxHeight;
                }
                else
                {
                    // Simple column layout (no wrap) with FLEX-GROW support for footer positioning
                    float maxWidth = 0;
                    
                    // DEBUG: Log column layout for CENTER
                    if (nodeTag == "CENTER")
                    {
                        FenLogger.Debug($"[ComputeFlexLayout] CENTER column layout: alignItems={alignItems} flexItems={flexItems.Count} contentBox.Width={contentBox.Width}", LogCategory.Layout);
                    }
                    
                    // FIRST PASS: Measure all items and calculate flex-grow totals
                    float totalChildrenHeight = 0;
                    float totalGrow = 0;
                    var childMeasurements = new List<(LiteElement child, float width, float height, float grow)>();
                    
                    foreach (var child in flexItems)
                    {
                        // Get child's flex-grow value
                        CssComputed childStyle = null;
                        if (_styles != null) _styles.TryGetValue(child, out childStyle);
                        float grow = (float)(childStyle?.FlexGrow ?? 0);
                        
                        // Measure child with shrink-to-content
                        string childTag = child.Tag?.ToUpperInvariant() ?? "TEXT";
                        if (childTag == "TEXTAREA" || childTag == "INPUT")
                        {
                            FenLogger.Debug($"[ComputeFlexLayout] Column measurement: childTag={childTag} cursorX={cursorX} contentBox.Left={contentBox.Left} contentBox.Width={contentBox.Width}", LogCategory.Layout);
                        }
                        ComputeLayout(child, cursorX, 0, contentBox.Width, shrinkToContent: true);
                        
                        float childWidth = 0, childHeight = 0;
                        if (_boxes.TryGetValue(child, out var childBox))
                        {
                            childWidth = childBox.MarginBox.Width;
                            childHeight = childBox.MarginBox.Height;
                        }
                        
                        childMeasurements.Add((child, childWidth, childHeight, grow));
                        totalChildrenHeight += childHeight;
                        totalGrow += grow;
                        
                        if (childWidth > maxWidth) maxWidth = childWidth;
                    }
                    
                    // Calculate free space for flex-grow distribution
                    // Use passed containerHeight if available, otherwise contentBox.Height, otherwise viewport
                    float effectiveContainerHeight = containerHeight;
                    if (effectiveContainerHeight <= 0 || float.IsInfinity(effectiveContainerHeight))
                    {
                        effectiveContainerHeight = contentBox.Height;
                    }
                    if (effectiveContainerHeight <= 0 || float.IsInfinity(effectiveContainerHeight))
                    {
                        effectiveContainerHeight = _viewportHeight;
                    }
                    
                    float freeSpace = effectiveContainerHeight - totalChildrenHeight - (rowGap * (flexItems.Count - 1));
                    
                    FenLogger.Debug($"[ComputeFlexLayout] Column flex-grow: effectiveContainerHeight={effectiveContainerHeight} totalChildrenHeight={totalChildrenHeight} freeSpace={freeSpace} totalGrow={totalGrow}", LogCategory.Layout);
                    
                    // SECOND PASS: Position items with flex-grow distribution
                    float itemY = cursorY;
                    float totalHeight = 0;
                    
                    foreach (var (child, childWidth, childHeight, grow) in childMeasurements)
                    {
                        float finalHeight = childHeight;
                        
                        // Apply flex-grow if there's free space and this child wants to grow
                        if (freeSpace > 0 && totalGrow > 0 && grow > 0)
                        {
                            float extraHeight = freeSpace * (grow / totalGrow);
                            finalHeight = childHeight + extraHeight;
                        }
                        
                        // Shift child to final Y position (don't re-layout, just move)
                        if (_boxes.TryGetValue(child, out var childBox))
                        {
                            float currentY = childBox.MarginBox.Top;
                            float deltaY = itemY - currentY;
                            if (Math.Abs(deltaY) > 0.1f)
                            {
                                ShiftTree(child, 0, deltaY);
                            }
                            
                            // If flex-grow applied, extend the box heights
                            if (finalHeight > childHeight + 0.1f)
                            {
                                float heightDelta = finalHeight - childHeight;
                                childBox.ContentBox.Bottom += heightDelta;
                                childBox.PaddingBox.Bottom += heightDelta;
                                childBox.BorderBox.Bottom += heightDelta;
                                childBox.MarginBox.Bottom += heightDelta;
                            }
                        }
                        
                        // Get align-self for this specific item (overrides parent's align-items)
                        CssComputed childStyle = null;
                        if (_styles != null) _styles.TryGetValue(child, out childStyle);
                        string itemAlign = !string.IsNullOrEmpty(childStyle?.AlignSelf) ? childStyle.AlignSelf : alignItems;
                        
                        // Apply horizontal alignment (align-items/align-self in column direction)
                        if (_boxes.TryGetValue(child, out var finalChildBox))
                        {
                            float alignOffset = 0;
                            switch (itemAlign)
                            {
                                case "center":
                                    alignOffset = (contentBox.Width - finalChildBox.MarginBox.Width) / 2;
                                    break;
                                case "flex-end":
                                    alignOffset = contentBox.Width - finalChildBox.MarginBox.Width;
                                    break;
                                case "stretch":
                                    // For stretch, item should expand to fill container width
                                    // This would require re-layout with fixed width
                                    break;
                            }
                            
                            if (alignOffset != 0)
                            {
                                ShiftTree(child, alignOffset, 0);
                            }
                        }
                        
                        itemY += finalHeight + rowGap;
                        totalHeight += finalHeight + rowGap;
                    }
                    
                    maxChildWidth = maxWidth;
                    return totalHeight > 0 ? totalHeight - rowGap : 0;
                }
            }
        }

        /// <summary>
        /// CSS Grid layout implementation
        /// </summary>
        private float ComputeGridLayout(LiteElement node, SKRect contentBox, CssComputed style, out float maxChildWidth)
        {
            maxChildWidth = 0;
            if (node.Children == null || node.Children.Count == 0) return 0;
            
            // Parse grid-template-columns
            var columnWidths = ParseGridTemplate(style?.GridTemplateColumns, contentBox.Width);
            if (columnWidths.Count == 0)
            {
                // Default: auto-fill with minmax
                columnWidths.Add(contentBox.Width);
            }
            
            // Parse gap
            float columnGap = 0, rowGap = 0;
            if (style?.Gap.HasValue == true)
            {
                columnGap = (float)style.Gap.Value;
                rowGap = (float)style.Gap.Value;
            }
            if (style?.ColumnGap.HasValue == true) columnGap = (float)style.ColumnGap.Value;
            if (style?.RowGap.HasValue == true) rowGap = (float)style.RowGap.Value;
            
            // Collect grid items (excluding absolute/fixed positioned)
            var gridItems = new List<LiteElement>();
            foreach (var c in node.Children)
            {
                CssComputed cStyle = null;
                if (_styles != null) _styles.TryGetValue(c, out cStyle);
                string cPos = cStyle?.Position?.ToLowerInvariant();
                if (cPos == "absolute" || cPos == "fixed")
                {
                    SKRect posCtx = cPos == "fixed" ? _viewport : contentBox;
                    ComputeAbsoluteLayout(c, posCtx);
                }
                else
                {
                    gridItems.Add(c);
                }
            }
            
            if (gridItems.Count == 0) return 0;
            
            int numColumns = columnWidths.Count;
            int numRows = (int)Math.Ceiling((double)gridItems.Count / numColumns);
            
            // Measure all items to determine row heights
            var rowHeights = new float[numRows];
            for (int i = 0; i < gridItems.Count; i++)
            {
                int row = i / numColumns;
                int col = i % numColumns;
                
                var child = gridItems[i];
                ComputeLayout(child, 0, 0, columnWidths[col], shrinkToContent: false);
                
                if (_boxes.TryGetValue(child, out var childBox))
                {
                    if (childBox.MarginBox.Height > rowHeights[row])
                        rowHeights[row] = childBox.MarginBox.Height;
                }
            }
            
            // Calculate positions
            var columnStarts = new float[numColumns + 1];
            columnStarts[0] = contentBox.Left;
            for (int i = 0; i < numColumns; i++)
            {
                columnStarts[i + 1] = columnStarts[i] + columnWidths[i] + (i < numColumns - 1 ? columnGap : 0);
            }
            
            var rowStarts = new float[numRows + 1];
            rowStarts[0] = contentBox.Top;
            for (int i = 0; i < numRows; i++)
            {
                rowStarts[i + 1] = rowStarts[i] + rowHeights[i] + (i < numRows - 1 ? rowGap : 0);
            }
            
            // Position items
            for (int i = 0; i < gridItems.Count; i++)
            {
                int row = i / numColumns;
                int col = i % numColumns;
                
                var child = gridItems[i];
                float x = columnStarts[col];
                float y = rowStarts[row];
                float width = columnWidths[col];
                
                ComputeLayout(child, x, y, width, shrinkToContent: false);
            }
            
            // Calculate total dimensions
            maxChildWidth = columnStarts[numColumns] - contentBox.Left;
            float totalHeight = rowStarts[numRows] - contentBox.Top;
            
            return totalHeight;
        }

        /// <summary>
        /// Parse grid-template-columns value
        /// </summary>
        private List<float> ParseGridTemplate(string template, float containerWidth)
        {
            var widths = new List<float>();
            if (string.IsNullOrWhiteSpace(template)) return widths;
            
            // Handle repeat()
            template = ExpandRepeat(template, containerWidth);
            
            // Split by whitespace
            var parts = Regex.Split(template.Trim(), @"\s+");
            
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                
                string p = part.Trim().ToLowerInvariant();
                
                // Handle fr units
                if (p.EndsWith("fr"))
                {
                    if (float.TryParse(p.TrimEnd('f', 'r'), NumberStyles.Float, CultureInfo.InvariantCulture, out float fr))
                    {
                        // For now, treat 1fr as equal portion of remaining space
                        widths.Add(containerWidth / 4); // Simplified
                    }
                }
                // Handle percentage
                else if (p.EndsWith("%"))
                {
                    if (float.TryParse(p.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct))
                    {
                        widths.Add(containerWidth * pct / 100);
                    }
                }
                // Handle px/em/rem
                else if (p.EndsWith("px") || p.EndsWith("em") || p.EndsWith("rem"))
                {
                    widths.Add(ParseCssLength(p));
                }
                // Handle auto
                else if (p == "auto")
                {
                    widths.Add(containerWidth / 4); // Simplified
                }
                // Handle minmax()
                else if (p.StartsWith("minmax("))
                {
                    var match = Regex.Match(p, @"minmax\s*\(\s*([^,]+)\s*,\s*([^)]+)\s*\)");
                    if (match.Success)
                    {
                        // Use the max value for simplicity
                        var maxVal = match.Groups[2].Value.Trim();
                        if (maxVal == "1fr" || maxVal == "auto")
                            widths.Add(containerWidth / 4);
                        else
                            widths.Add(ParseCssLength(maxVal));
                    }
                }
                // Plain number
                else if (float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                {
                    widths.Add(val);
                }
            }
            
            // Distribute fr units properly
            if (widths.Count > 0)
            {
                float totalFixed = widths.Sum();
                float remaining = containerWidth - totalFixed;
                int frCount = widths.Count(w => w == containerWidth / 4);
                
                if (frCount > 0 && remaining > 0)
                {
                    float perFr = remaining / frCount;
                    for (int i = 0; i < widths.Count; i++)
                    {
                        if (widths[i] == containerWidth / 4)
                            widths[i] = perFr;
                    }
                }
            }
            
            return widths;
        }

        /// <summary>
        /// Expand repeat() in grid-template
        /// </summary>
        private string ExpandRepeat(string template, float containerWidth)
        {
            var match = Regex.Match(template, @"repeat\s*\(\s*(\d+|auto-fill|auto-fit)\s*,\s*([^)]+)\s*\)");
            if (!match.Success) return template;
            
            string countStr = match.Groups[1].Value.Trim();
            string value = match.Groups[2].Value.Trim();
            
            int count = 4; // Default
            if (int.TryParse(countStr, out int explicitCount))
            {
                count = explicitCount;
            }
            else if (countStr == "auto-fill" || countStr == "auto-fit")
            {
                // Calculate how many columns fit
                float colWidth = ParseCssLength(value);
                if (colWidth > 0)
                    count = Math.Max(1, (int)(containerWidth / colWidth));
            }
            
            var expanded = string.Join(" ", Enumerable.Repeat(value, count));
            return template.Replace(match.Value, expanded);
        }
        
        private float ComputeTableLayout(LiteElement node, SKRect contentBox, CssComputed style, out float maxChildWidth)
        {
            maxChildWidth = 0;
            float startY = contentBox.Top;
            float currentY = startY;

            // 1. Identify Rows and Cells
            var rows = new List<List<LiteElement>>();
            
            void CollectRows(LiteElement parent) 
            {
                 if (parent.Children == null) return;
                 foreach(var c in parent.Children)
                 {
                     string t = c.Tag?.ToUpperInvariant();
                     if (t == "TR") {
                         var cells = new List<LiteElement>();
                         if (c.Children != null) {
                            foreach(var cell in c.Children) {
                                string ct = cell.Tag?.ToUpperInvariant();
                                if(ct=="TD"||ct=="TH") cells.Add(cell);
                            }
                         }
                         rows.Add(cells);
                     } else if (t == "THEAD" || t == "TBODY" || t == "TFOOT") {
                         CollectRows(c);
                     }
                 }
            }
            CollectRows(node);

            if (rows.Count == 0) return 0;

            // 2. Build Grid Map (Coordinate System)
            var occupied = new HashSet<(int, int)>();
            var cellData = new List<TableGridCell>(); 
            
            int maxCols = 0;
            int currentRowIndex = 0;
            
            foreach(var row in rows)
            {
                int currentColIndex = 0;
                foreach(var cell in row)
                {
                    // Find next available slot
                    while (occupied.Contains((currentRowIndex, currentColIndex)))
                    {
                        currentColIndex++;
                    }

                    // Parse Span
                    int rowspan = 1;
                    int colspan = 1;
                    if (cell.Attr != null)
                    {
                        if (cell.Attr.TryGetValue("rowspan", out var rs)) int.TryParse(rs, out rowspan);
                        if (cell.Attr.TryGetValue("colspan", out var cs)) int.TryParse(cs, out colspan);
                    }
                    if (rowspan < 1) rowspan = 1;
                    if (colspan < 1) colspan = 1;

                    // Mark Occupied
                    for (int r = 0; r < rowspan; r++)
                    {
                        for (int c = 0; c < colspan; c++)
                        {
                            occupied.Add((currentRowIndex + r, currentColIndex + c));
                        }
                    }

                    cellData.Add(new TableGridCell 
                    { 
                        Element = cell, 
                        Row = currentRowIndex, 
                        Col = currentColIndex, 
                        RowSpan = rowspan, 
                        ColSpan = colspan 
                    });

                    if (currentColIndex + colspan > maxCols) maxCols = currentColIndex + colspan;

                    currentColIndex += colspan;
                }
                currentRowIndex++;
            }
            
            // 3. Measure Columns (Intrinsic Widths)
            float[] colWidths = new float[maxCols];
            
            // First pass: Measure 1x1 cells
            foreach(var cd in cellData)
            {
                if (cd.ColSpan == 1)
                {
                    ComputeLayout(cd.Element, 0, 0, 10000, shrinkToContent: true);
                    if (_boxes.TryGetValue(cd.Element, out var box))
                    {
                        if (box.MarginBox.Width > colWidths[cd.Col]) colWidths[cd.Col] = box.MarginBox.Width;
                    }
                }
            }
            
            // Ensure min width
            for(int i=0; i<maxCols; i++) if (colWidths[i] < 10) colWidths[i] = 10;

            // 4. Calculate Column X Positions
            float[] colX = new float[maxCols + 1];
            float cx = contentBox.Left;
            for(int i=0; i<maxCols; i++)
            {
                colX[i] = cx;
                cx += colWidths[i];
            }
            colX[maxCols] = cx; // End position
            
            if (cx - contentBox.Left > maxChildWidth) maxChildWidth = cx - contentBox.Left;

            // 5. Layout Rows & Heights
            float[] rowHeights = new float[currentRowIndex];
            float[] rowY = new float[currentRowIndex + 1];
            
            foreach(var cd in cellData)
            {
                float w = colX[cd.Col + cd.ColSpan] - colX[cd.Col];
                ComputeLayout(cd.Element, 0, 0, w, shrinkToContent: false);
                
                if (_boxes.TryGetValue(cd.Element, out var box))
                {
                     if (cd.RowSpan == 1)
                     {
                         if (box.MarginBox.Height > rowHeights[cd.Row]) rowHeights[cd.Row] = box.MarginBox.Height;
                     }
                }
            }

            // Calculate Y positions
            float cy = startY;
            for(int i=0; i<currentRowIndex; i++)
            {
                rowY[i] = cy;
                if (rowHeights[i] < 20) rowHeights[i] = 20; // Min height
                cy += rowHeights[i];
            }
            rowY[currentRowIndex] = cy;

            // 6. Final Positioning
            foreach(var cd in cellData)
            {
                float x = colX[cd.Col];
                float y = rowY[cd.Row];
                float w = colX[cd.Col + cd.ColSpan] - x;
                float h = rowY[cd.Row + cd.RowSpan] - y; 
                
                ComputeLayout(cd.Element, x, y, w, shrinkToContent: false);
                
                if (_boxes.TryGetValue(cd.Element, out var box))
                {
                    float delta = h - box.MarginBox.Height;
                    if (delta > 0)
                    {
                         box.MarginBox.Bottom += delta;
                         box.BorderBox.Bottom += delta;
                         box.PaddingBox.Bottom += delta;
                         box.ContentBox.Bottom += delta;
                         _boxes[cd.Element] = box;
                    }
                }
            }

            return rowY[currentRowIndex] - startY;
        }

        private void ShiftTree(LiteElement node, float dx, float dy)
        {
             // First, shift the node itself
             if (_boxes.TryGetValue(node, out var b))
             {
                 b.MarginBox.Offset(dx, dy);
                 b.BorderBox.Offset(dx, dy);
                 b.PaddingBox.Offset(dx, dy);
                 b.ContentBox.Offset(dx, dy);
                 _boxes[node] = b;
             }
             
             // Then shift all children recursively
             if (node.Children != null)
             {
                 foreach(var c in node.Children)
                 {
                     ShiftTree(c, dx, dy);
                 }
             }
        }
        
        private class TableGridCell
        {
            public LiteElement Element;
            public int Row;
            public int Col;
            public int RowSpan;
            public int ColSpan;
        }

        private void ComputeAbsoluteLayout(LiteElement node, SKRect containerBox)
        {
            // Position relative to containerBox
            CssComputed style = null;
            if (_styles != null) _styles.TryGetValue(node, out style);
            
            // First, measure the element to know its size (shrinkToContent)
            ComputeLayout(node, 0, 0, containerBox.Width, shrinkToContent: true);
            
            float elemWidth = 0, elemHeight = 0;
            if (_boxes.TryGetValue(node, out var measuredBox))
            {
                elemWidth = measuredBox.MarginBox.Width;
                elemHeight = measuredBox.MarginBox.Height;
            }
            
            float x = containerBox.Left;
            float y = containerBox.Top;
            
            if (style != null)
            {
                // Horizontal positioning
                if (style.Left.HasValue) 
                    x = containerBox.Left + (float)style.Left.Value;
                else if (style.Right.HasValue) 
                    x = containerBox.Right - (float)style.Right.Value - elemWidth;
                
                // Vertical positioning  
                if (style.Top.HasValue) 
                    y = containerBox.Top + (float)style.Top.Value;
                else if (style.Bottom.HasValue)
                    y = containerBox.Bottom - (float)style.Bottom.Value - elemHeight;
            }
            
            // Re-layout at final position
            ComputeLayout(node, x, y, containerBox.Width, shrinkToContent: true); 
        }

        private bool IsAbsolute(LiteElement node)
        {
            if (_styles != null && _styles.TryGetValue(node, out var s))
            {
                return s?.Position == "absolute" || s?.Position == "fixed";
            }
            return false;
        }

        private void DrawLayout(LiteElement node, SKCanvas canvas)
        {
            if (!_boxes.TryGetValue(node, out var box)) return;

            // Capture Inputs for Overlay
            string overlayTag = node.Tag?.ToUpperInvariant();
            bool isOverlay = false;
            string overlayType = "text";
            string overlayValue = "";

            // DEBUG: Log submit button positions at DrawLayout time
            if (overlayTag == "INPUT" && node.Attr != null)
            {
                node.Attr.TryGetValue("type", out var inputType);
                node.Attr.TryGetValue("value", out var inputVal);
                if (inputType?.ToLowerInvariant() == "submit")
                {
                    FenLogger.Debug($"[DrawLayout] Submit button '{inputVal}': box.PaddingBox={box.PaddingBox}", LogCategory.Layout);
                }
            }

            if (overlayTag == "TEXTAREA")
            {
                isOverlay = true;
                string textareaId = node.Attr?.TryGetValue("id", out var tid) == true ? tid : "no-id";
                FenLogger.Debug($"[DrawLayout] TEXTAREA detected id={textareaId}: box.BorderBox={box.BorderBox} Width={box.BorderBox.Width} Height={box.BorderBox.Height}", LogCategory.Layout);
            }
            else if (overlayTag == "BUTTON")
            {
                // HTML BUTTON element - use Avalonia Button overlay
                overlayValue = GetTextContentExcludingStyle(node)?.Trim() ?? ""; // Button label from text content, excluding STYLE elements
                // Skip buttons that contain CSS-like content (likely have STYLE children)
                if (overlayValue.Contains("{") && overlayValue.Contains(":") && overlayValue.Contains("}"))
                {
                    // This looks like CSS content, skip this button overlay
                    isOverlay = false;
                }
                // Skip "AI Mode" button from Google - it's a Google-specific feature that requires complex JS
                else if (overlayValue.ToLowerInvariant().Contains("ai mode"))
                {
                    isOverlay = false;
                }
                else
                {
                    isOverlay = true;
                    overlayType = "button";
                    // Truncate if too long
                    if (overlayValue.Length > 50) overlayValue = overlayValue.Substring(0, 50);
                }
            }
            else if (overlayTag == "INPUT")
            {
                 overlayValue = node.Attr != null && node.Attr.TryGetValue("value", out var v) ? v : "";
                 if (node.Attr != null && node.Attr.TryGetValue("type", out var t)) overlayType = t.ToLowerInvariant();
                 // Checkbox, Radio, Hidden are NOT overlays
                 // Button, Submit, Reset use Avalonia Button overlays for proper click handling
                 if (overlayType != "checkbox" && overlayType != "radio" && overlayType != "hidden")
                 {
                     isOverlay = true;
                 }
            }
            else if (overlayTag == "SELECT")
            {
                isOverlay = true;
                overlayType = "select";
            }
            
            if (isOverlay)
            {
                 // Only if visible and within reasonable viewport bounds
                 // Skip elements positioned way off-screen (e.g., Left > 1920 on typical displays)
                 bool isWithinViewport = box.BorderBox.Left < 1920 && box.BorderBox.Left > -100;
                 if (box.BorderBox.Width > 0 && box.BorderBox.Height > 0 && isWithinViewport)
                 {
                     // FIX: Cap INPUT and TEXTAREA element overlay heights to prevent them from stretching
                     // to viewport height when parent containers have height: 100%
                     // Text inputs should never exceed ~50px in height unless explicitly styled
                     // TEXTAREA used as search boxes (single-line appearance) should also be capped
                     SKRect overlayBounds = box.PaddingBox;
                     if (overlayTag == "INPUT")
                     {
                         // Cap text input height at maximum reasonable value
                         float maxInputHeight = 50f;
                         if (overlayType == "submit" || overlayType == "button" || overlayType == "reset")
                         {
                             maxInputHeight = 60f; // Buttons can be slightly taller
                         }
                         
                         if (overlayBounds.Height > maxInputHeight)
                         {
                             FenLogger.Debug($"[DrawLayout] INPUT height capped from {overlayBounds.Height} to {maxInputHeight}", LogCategory.Layout);
                             overlayBounds = new SKRect(
                                 overlayBounds.Left,
                                 overlayBounds.Top,
                                 overlayBounds.Right,
                                 overlayBounds.Top + maxInputHeight
                             );
                         }
                     }
                     else if (overlayTag == "TEXTAREA")
                     {
                         // TEXTAREA used as search boxes (like Brave Search) can have incorrect heights
                         // Cap at reasonable maximum - real multi-line textareas should have explicit CSS height
                         float maxTextareaHeight = 100f;
                         
                         // If the TEXTAREA is extremely tall (>200px), it's likely a layout bug
                         if (overlayBounds.Height > maxTextareaHeight)
                         {
                             FenLogger.Debug($"[DrawLayout] TEXTAREA height capped from {overlayBounds.Height} to {maxTextareaHeight}", LogCategory.Layout);
                             overlayBounds = new SKRect(
                                 overlayBounds.Left,
                                 overlayBounds.Top,
                                 overlayBounds.Right,
                                 overlayBounds.Top + maxTextareaHeight
                             );
                         }
                     }
                     
                     // Extract placeholder from HTML attribute
                     string overlayPlaceholder = null;
                     if (node.Attr != null)
                     {
                         node.Attr.TryGetValue("placeholder", out overlayPlaceholder);
                         if (string.IsNullOrEmpty(overlayPlaceholder))
                             node.Attr.TryGetValue("aria-label", out overlayPlaceholder);
                     }
                     
                     var overlayData = new InputOverlayData
                     {
                         Node = node,
                         Bounds = overlayBounds,
                         Type = overlayTag == "TEXTAREA" ? "textarea" : overlayType,
                         InitialText = overlayValue,
                         Placeholder = overlayPlaceholder
                     };

                     // Extract Options for Select
                     if (overlayTag == "SELECT" && node.Children != null)
                     {
                         int idx = 0;
                         foreach(var child in node.Children)
                         {
                             if (child.Tag?.ToUpperInvariant() == "OPTION")
                             {
                                 string txt = child.Text ?? "";
                                 overlayData.Options.Add(txt);
                                 if (child.Attr != null && child.Attr.ContainsKey("selected"))
                                 {
                                     overlayData.SelectedIndex = idx;
                                 }
                                 idx++;
                             }
                         }
                         if (overlayData.SelectedIndex == -1 && overlayData.Options.Count > 0) overlayData.SelectedIndex = 0;
                     }

                     // Enhanced logging for debugging overlay containers
                     LiteElement parentNode = null;
                     _parents.TryGetValue(node, out parentNode);
                     string parentTag = parentNode?.Tag ?? "NONE";
                     string parentClass = parentNode?.Attr?.TryGetValue("class", out var pc) == true ? pc : "";
                     
                     // Check if parent has a background color
                     CssComputed parentStyle = null;
                     if (parentNode != null && _styles != null) _styles.TryGetValue(parentNode, out parentStyle);
                     string parentBg = parentStyle?.BackgroundColor.HasValue == true ? parentStyle.BackgroundColor.Value.ToString() : "none";
                     
                     FenLogger.Debug($"[DrawLayout] Adding overlay: type={overlayData.Type} bounds={overlayData.Bounds} parentTag={parentTag} parentClass={parentClass} parentBg={parentBg}", LogCategory.Layout);
                     CurrentOverlays.Add(overlayData);
                 }
                 return; // Skip drawing Skia representation
            }
            
            CssComputed layoutStyle = null;
            if (_styles != null) _styles.TryGetValue(node, out layoutStyle);

            // Check visibility
            string visibility = layoutStyle?.Map?.ContainsKey("visibility") == true ? layoutStyle.Map["visibility"]?.ToLowerInvariant() : null;
            if (visibility == "hidden" || visibility == "collapse")
            {
                // Still take up space but don't render - skip to children
                if (node.Children != null)
                {
                    foreach (var child in node.Children)
                    {
                        DrawLayout(child, canvas);
                    }
                }
                return;
            }

            // Handle position:relative offset
            // For relative positioning, we offset the visual rendering without affecting layout
            bool isRelativePositioned = false;
            float relativeOffsetX = 0f, relativeOffsetY = 0f;
            string positionValue = layoutStyle?.Position?.ToLowerInvariant();
            if (positionValue == "relative")
            {
                isRelativePositioned = true;
                // left overrides right, top overrides bottom
                if (layoutStyle.Left.HasValue)
                    relativeOffsetX = (float)layoutStyle.Left.Value;
                else if (layoutStyle.Right.HasValue)
                    relativeOffsetX = -(float)layoutStyle.Right.Value;
                
                if (layoutStyle.Top.HasValue)
                    relativeOffsetY = (float)layoutStyle.Top.Value;
                else if (layoutStyle.Bottom.HasValue)
                    relativeOffsetY = -(float)layoutStyle.Bottom.Value;
            }

            // Get opacity (default 1.0 = fully opaque)
            float opacity = 1f;
            if (layoutStyle?.Opacity.HasValue == true)
                opacity = (float)layoutStyle.Opacity.Value;

            // Get border-radius (CornerRadius is not nullable, check TopLeft > 0)
            float borderRadius = 0f;
            if (layoutStyle?.BorderRadius.TopLeft > 0)
                borderRadius = (float)layoutStyle.BorderRadius.TopLeft;

            // Parse transform
            TransformParsed transform = null;
            if (!string.IsNullOrEmpty(layoutStyle?.Transform))
            {
                transform = ParseTransform(layoutStyle.Transform);
            }

            // Parse box-shadow
            List<BoxShadowParsed> shadows = null;
            if (!string.IsNullOrEmpty(layoutStyle?.BoxShadow))
            {
                shadows = ParseBoxShadow(layoutStyle.BoxShadow);
            }

            // Parse text-decoration for non-text nodes (for links etc)
            TextDecorationParsed textDeco = null;
            if (!string.IsNullOrEmpty(layoutStyle?.TextDecoration))
            {
                textDeco = ParseTextDecoration(layoutStyle.TextDecoration);
            }

            // Get filter
            string filter = layoutStyle?.Filter;

            // Apply position:relative offset (before transforms)
            if (isRelativePositioned && (relativeOffsetX != 0 || relativeOffsetY != 0))
            {
                canvas.Save();
                canvas.Translate(relativeOffsetX, relativeOffsetY);
            }

            // Apply transform if present
            bool hasTransform = transform != null && (transform.TranslateX != 0 || transform.TranslateY != 0 ||
                                transform.ScaleX != 1 || transform.ScaleY != 1 || transform.Rotate != 0 ||
                                transform.SkewX != 0 || transform.SkewY != 0);
            
            if (hasTransform)
            {
                canvas.Save();
                
                // Apply transforms around the element center
                float cx = box.BorderBox.MidX;
                float cy = box.BorderBox.MidY;
                
                canvas.Translate(cx, cy);
                
                if (transform.Rotate != 0)
                    canvas.RotateDegrees(transform.Rotate);
                
                if (transform.ScaleX != 1 || transform.ScaleY != 1)
                    canvas.Scale(transform.ScaleX, transform.ScaleY);
                
                if (transform.SkewX != 0 || transform.SkewY != 0)
                {
                    var skewMatrix = SKMatrix.CreateSkew((float)Math.Tan(transform.SkewX * Math.PI / 180), 
                                                          (float)Math.Tan(transform.SkewY * Math.PI / 180));
                    canvas.Concat(ref skewMatrix);
                }
                
                canvas.Translate(-cx + transform.TranslateX, -cy + transform.TranslateY);
            }

            // Apply clip-path if present
            bool hasClipPath = !string.IsNullOrEmpty(layoutStyle?.ClipPath) && layoutStyle.ClipPath != "none";
            SKPath clipPathSkia = null;
            if (hasClipPath)
            {
                clipPathSkia = ParseClipPath(layoutStyle.ClipPath, box.BorderBox);
                if (clipPathSkia != null)
                {
                    canvas.Save();
                    canvas.ClipPath(clipPathSkia);
                }
            }

            // 0. Draw Box Shadows (before background)
            if (shadows != null && shadows.Count > 0)
            {
                DrawBoxShadow(canvas, box.BorderBox, borderRadius, shadows, opacity);
            }

            // 1. Draw Background (with opacity, rounded corners, and filter)
            // First try gradient brush, then fall back to solid color
            bool backgroundDrawn = false;
            
            if (layoutStyle?.Background != null)
            {
                var shader = CreateShaderFromBrush(layoutStyle.Background, box.BorderBox, opacity);
                if (shader != null)
                {
                    using (var paint = new SKPaint())
                    {
                        paint.Shader = shader;
                        paint.IsAntialias = true;
                        
                        if (!string.IsNullOrEmpty(filter))
                            ApplyFilter(paint, filter);
                        
                        if (borderRadius > 0)
                            canvas.DrawRoundRect(box.BorderBox, borderRadius, borderRadius, paint);
                        else
                            canvas.DrawRect(box.BorderBox, paint);
                        
                        backgroundDrawn = true;
                    }
                    shader.Dispose();
                }
            }
            
            if (!backgroundDrawn && layoutStyle?.BackgroundColor.HasValue == true)
            {
                var c = layoutStyle.BackgroundColor.Value;
                byte alpha = (byte)(c.A * opacity);
                
                // Debug: Log white/light backgrounds that are large (potential issue indicators)
                bool isLightBackground = c.R > 240 && c.G > 240 && c.B > 240;
                bool isLargeElement = box.BorderBox.Width > 300 && box.BorderBox.Height > 80;
                if (isLightBackground && isLargeElement)
                {
                    string nodeDbgTag = node.Tag ?? "TEXT";
                    string nodeDbgClass = node.Attr?.TryGetValue("class", out var nc) == true ? nc : "";
                    FenLogger.Debug($"[DrawLayout] Large white background: tag={nodeDbgTag} class={nodeDbgClass} color=RGB({c.R},{c.G},{c.B}) box={box.BorderBox}", LogCategory.Layout);
                }
                
                using (var paint = new SKPaint { Color = new SKColor(c.R, c.G, c.B, alpha) })
                {
                    // Apply filter if present
                    if (!string.IsNullOrEmpty(filter))
                        ApplyFilter(paint, filter);
                    
                    if (borderRadius > 0)
                        canvas.DrawRoundRect(box.BorderBox, borderRadius, borderRadius, paint);
                    else
                        canvas.DrawRect(box.BorderBox, paint); 
                }
            }

            // 1.5 Draw inset shadows (after background, inside element)
            if (shadows != null)
            {
                DrawInsetShadow(canvas, box.BorderBox, borderRadius, shadows, opacity);
            }

            // 2. Draw Borders (with opacity, rounded corners, and multiple styles)
            if (box.Border.Left > 0 || box.Border.Top > 0 || box.Border.Right > 0 || box.Border.Bottom > 0)
            {
                DrawStyledBorders(canvas, box, layoutStyle, borderRadius, opacity);
            }

            // 3. Draw Text (with wrapping support)
            if (node.IsText && !string.IsNullOrWhiteSpace(node.Text))
            {
                using (var paint = new SKPaint())
                {
                    float fontSize = layoutStyle?.FontSize != null ? (float)layoutStyle.FontSize.Value : DefaultFontSize;
                    paint.TextSize = fontSize;
                    paint.Color = SKColors.Black; 
                    paint.IsAntialias = true;

                    // Get line height
                    float lineHeight = box.LineHeight > 0 ? box.LineHeight : fontSize * DefaultLineHeightMultiplier;

                    // SAFE ACCESS: Use ForegroundColor struct
                    SKColor textColor = SKColors.Black;
                    if (layoutStyle?.ForegroundColor.HasValue == true)
                    {
                        var c = layoutStyle.ForegroundColor.Value;
                        textColor = new SKColor(c.R, c.G, c.B, c.A);
                        paint.Color = textColor;
                    }
                    else
                    {
                        // Check if parent is link (Default UA style)
                        var parent = GetParent(node);
                        if (parent != null && parent.Tag?.ToUpperInvariant() == "A")
                        {
                            textColor = SKColors.Blue;
                            paint.Color = textColor;
                        }
                    }
                    
                    // Apply opacity
                    textColor = textColor.WithAlpha((byte)(textColor.Alpha * opacity));
                    paint.Color = textColor;
                    
                    // Text Alignment
                    string textAlign = "left";
                    float containerWidth = box.ContentBox.Width;
                    var textParent = GetParent(node);
                    if (textParent != null && _styles != null && _styles.TryGetValue(textParent, out var parentStyle))
                    {
                        if (parentStyle?.TextAlign != null)
                            textAlign = parentStyle.TextAlign.ToString().ToLowerInvariant();
                        containerWidth = _boxes.TryGetValue(textParent, out var parentBox) ? parentBox.ContentBox.Width : containerWidth;
                    }
                    
                    try
                    {
                        string ff = layoutStyle?.FontFamily?.ToString();
                        paint.Typeface = ResolveTypeface(ff, node.Text);
                    }
                    catch { }
                    
                    // Get text decoration from parent
                    TextDecorationParsed deco = null;
                    if (textParent != null && _styles != null && _styles.TryGetValue(textParent, out var parentStyleDeco))
                    {
                        if (!string.IsNullOrEmpty(parentStyleDeco?.TextDecoration))
                            deco = ParseTextDecoration(parentStyleDeco.TextDecoration);
                        
                        // Auto-underline links
                        if (textParent.Tag?.ToUpperInvariant() == "A" && (deco == null || (!deco.Underline && !deco.LineThrough && !deco.Overline)))
                        {
                            deco = new TextDecorationParsed { Underline = true };
                        }
                    }
                    
                    // Get word/letter spacing from style
                    double? wordSpacing = layoutStyle?.WordSpacing;
                    double? letterSpacing = layoutStyle?.LetterSpacing;
                    
                    // Draw text lines (wrapped or single)
                    if (_textLines.TryGetValue(node, out var lines) && lines.Count > 0)
                    {
                        float drawY = box.ContentBox.Top + lineHeight - 5;
                        
                        foreach (var line in lines)
                        {
                            float drawX = box.ContentBox.Left;
                            
                            // Apply alignment with spacing-adjusted width
                            float lineWidthWithSpacing = MeasureTextWithSpacing(paint, line.Text, wordSpacing, letterSpacing);
                            if (textAlign == "center")
                                drawX = box.ContentBox.Left + (containerWidth - lineWidthWithSpacing) / 2;
                            else if (textAlign == "right")
                                drawX = box.ContentBox.Left + containerWidth - lineWidthWithSpacing;
                            
                            // Draw with spacing support
                            DrawTextWithSpacing(canvas, line.Text, drawX, drawY, paint, wordSpacing, letterSpacing);
                            
                            // Draw text decoration for this line
                            if (deco != null && (deco.Underline || deco.Overline || deco.LineThrough))
                            {
                                var lineBox = new SKRect(drawX, drawY - lineHeight + 5, drawX + line.Width, drawY + 5);
                                DrawTextDecoration(canvas, deco, lineBox, fontSize, textColor);
                            }
                            
                            drawY += lineHeight;
                        }
                    }
                    else
                    {
                        // Fallback: single line draw
                        float drawX = box.ContentBox.Left;
                        float actualTextWidth = MeasureTextWithSpacing(paint, node.Text, wordSpacing, letterSpacing);
                        
                        if (textAlign == "center")
                            drawX = box.ContentBox.Left + (containerWidth - actualTextWidth) / 2;
                        else if (textAlign == "right")
                            drawX = box.ContentBox.Left + containerWidth - actualTextWidth;
                        
                        // Draw with spacing support
                        DrawTextWithSpacing(canvas, node.Text, drawX, box.ContentBox.Bottom - 5, paint, wordSpacing, letterSpacing);
                        
                        // Draw text decoration
                        if (deco != null && (deco.Underline || deco.Overline || deco.LineThrough))
                        {
                            DrawTextDecoration(canvas, deco, box.ContentBox, fontSize, textColor);
                        }
                    }
                }
            }
            
            // 3.5 Draw Replaced Elements Helpers
            string tag = node.Tag?.ToUpperInvariant();
            if (tag == "INPUT" || tag == "TEXTAREA")
            {
                // Skip hidden inputs entirely
                if (tag == "INPUT" && node.Attr != null && node.Attr.TryGetValue("type", out var hiddenCheck) && hiddenCheck.ToLowerInvariant() == "hidden")
                {
                    // Don't draw anything for hidden inputs
                    return;
                }
                
                // Draw a debug border/background if the element is otherwise invisible
                // Real browsers rely on UA stylesheet. We simulate it here.
                if (box.Border.Top == 0)
                {
                    using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Gray, StrokeWidth = 1 })
                    {
                        canvas.DrawRect(box.BorderBox, paint);
                    }
                }
                
                // Draw Value (Text)
                if (node.Attr != null && node.Attr.ContainsKey("value"))
                {
                    string val = node.Attr["value"];
                    string type = node.Attr.ContainsKey("type") ? node.Attr["type"].ToLowerInvariant() : "text";
                    
                    if (type == "checkbox")
                    {
                         using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1 })
                         {
                             // Draw Box
                             float size = Math.Min(box.ContentBox.Width, box.ContentBox.Height) - 4;
                             if (size < 10) size = 10;
                             float x = box.ContentBox.Left + 2;
                             float y = box.ContentBox.MidY - size/2;
                             var rect = new SKRect(x, y, x+size, y+size);
                             canvas.DrawRect(rect, paint);
                             
                             // Draw Checkmark if checked
                             if (node.Attr.ContainsKey("checked"))
                             {
                                 paint.Style = SKPaintStyle.Fill;
                                 rect.Inflate(-2, -2);
                                 canvas.DrawRect(rect, paint);
                             }
                         }
                    }
                    else if (type == "radio")
                    {
                         using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 1, IsAntialias = true })
                         {
                             // Draw Circle
                             float size = Math.Min(box.ContentBox.Width, box.ContentBox.Height) - 4;
                             if (size < 10) size = 10;
                             float x = box.ContentBox.Left + size/2 + 2;
                             float y = box.ContentBox.MidY;
                             canvas.DrawCircle(x, y, size/2, paint);
                             
                             // Draw Dot if checked
                             if (node.Attr.ContainsKey("checked"))
                             {
                                 paint.Style = SKPaintStyle.Fill;
                                 canvas.DrawCircle(x, y, size/2 - 3, paint);
                             }
                         }
                    }
                    else if (!string.IsNullOrEmpty(val))
                    {
                         bool isBtn = type == "submit" || type == "button" || type == "reset" || tag == "BUTTON";
                         
                         using (var paint = new SKPaint())
                         {
                             paint.TextSize = layoutStyle?.FontSize != null ? (float)layoutStyle.FontSize.Value : DefaultFontSize;
                             paint.Color = SKColors.Black;
                             paint.IsAntialias = true;
                             paint.Typeface = ResolveTypeface(layoutStyle?.FontFamily?.ToString(), val); 
                             
                             // Center text for buttons
                             if (isBtn)
                             {
                                 paint.TextAlign = SKTextAlign.Center;
                                 float centerX = box.ContentBox.MidX;
                                 float centerY = box.ContentBox.MidY + (paint.TextSize / 2) - 2; 
                                 canvas.DrawText(val, centerX, centerY, paint);
                             }
                             else
                             {
                                 // Left align for text inputs
                                 float drawX = box.ContentBox.Left + 2; 
                                 float drawY = box.ContentBox.MidY + (paint.TextSize / 2) - 2;
                                 
                                 canvas.Save();
                                 canvas.ClipRect(box.ContentBox);
                                 canvas.DrawText(val, drawX, drawY, paint);
                                 canvas.Restore();
                             }
                         }
                    }
                }
            }

            if (tag == "IMG")
            {
                 // Fetch from ImageLoader
                 string src = node.Attr?.ContainsKey("src") == true ? node.Attr["src"] : null;
                 
                  // Resolve Relative URL
                 if (!string.IsNullOrEmpty(src) && !src.StartsWith("http") && !src.StartsWith("data:") && !string.IsNullOrEmpty(_baseUrl))
                 {
                     try 
                     {
                         var baseUri = new Uri(_baseUrl);
                         var resolved = new Uri(baseUri, src);
                         src = resolved.AbsoluteUri;
                     }
                     catch { }
                 }

                 var bitmap = ImageLoader.GetImage(src);
                 
                 if (bitmap != null)
                 {
                     // Draw Bitmap
                     using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
                     {
                         canvas.DrawBitmap(bitmap, box.ContentBox, paint); 
                     }
                 }
                 else
                 {
                     // Draw placeholder X
                     using (var paint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.LightGray })
                     {
                        canvas.DrawRect(box.ContentBox, paint);
                        canvas.DrawLine(box.ContentBox.Left, box.ContentBox.Top, box.ContentBox.Right, box.ContentBox.Bottom, paint);
                        canvas.DrawLine(box.ContentBox.Right, box.ContentBox.Top, box.ContentBox.Left, box.ContentBox.Bottom, paint);
                     }
                 }
            }
            if (tag == "SVG")
            {
                 string svgXml = GetOuterXml(node);
                 if (!string.IsNullOrEmpty(svgXml))
                 {
                     try
                     {
                         using (var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(svgXml)))
                         {
                             var svg = new Svg.Skia.SKSvg();
                             svg.Load(ms);
                             if (svg.Picture != null)
                             {
                                 canvas.Save();
                                 
                                 // Get SVG dimensions from viewBox or attributes
                                 var cull = svg.Picture.CullRect;
                                 float svgW = cull.Width;
                                 float svgH = cull.Height;
                                 
                                 // Check for viewBox attribute for accurate sizing
                                 if (node.Attr != null && node.Attr.TryGetValue("viewBox", out var viewBox))
                                 {
                                     var vbParts = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                                     if (vbParts.Length >= 4)
                                     {
                                         float.TryParse(vbParts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out svgW);
                                         float.TryParse(vbParts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out svgH);
                                     }
                                 }
                                 
                                 if (svgW > 0 && svgH > 0)
                                 {
                                     // Calculate scale to fit while preserving aspect ratio
                                     float scaleX = box.ContentBox.Width / svgW;
                                     float scaleY = box.ContentBox.Height / svgH;
                                     float scale = Math.Min(scaleX, scaleY); // Use uniform scale for aspect ratio
                                     
                                     // Center the SVG within the content box
                                     float scaledW = svgW * scale;
                                     float scaledH = svgH * scale;
                                     float offsetX = box.ContentBox.Left + (box.ContentBox.Width - scaledW) / 2;
                                     float offsetY = box.ContentBox.Top + (box.ContentBox.Height - scaledH) / 2;
                                     
                                     canvas.Translate(offsetX, offsetY);
                                     canvas.Scale(scale, scale);
                                     
                                     // Offset for viewBox origin
                                     if (cull.Left != 0 || cull.Top != 0)
                                     {
                                         canvas.Translate(-cull.Left, -cull.Top);
                                     }
                                 }
                                 else
                                 {
                                     canvas.Translate(box.ContentBox.Left, box.ContentBox.Top);
                                 }
                                 
                                 canvas.DrawPicture(svg.Picture);
                                 canvas.Restore();
                             }
                         }
                     }
                     catch { /* Ignore invalid SVG */ }
                 }
            }

            // 3.8 Media Placeholders
            if (tag == "VIDEO" || tag == "AUDIO" || tag == "CANVAS" || tag == "IFRAME" || tag == "OBJECT" || tag == "EMBED")
            {
                 using (var paint = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.DarkGray })
                 {
                      canvas.DrawRect(box.BorderBox, paint);
                 }
                 using (var paint = new SKPaint { Color = SKColors.White, TextSize = 12, IsAntialias = true, TextAlign = SKTextAlign.Center })
                 {
                      float midX = box.BorderBox.MidX;
                      float midY = box.BorderBox.MidY + 4;
                      canvas.DrawText($"<{tag}>", midX, midY, paint);
                 }
            }
            
            // 3.6 Draw HR
            if (tag == "HR")
            {
                using (var paint = new SKPaint { Color = SKColors.LightGray, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
                {
                     float midY = box.MarginBox.MidY;
                     canvas.DrawLine(box.MarginBox.Left, midY, box.MarginBox.Right, midY, paint);
                }
            }

            // 3.7 Draw List Markers (Bullets/Numbers)
            if (tag == "LI")
            {
                 var parent = GetParent(node);
                 if (parent != null)
                 {
                     string pTag = parent.Tag?.ToUpperInvariant();
                     float markerX = box.MarginBox.Left + 10; // Indent
                     float markerY = box.ContentBox.Top + 14; // Approx baseline (16px font -> 14px down)
                     
                     // Get list-style-type from either LI or parent UL/OL
                     string listStyleType = layoutStyle?.ListStyleType?.ToLowerInvariant();
                     if (string.IsNullOrEmpty(listStyleType) || listStyleType == "inherit" || listStyleType == "initial")
                     {
                         // Check parent for list-style-type
                         CssComputed parentStyle = null;
                         if (_styles != null) _styles.TryGetValue(parent, out parentStyle);
                         listStyleType = parentStyle?.ListStyleType?.ToLowerInvariant();
                     }
                     
                     // Default based on parent tag
                     if (string.IsNullOrEmpty(listStyleType))
                     {
                         listStyleType = pTag == "OL" ? "decimal" : "disc";
                     }
                     
                     // Get ::marker styles if available
                     CssComputed markerStyle = layoutStyle?.Marker;
                     float markerFontSize = 12f;
                     SKColor markerColor = SKColors.Black;
                     string markerContent = null;
                     
                     if (markerStyle != null)
                     {
                         // Apply ::marker color
                         if (markerStyle.ForegroundColor.HasValue)
                         {
                             var c = markerStyle.ForegroundColor.Value;
                             markerColor = new SKColor(c.R, c.G, c.B, c.A);
                         }
                         
                         // Apply ::marker font-size
                         if (markerStyle.FontSize.HasValue)
                         {
                             markerFontSize = (float)markerStyle.FontSize.Value;
                         }
                         
                         // Apply ::marker content (custom marker text)
                         markerContent = markerStyle.Content;
                         if (!string.IsNullOrEmpty(markerContent))
                         {
                             // Remove quotes from content value
                             markerContent = markerContent.Trim('"', '\'');
                         }
                     }

                     using (var paint = new SKPaint { Color = markerColor, IsAntialias = true, TextSize = markerFontSize })
                     {
                         // Apply foreground color if set (fallback if no ::marker style)
                         if (markerStyle == null && layoutStyle?.ForegroundColor.HasValue == true)
                         {
                             var c = layoutStyle.ForegroundColor.Value;
                             paint.Color = new SKColor(c.R, c.G, c.B, c.A);
                         }
                         
                         // If custom content is set via ::marker, use it
                         if (!string.IsNullOrEmpty(markerContent))
                         {
                             canvas.DrawText(markerContent, markerX, markerY, paint);
                         }
                         else
                         {
                             // Use list-style-type based rendering
                             switch (listStyleType)
                             {
                             case "none":
                                 // No marker
                                 break;
                                 
                             case "disc":
                                 // Filled circle (default for UL)
                                 paint.Style = SKPaintStyle.Fill;
                                 canvas.DrawCircle(markerX, markerY - 4, 3, paint);
                                 break;
                                 
                             case "circle":
                                 // Hollow circle
                                 paint.Style = SKPaintStyle.Stroke;
                                 paint.StrokeWidth = 1.5f;
                                 canvas.DrawCircle(markerX, markerY - 4, 3, paint);
                                 break;
                                 
                             case "square":
                                 // Filled square
                                 paint.Style = SKPaintStyle.Fill;
                                 canvas.DrawRect(markerX - 3, markerY - 7, 6, 6, paint);
                                 break;
                                 
                             case "decimal":
                                 // Numbers (1, 2, 3...)
                                 {
                                     int index = parent.Children.IndexOf(node) + 1;
                                     canvas.DrawText($"{index}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             case "decimal-leading-zero":
                                 // Numbers with leading zeros (01, 02, 03...)
                                 {
                                     int index = parent.Children.IndexOf(node) + 1;
                                     canvas.DrawText($"{index:D2}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             case "lower-roman":
                                 // Lowercase Roman numerals (i, ii, iii...)
                                 {
                                     int index = parent.Children.IndexOf(node) + 1;
                                     canvas.DrawText($"{ToRomanNumeral(index).ToLower()}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             case "upper-roman":
                                 // Uppercase Roman numerals (I, II, III...)
                                 {
                                     int index = parent.Children.IndexOf(node) + 1;
                                     canvas.DrawText($"{ToRomanNumeral(index)}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             case "lower-alpha":
                             case "lower-latin":
                                 // Lowercase letters (a, b, c...)
                                 {
                                     int index = parent.Children.IndexOf(node);
                                     string letter = index < 26 ? ((char)('a' + index)).ToString() : $"a{index - 25}";
                                     canvas.DrawText($"{letter}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             case "upper-alpha":
                             case "upper-latin":
                                 // Uppercase letters (A, B, C...)
                                 {
                                     int index = parent.Children.IndexOf(node);
                                     string letter = index < 26 ? ((char)('A' + index)).ToString() : $"A{index - 25}";
                                     canvas.DrawText($"{letter}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             case "lower-greek":
                                 // Greek letters (α, β, γ...)
                                 {
                                     int index = parent.Children.IndexOf(node);
                                     char[] greek = { 'α', 'β', 'γ', 'δ', 'ε', 'ζ', 'η', 'θ', 'ι', 'κ', 'λ', 'μ', 'ν', 'ξ', 'ο', 'π', 'ρ', 'σ', 'τ', 'υ', 'φ', 'χ', 'ψ', 'ω' };
                                     string letter = index < greek.Length ? greek[index].ToString() : $"{greek[0]}{index - greek.Length + 1}";
                                     canvas.DrawText($"{letter}.", markerX, markerY, paint);
                                 }
                                 break;
                                 
                             default:
                                 // Default to disc for unrecognized types
                                 if (pTag == "OL")
                                 {
                                     int index = parent.Children.IndexOf(node) + 1;
                                     canvas.DrawText($"{index}.", markerX, markerY, paint);
                                 }
                                 else
                                 {
                                     paint.Style = SKPaintStyle.Fill;
                                     canvas.DrawCircle(markerX, markerY - 4, 3, paint);
                                 }
                                 break;
                             }
                         }
                     }
                 }
            }

            // 4. Recurse (with z-index sorting)
            if (node.Children != null)
            {
                // Sort children by z-index for proper stacking
                var sortedChildren = node.Children.OrderBy(c =>
                {
                    if (_styles != null && _styles.TryGetValue(c, out var childStyle))
                        return childStyle?.ZIndex ?? 0;
                    return 0;
                }).ToList();
                
                foreach (var child in sortedChildren)
                {
                    DrawLayout(child, canvas);
                }
            }
            
            // 5. Restore canvas if we applied transform
            if (hasTransform)
            {
                canvas.Restore();
            }
            
            // 6. Restore canvas if we applied position:relative offset
            if (isRelativePositioned && (relativeOffsetX != 0 || relativeOffsetY != 0))
            {
                canvas.Restore();
            }
            
            // 7. Restore canvas if we applied clip-path
            if (hasClipPath && clipPathSkia != null)
            {
                canvas.Restore();
                clipPathSkia.Dispose();
            }
        }
        private void ApplyUserAgentStyles(LiteElement node, ref CssComputed style)
        {
            if (node == null) return;
            string tag = node.Tag?.ToUpperInvariant();

            if (tag == "INPUT" || tag == "TEXTAREA" || tag == "BUTTON" || tag == "SELECT" || tag == "FIELDSET")
            {
                if (style == null) style = new CssComputed();
                
                // Check if this is a button-type input
                string inputType = node.Attr?.ContainsKey("type") == true ? node.Attr["type"]?.ToLowerInvariant() : "";
                bool isButtonType = tag == "BUTTON" || inputType == "submit" || inputType == "button" || inputType == "reset";

                // 1. Force Background if missing or transparent
                // Note: SkiaColor transparent is 0 (alpha 0)
                bool hasBackground = style.BackgroundColor.HasValue && style.BackgroundColor.Value.A > 0;
                
                if (!hasBackground && tag != "FIELDSET") // Fieldset transparent by default
                {
                    if (isButtonType)
                    {
                        // Google-style button: light gray background (#f8f9fa)
                        style.BackgroundColor = Avalonia.Media.Color.FromRgb(0xf8, 0xf9, 0xfa);
                    }
                    else
                    {
                        // Text inputs: white background
                        style.BackgroundColor = Avalonia.Media.Colors.White;
                    }
                }

                // 2. Force Border if missing
                if (style.BorderThickness.Top == 0 && style.BorderThickness.Left == 0)
                {
                    if (tag == "FIELDSET")
                    {
                         style.BorderThickness = new Avalonia.Thickness(1); // Usually groove/etched
                         style.BorderBrushColor = Avalonia.Media.Colors.DarkGray;
                         style.Margin = new Avalonia.Thickness(2, 2, 2, 2); // Default margin
                    }
                    else if (isButtonType)
                    {
                        // Google-style button: border matches background (#f8f9fa)
                        style.BorderThickness = new Avalonia.Thickness(1);
                        style.BorderBrushColor = Avalonia.Media.Color.FromRgb(0xf8, 0xf9, 0xfa);
                    }
                    else
                    {
                        // Text inputs: gray border
                        style.BorderThickness = new Avalonia.Thickness(1);
                        style.BorderBrushColor = Avalonia.Media.Colors.Gray;
                    }
                }
                
                // 3. Force Padding if missing
                if (style.Padding.Top == 0 && style.Padding.Left == 0)
                {
                    if (tag == "FIELDSET") 
                         style.Padding = new Avalonia.Thickness(10, 10, 10, 10); // Give space for Legend
                    else if (isButtonType)
                         style.Padding = new Avalonia.Thickness(16, 8, 16, 8); // Google buttons have more horizontal padding
                    else
                         style.Padding = new Avalonia.Thickness(5, 2, 5, 2);
                }
                
                // 4. Add default border-radius for modern button appearance
                if (isButtonType && style.BorderRadius.TopLeft == 0)
                {
                    style.BorderRadius = new Avalonia.CornerRadius(8); // Google uses 8px radius
                }
                
                // 5. Add border-radius for search inputs (rounded pill-style)
                if (!isButtonType && (tag == "INPUT" || tag == "TEXTAREA") && style.BorderRadius.TopLeft == 0)
                {
                    style.BorderRadius = new Avalonia.CornerRadius(24); // Google search box uses 24px radius
                }
            }
            
            // Fix for "Not Clickable" / "Mashed Together" links
            // Add default padding to A tags to increase hit target and spacing
            if (tag == "A")
            {
                 if (style == null) style = new CssComputed();
                 if (style.Padding.Top == 0 && style.Padding.Right == 0) 
                 {
                     // Add horizontal padding (space between links)
                     style.Padding = new Avalonia.Thickness(4, 0, 4, 0);
                 }
            }

            // Table and Cell Styles
            if (tag == "TABLE" || tag == "TD" || tag == "TH")
            {
                if (style == null) style = new CssComputed();
                
                // Force Border if missing (emulates border="1")
                if (style.BorderThickness.Top == 0 && style.BorderThickness.Left == 0)
                {
                    style.BorderThickness = new Avalonia.Thickness(1);
                    style.BorderBrushColor = Avalonia.Media.Colors.Gray;
                }
                
                // Add padding for cells
                if ((tag == "TD" || tag == "TH") && style.Padding.Top == 0 && style.Padding.Left == 0)
                {
                    style.Padding = new Avalonia.Thickness(5, 5, 5, 5);
                }
            }
        }

        public LiteElement HitTest(float x, float y)
        {
             // We need to find the specific element.
             // Because _boxes is flat, we can iterate all, but we want the 'highest Z-order',
             // which usually means the last rendered or the deepest in the tree.
             
             LiteElement bestMatch = null;
             // Start with root? We don't have root stored. 
             // We can iterate _boxes. The dictionary order is not guaranteed z-order.
             // However, smaller boxes usually are children of larger boxes.
             // So if we find all boxes containing point, the one with SMALLEST area is likely the leaf.
             
             float minArea = float.MaxValue;
             
             // ConcurrentDictionary.ToArray() is thread-safe
             var boxSnapshot = _boxes.ToArray();
             
             foreach (var kvp in boxSnapshot)
             {
                 var element = kvp.Key;
                 var box = kvp.Value;
                 
                 // Visibility check
                 if (box.BorderBox.Width <= 0 || box.BorderBox.Height <= 0) continue;
                 
                 if (box.BorderBox.Contains(x,y))
                 {
                     float area = box.BorderBox.Width * box.BorderBox.Height;
                     // Prefer smaller area (child over parent)
                     // If equal area (e.g. block wrapping inline), prefer content? 
                     // Or prefer element that is NOT the body/html if poss.
                     
                     if (area <= minArea) // Use <= to let later items override? Random.
                     {
                         // If area is same, prefer the one that is 'deeper' in DOM? 
                         // We don't know depth here.
                         // But usually we want links (A tags) over spans over divs.
                         
                         minArea = area;
                         bestMatch = element;
                     }
                 }
             }
             
             return bestMatch;
        }

        private bool ShouldHide(LiteElement node, CssComputed style)
        {
            if (node == null) return true;
            
            // 1. Tag Filtering
            string tag = node.Tag?.ToUpperInvariant();
            if (tag == "HEAD" || tag == "SCRIPT" || tag == "STYLE" || tag == "META" || tag == "LINK" || tag == "TITLE" || tag == "NOSCRIPT")
                return true;

            // 2. Hidden inputs should not be rendered
            if (tag == "INPUT" && node.Attr != null && node.Attr.TryGetValue("type", out var inputType) && inputType.ToLowerInvariant() == "hidden")
                return true;

            // 3. CSS Display: None
            if (style != null && string.Equals(style.Display, "none", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
        
        /// <summary>
        /// Get all text content from an element and its children
        /// </summary>
        private string GetTextContent(LiteElement node)
        {
            if (node == null) return "";
            if (node.IsText) return node.Text ?? "";
            
            var sb = new System.Text.StringBuilder();
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    sb.Append(GetTextContent(child));
                }
            }
            return sb.ToString();
        }
        
        /// <summary>
        /// Get text content from an element, excluding STYLE and SCRIPT elements
        /// </summary>
        private string GetTextContentExcludingStyle(LiteElement node)
        {
            if (node == null) return "";
            if (node.IsText) return node.Text ?? "";
            
            // Skip STYLE and SCRIPT elements entirely
            string tag = node.Tag?.ToUpperInvariant();
            if (tag == "STYLE" || tag == "SCRIPT") return "";
            
            var sb = new System.Text.StringBuilder();
            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    sb.Append(GetTextContentExcludingStyle(child));
                }
            }
            return sb.ToString();
        }
        
        public LiteElement GetParent(LiteElement node)
        {
            if (node == null) return null;
            _parents.TryGetValue(node, out var parent);
            return parent;
        }
        
        public CssComputed GetStyle(LiteElement node)
        {
            if (node == null || _styles == null) return null;
            _styles.TryGetValue(node, out var style);
            return style;
        }

        private SKTypeface ResolveTypeface(string fontFamily, string text)
        {
            // 1. Try CSS Font Families
            if (!string.IsNullOrEmpty(fontFamily))
            {
                var families = fontFamily.Split(',');
                foreach (var f in families)
                {
                    var clean = f.Trim().Trim('\'', '"');
                    var tf = SKTypeface.FromFamilyName(clean);
                    if (tf != null && tf.FamilyName != "Arial") // SKTypeface.FromFamily often returns Default (Arial) if not found, checking if it actually differs or checking coverage is hard. 
                    {
                         // Basic check: if we asked for "Times" and got "Arial", it failed.
                         // But SKTypeface behavior depends on OS.
                         // Let's assume it returns something valid.
                         return tf;
                    }
                }
            }
            
            // 2. Fallback based on content (Character Matching)
            if (!string.IsNullOrEmpty(text))
            {
                 // Check first non-whitespace char
                 foreach (var c in text)
                 {
                     if (!char.IsWhiteSpace(c))
                     {
                         var matched = SKFontManager.Default.MatchCharacter(c);
                         if (matched != null) return matched;
                         break;
                     }
                 }
            }

            // 3. Ultimate Fallback
            // On Windows, Segoe UI is good.
             var fallback = SKTypeface.FromFamilyName("Segoe UI");
             if (fallback != null) return fallback;
             
            return SKTypeface.FromFamilyName("Arial");
        }

        private string GetOuterXml(LiteElement node)
        {
            if (node == null) return "";
            if (node.IsText) return node.Text;

            var sb = new System.Text.StringBuilder();
            string tag = node.Tag?.ToLowerInvariant();
            sb.Append($"<{tag}");
            
            if (node.Attr != null)
            {
                foreach(var kvp in node.Attr)
                {
                    sb.Append($" {kvp.Key}=\"{kvp.Value}\"");
                }
            }
            sb.Append(">");

            if (node.Children != null)
            {
                foreach(var child in node.Children)
                {
                    sb.Append(GetOuterXml(child));
                }
            }
            
            sb.Append($"</{tag}>");
            return sb.ToString();
        }

        /// <summary>
        /// Wrap text into multiple lines based on available width
        /// </summary>
        private List<TextLine> WrapText(string text, SKPaint paint, float maxWidth, string whiteSpace, string hyphens = "none")
        {
            var lines = new List<TextLine>();
            if (string.IsNullOrEmpty(text)) return lines;
            
            // Determine if hyphens should be added when breaking words
            bool useHyphens = hyphens == "auto" || hyphens == "manual";
            
            // Handle pre/pre-wrap/pre-line whitespace modes
            bool preserveNewlines = whiteSpace == "pre" || whiteSpace == "pre-wrap" || whiteSpace == "pre-line";
            bool collapseSpaces = whiteSpace != "pre" && whiteSpace != "pre-wrap";
            
            // Normalize whitespace if needed
            if (collapseSpaces)
            {
                text = Regex.Replace(text, @"\s+", " ");
            }
            
            // Split by explicit newlines first
            var paragraphs = preserveNewlines ? text.Split('\n') : new[] { text };
            
            foreach (var paragraph in paragraphs)
            {
                var words = paragraph.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                {
                    if (preserveNewlines) lines.Add(new TextLine { Text = "", Width = 0, Y = lines.Count });
                    continue;
                }
                
                string currentLine = "";
                float currentWidth = 0;
                
                foreach (var word in words)
                {
                    string testLine = string.IsNullOrEmpty(currentLine) ? word : currentLine + " " + word;
                    float testWidth = paint.MeasureText(testLine);
                    
                    if (testWidth <= maxWidth || string.IsNullOrEmpty(currentLine))
                    {
                        currentLine = testLine;
                        currentWidth = testWidth;
                    }
                    else
                    {
                        // Add current line and start new one
                        lines.Add(new TextLine { Text = currentLine, Width = currentWidth, Y = lines.Count });
                        currentLine = word;
                        currentWidth = paint.MeasureText(word);
                        
                        // Handle very long words (break them)
                        if (currentWidth > maxWidth)
                        {
                            var brokenLines = BreakLongWord(word, paint, maxWidth, useHyphens);
                            for (int i = 0; i < brokenLines.Count - 1; i++)
                            {
                                lines.Add(new TextLine { Text = brokenLines[i].Text, Width = brokenLines[i].Width, Y = lines.Count });
                            }
                            if (brokenLines.Count > 0)
                            {
                                var last = brokenLines[brokenLines.Count - 1];
                                currentLine = last.Text;
                                currentWidth = last.Width;
                            }
                        }
                    }
                }
                
                if (!string.IsNullOrEmpty(currentLine))
                {
                    lines.Add(new TextLine { Text = currentLine, Width = currentWidth, Y = lines.Count });
                }
            }
            
            return lines;
        }

        /// <summary>
        /// Break a long word that exceeds maxWidth into multiple lines
        /// </summary>
        private List<TextLine> BreakLongWord(string word, SKPaint paint, float maxWidth, bool useHyphens = false)
        {
            var lines = new List<TextLine>();
            string remaining = word;
            float hyphenWidth = useHyphens ? paint.MeasureText("-") : 0;
            
            while (!string.IsNullOrEmpty(remaining))
            {
                int breakPoint = remaining.Length;
                float width = paint.MeasureText(remaining);
                
                if (width <= maxWidth)
                {
                    lines.Add(new TextLine { Text = remaining, Width = width, Y = 0 });
                    break;
                }
                
                // Binary search for break point (accounting for hyphen width if using hyphens)
                float effectiveMaxWidth = useHyphens ? maxWidth - hyphenWidth : maxWidth;
                int low = 1, high = remaining.Length;
                while (low < high)
                {
                    int mid = (low + high + 1) / 2;
                    width = paint.MeasureText(remaining.Substring(0, mid));
                    if (width <= effectiveMaxWidth) low = mid;
                    else high = mid - 1;
                }
                
                if (low == 0) low = 1; // At least one character
                
                var part = remaining.Substring(0, low);
                // Add hyphen if breaking mid-word and more text remains
                if (useHyphens && remaining.Length > low)
                {
                    part = part + "-";
                }
                lines.Add(new TextLine { Text = part, Width = paint.MeasureText(part), Y = 0 });
                remaining = remaining.Substring(low);
            }
            
            return lines;
        }

        /// <summary>
        /// Parse CSS box-shadow value
        /// </summary>
        public static List<BoxShadowParsed> ParseBoxShadow(string value)
        {
            var shadows = new List<BoxShadowParsed>();
            if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
                return shadows;
            
            // Split multiple shadows by comma (outside of parentheses)
            var parts = SplitShadows(value);
            
            foreach (var part in parts)
            {
                var shadow = ParseSingleShadow(part.Trim());
                if (shadow != null) shadows.Add(shadow);
            }
            
            return shadows;
        }

        private static List<string> SplitShadows(string value)
        {
            var result = new List<string>();
            int depth = 0;
            int start = 0;
            
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    result.Add(value.Substring(start, i - start));
                    start = i + 1;
                }
            }
            
            if (start < value.Length)
                result.Add(value.Substring(start));
            
            return result;
        }

        private static BoxShadowParsed ParseSingleShadow(string value)
        {
            var shadow = new BoxShadowParsed();
            
            // Check for inset
            if (value.ToLowerInvariant().Contains("inset"))
            {
                shadow.Inset = true;
                value = Regex.Replace(value, @"\binset\b", "", RegexOptions.IgnoreCase).Trim();
            }
            
            // Extract color (rgba, rgb, hex, named)
            var colorMatch = Regex.Match(value, @"(rgba?\s*\([^)]+\)|#[0-9a-fA-F]{3,8}|\b(?:transparent|black|white|red|green|blue|gray|grey)\b)", RegexOptions.IgnoreCase);
            if (colorMatch.Success)
            {
                shadow.Color = ParseColorToSK(colorMatch.Value);
                value = value.Replace(colorMatch.Value, "").Trim();
            }
            
            // Extract numeric values (offset-x, offset-y, blur, spread)
            var numbers = Regex.Matches(value, @"-?[\d.]+(?:px|em|rem)?");
            var values = new List<float>();
            
            foreach (Match m in numbers)
            {
                float v = ParseCssLength(m.Value);
                values.Add(v);
            }
            
            if (values.Count >= 2)
            {
                shadow.OffsetX = values[0];
                shadow.OffsetY = values[1];
                if (values.Count >= 3) shadow.BlurRadius = Math.Max(0, values[2]);
                if (values.Count >= 4) shadow.SpreadRadius = values[3];
            }
            
            return shadow;
        }

        /// <summary>
        /// Parse CSS transform value
        /// </summary>
        public static TransformParsed ParseTransform(string value)
        {
            var transform = new TransformParsed();
            if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
                return transform;
            
            // Parse individual transform functions
            var funcMatches = Regex.Matches(value, @"(\w+)\s*\(([^)]+)\)");
            
            foreach (Match m in funcMatches)
            {
                string func = m.Groups[1].Value.ToLowerInvariant();
                string args = m.Groups[2].Value;
                var argValues = Regex.Matches(args, @"-?[\d.]+");
                var nums = new List<float>();
                foreach (Match a in argValues)
                {
                    if (float.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                        nums.Add(v);
                }
                
                switch (func)
                {
                    case "translate":
                        if (nums.Count >= 1) transform.TranslateX = nums[0];
                        if (nums.Count >= 2) transform.TranslateY = nums[1];
                        break;
                    case "translatex":
                        if (nums.Count >= 1) transform.TranslateX = nums[0];
                        break;
                    case "translatey":
                        if (nums.Count >= 1) transform.TranslateY = nums[0];
                        break;
                    case "scale":
                        if (nums.Count >= 1) transform.ScaleX = nums[0];
                        if (nums.Count >= 2) transform.ScaleY = nums[1];
                        else transform.ScaleY = transform.ScaleX;
                        break;
                    case "scalex":
                        if (nums.Count >= 1) transform.ScaleX = nums[0];
                        break;
                    case "scaley":
                        if (nums.Count >= 1) transform.ScaleY = nums[0];
                        break;
                    case "rotate":
                        if (nums.Count >= 1) transform.Rotate = nums[0];
                        break;
                    case "skew":
                        if (nums.Count >= 1) transform.SkewX = nums[0];
                        if (nums.Count >= 2) transform.SkewY = nums[1];
                        break;
                    case "skewx":
                        if (nums.Count >= 1) transform.SkewX = nums[0];
                        break;
                    case "skewy":
                        if (nums.Count >= 1) transform.SkewY = nums[0];
                        break;
                }
            }
            
            return transform;
        }

        /// <summary>
        /// Parse CSS text-decoration value
        /// </summary>
        public static TextDecorationParsed ParseTextDecoration(string value)
        {
            var deco = new TextDecorationParsed();
            if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
                return deco;
            
            string lower = value.ToLowerInvariant();
            
            deco.Underline = lower.Contains("underline");
            deco.Overline = lower.Contains("overline");
            deco.LineThrough = lower.Contains("line-through");
            
            if (lower.Contains("dashed")) deco.Style = "dashed";
            else if (lower.Contains("dotted")) deco.Style = "dotted";
            else if (lower.Contains("wavy")) deco.Style = "wavy";
            
            // Try to extract color
            var colorMatch = Regex.Match(value, @"(rgba?\s*\([^)]+\)|#[0-9a-fA-F]{3,8})", RegexOptions.IgnoreCase);
            if (colorMatch.Success)
            {
                deco.Color = ParseColorToSK(colorMatch.Value);
            }
            
            return deco;
        }

        /// <summary>
        /// Parse a CSS color string to SKColor
        /// </summary>
        public static SKColor ParseColorToSK(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new SKColor(0, 0, 0, 80);
            value = value.Trim().ToLowerInvariant();
            
            // Named colors
            var namedColors = new Dictionary<string, SKColor>(StringComparer.OrdinalIgnoreCase)
            {
                { "transparent", SKColors.Transparent },
                { "black", SKColors.Black },
                { "white", SKColors.White },
                { "red", SKColors.Red },
                { "green", SKColors.Green },
                { "blue", SKColors.Blue },
                { "gray", SKColors.Gray },
                { "grey", SKColors.Gray },
                { "yellow", SKColors.Yellow },
                { "orange", SKColors.Orange },
                { "purple", SKColors.Purple },
                { "pink", SKColors.Pink },
                { "cyan", SKColors.Cyan },
                { "magenta", SKColors.Magenta },
            };
            
            if (namedColors.TryGetValue(value, out var named))
                return named;
            
            // Hex color
            if (value.StartsWith("#"))
            {
                try
                {
                    return SKColor.Parse(value);
                }
                catch { }
            }
            
            // rgba(r, g, b, a)
            var rgbaMatch = Regex.Match(value, @"rgba?\s*\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)(?:\s*,\s*([\d.]+))?\s*\)");
            if (rgbaMatch.Success)
            {
                byte r = (byte)Math.Min(255, Math.Max(0, float.Parse(rgbaMatch.Groups[1].Value, CultureInfo.InvariantCulture)));
                byte g = (byte)Math.Min(255, Math.Max(0, float.Parse(rgbaMatch.Groups[2].Value, CultureInfo.InvariantCulture)));
                byte b = (byte)Math.Min(255, Math.Max(0, float.Parse(rgbaMatch.Groups[3].Value, CultureInfo.InvariantCulture)));
                byte a = 255;
                
                if (rgbaMatch.Groups[4].Success)
                {
                    float alpha = float.Parse(rgbaMatch.Groups[4].Value, CultureInfo.InvariantCulture);
                    a = (byte)(alpha <= 1 ? alpha * 255 : Math.Min(255, alpha));
                }
                
                return new SKColor(r, g, b, a);
            }
            
            return new SKColor(0, 0, 0, 80); // Default shadow color
        }

        /// <summary>
        /// Parse CSS length value (px, em, rem) to float
        /// </summary>
        private static float ParseCssLength(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            value = value.Trim().ToLowerInvariant();
            
            float multiplier = 1;
            if (value.EndsWith("px")) value = value.Substring(0, value.Length - 2);
            else if (value.EndsWith("em")) { value = value.Substring(0, value.Length - 2); multiplier = 16; }
            else if (value.EndsWith("rem")) { value = value.Substring(0, value.Length - 3); multiplier = 16; }
            
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
                return result * multiplier;
            
            return 0;
        }

        /// <summary>
        /// Process CSS counter-reset and counter-increment properties
        /// </summary>
        private void ProcessCssCounters(CssComputed style)
        {
            if (style == null) return;
            
            // Process counter-reset
            if (!string.IsNullOrEmpty(style.CounterReset) && style.CounterReset != "none")
            {
                ParseAndApplyCounters(style.CounterReset, isReset: true);
            }
            
            // Process counter-increment
            if (!string.IsNullOrEmpty(style.CounterIncrement) && style.CounterIncrement != "none")
            {
                ParseAndApplyCounters(style.CounterIncrement, isReset: false);
            }
        }
        
        private void ParseAndApplyCounters(string counterStr, bool isReset)
        {
            // Format: "name [value] [name2 [value2]]..."
            // Default value for reset is 0, for increment is 1
            var parts = counterStr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int i = 0;
            while (i < parts.Length)
            {
                string name = parts[i].Trim();
                int value = isReset ? 0 : 1; // Default values
                
                // Check if next part is a number
                if (i + 1 < parts.Length && int.TryParse(parts[i + 1], out int num))
                {
                    value = num;
                    i += 2;
                }
                else
                {
                    i += 1;
                }
                
                if (isReset)
                {
                    _counters[name] = value;
                }
                else
                {
                    // Increment
                    if (!_counters.ContainsKey(name))
                        _counters[name] = 0;
                    _counters[name] += value;
                }
            }
        }
        
        /// <summary>
        /// Get current value of a CSS counter
        /// </summary>
        public int GetCounterValue(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            _counters.TryGetValue(name, out int value);
            return value;
        }
        
        /// <summary>
        /// Resolve counter() and counters() functions in content property
        /// </summary>
        public string ResolveContentCounters(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;
            
            // Resolve counter(name) or counter(name, style)
            var counterMatch = Regex.Match(content, @"counter\s*\(\s*([^,\)]+)\s*(?:,\s*([^)]+))?\s*\)");
            while (counterMatch.Success)
            {
                string name = counterMatch.Groups[1].Value.Trim();
                string style = counterMatch.Groups[2].Value.Trim();
                int value = GetCounterValue(name);
                
                string formatted = FormatCounterValue(value, style);
                content = content.Substring(0, counterMatch.Index) + formatted + 
                          content.Substring(counterMatch.Index + counterMatch.Length);
                          
                counterMatch = Regex.Match(content, @"counter\s*\(\s*([^,\)]+)\s*(?:,\s*([^)]+))?\s*\)");
            }
            
            // Resolve counters(name, string) or counters(name, string, style)
            var countersMatch = Regex.Match(content, @"counters\s*\(\s*([^,]+)\s*,\s*([^,\)]+)\s*(?:,\s*([^)]+))?\s*\)");
            while (countersMatch.Success)
            {
                string name = countersMatch.Groups[1].Value.Trim().Trim('"', '\'');
                string separator = countersMatch.Groups[2].Value.Trim().Trim('"', '\'');
                string style = countersMatch.Groups[3].Value.Trim();
                
                int value = GetCounterValue(name);
                string formatted = FormatCounterValue(value, style);
                
                content = content.Substring(0, countersMatch.Index) + formatted + 
                          content.Substring(countersMatch.Index + countersMatch.Length);
                          
                countersMatch = Regex.Match(content, @"counters\s*\(\s*([^,]+)\s*,\s*([^,\)]+)\s*(?:,\s*([^)]+))?\s*\)");
            }
            
            return content;
        }
        
        private string FormatCounterValue(int value, string style)
        {
            style = style?.ToLowerInvariant()?.Trim() ?? "";
            
            switch (style)
            {
                case "lower-alpha":
                case "lower-latin":
                    return value > 0 && value <= 26 ? ((char)('a' + value - 1)).ToString() : value.ToString();
                case "upper-alpha":
                case "upper-latin":
                    return value > 0 && value <= 26 ? ((char)('A' + value - 1)).ToString() : value.ToString();
                case "lower-roman":
                    return ToRomanNumeral(value).ToLowerInvariant();
                case "upper-roman":
                    return ToRomanNumeral(value);
                default: // decimal
                    return value.ToString();
            }
        }

        /// <summary>
        /// Parse CSS clip-path to SKPath
        /// </summary>
        private SKPath ParseClipPath(string clipPath, SKRect bounds)
        {
            if (string.IsNullOrWhiteSpace(clipPath)) return null;
            
            try
            {
                var lower = clipPath.ToLowerInvariant().Trim();
                
                // circle(radius at cx cy) or circle(radius)
                if (lower.StartsWith("circle("))
                {
                    var content = clipPath.Substring(7, clipPath.Length - 8).Trim();
                    float radius = Math.Min(bounds.Width, bounds.Height) / 2;
                    float cx = bounds.MidX;
                    float cy = bounds.MidY;
                    
                    var parts = content.Split(new[] { " at " }, StringSplitOptions.None);
                    
                    // Parse radius
                    var radiusPart = parts[0].Trim();
                    if (radiusPart.EndsWith("%"))
                    {
                        if (float.TryParse(radiusPart.TrimEnd('%'), out float pct))
                            radius = Math.Min(bounds.Width, bounds.Height) * (pct / 100f) / 2;
                    }
                    else if (radiusPart.EndsWith("px"))
                    {
                        if (float.TryParse(radiusPart.Replace("px", ""), out float px))
                            radius = px;
                    }
                    else if (radiusPart == "closest-side")
                        radius = Math.Min(bounds.Width, bounds.Height) / 2;
                    else if (radiusPart == "farthest-side")
                        radius = Math.Max(bounds.Width, bounds.Height) / 2;
                    
                    // Parse position
                    if (parts.Length > 1)
                    {
                        var posParts = parts[1].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (posParts.Length >= 1) cx = ParseClipPosition(posParts[0], bounds.Left, bounds.Width);
                        if (posParts.Length >= 2) cy = ParseClipPosition(posParts[1], bounds.Top, bounds.Height);
                    }
                    
                    var path = new SKPath();
                    path.AddCircle(cx, cy, radius);
                    return path;
                }
                // ellipse(rx ry at cx cy) or ellipse()
                else if (lower.StartsWith("ellipse("))
                {
                    var content = clipPath.Substring(8, clipPath.Length - 9).Trim();
                    float rx = bounds.Width / 2;
                    float ry = bounds.Height / 2;
                    float cx = bounds.MidX;
                    float cy = bounds.MidY;
                    
                    var parts = content.Split(new[] { " at " }, StringSplitOptions.None);
                    
                    // Parse radii
                    var radiiPart = parts[0].Trim();
                    if (!string.IsNullOrEmpty(radiiPart))
                    {
                        var radii = radiiPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (radii.Length >= 1) rx = ParseClipLength(radii[0], bounds.Width);
                        if (radii.Length >= 2) ry = ParseClipLength(radii[1], bounds.Height);
                        else if (radii.Length == 1) ry = rx; // If only one, use same for both
                    }
                    
                    // Parse position
                    if (parts.Length > 1)
                    {
                        var posParts = parts[1].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (posParts.Length >= 1) cx = ParseClipPosition(posParts[0], bounds.Left, bounds.Width);
                        if (posParts.Length >= 2) cy = ParseClipPosition(posParts[1], bounds.Top, bounds.Height);
                    }
                    
                    var path = new SKPath();
                    path.AddOval(new SKRect(cx - rx, cy - ry, cx + rx, cy + ry));
                    return path;
                }
                // inset(top right bottom left round radius)
                else if (lower.StartsWith("inset("))
                {
                    var content = clipPath.Substring(6, clipPath.Length - 7).Trim();
                    float top = 0, right = 0, bottom = 0, left = 0;
                    float borderRadius = 0;
                    
                    // Split by "round" to get inset values and radius
                    var roundParts = content.Split(new[] { " round " }, StringSplitOptions.None);
                    var insetStr = roundParts[0].Trim();
                    
                    var insetParts = insetStr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (insetParts.Length >= 1) top = ParseClipLength(insetParts[0], bounds.Height);
                    if (insetParts.Length >= 2) right = ParseClipLength(insetParts[1], bounds.Width);
                    else right = top;
                    if (insetParts.Length >= 3) bottom = ParseClipLength(insetParts[2], bounds.Height);
                    else bottom = top;
                    if (insetParts.Length >= 4) left = ParseClipLength(insetParts[3], bounds.Width);
                    else left = right;
                    
                    if (roundParts.Length > 1)
                    {
                        borderRadius = ParseClipLength(roundParts[1].Trim(), Math.Min(bounds.Width, bounds.Height));
                    }
                    
                    var rect = new SKRect(
                        bounds.Left + left,
                        bounds.Top + top,
                        bounds.Right - right,
                        bounds.Bottom - bottom
                    );
                    
                    var path = new SKPath();
                    if (borderRadius > 0)
                        path.AddRoundRect(rect, borderRadius, borderRadius);
                    else
                        path.AddRect(rect);
                    return path;
                }
                // polygon(x1 y1, x2 y2, ...)
                else if (lower.StartsWith("polygon("))
                {
                    var content = clipPath.Substring(8, clipPath.Length - 9).Trim();
                    var points = content.Split(',');
                    
                    var path = new SKPath();
                    bool first = true;
                    
                    foreach (var point in points)
                    {
                        var coords = point.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (coords.Length >= 2)
                        {
                            float x = ParseClipPosition(coords[0], bounds.Left, bounds.Width);
                            float y = ParseClipPosition(coords[1], bounds.Top, bounds.Height);
                            
                            if (first)
                            {
                                path.MoveTo(x, y);
                                first = false;
                            }
                            else
                            {
                                path.LineTo(x, y);
                            }
                        }
                    }
                    
                    path.Close();
                    return path;
                }
            }
            catch { }
            
            return null;
        }
        
        private float ParseClipPosition(string value, float start, float size)
        {
            value = value.Trim().ToLowerInvariant();
            if (value == "center") return start + size / 2;
            if (value == "left" || value == "top") return start;
            if (value == "right" || value == "bottom") return start + size;
            if (value.EndsWith("%") && float.TryParse(value.TrimEnd('%'), out float pct))
                return start + size * (pct / 100f);
            if (value.EndsWith("px") && float.TryParse(value.Replace("px", ""), out float px))
                return start + px;
            if (float.TryParse(value, out float num))
                return start + num;
            return start + size / 2;
        }
        
        private float ParseClipLength(string value, float refSize)
        {
            value = value.Trim().ToLowerInvariant();
            if (value.EndsWith("%") && float.TryParse(value.TrimEnd('%'), out float pct))
                return refSize * (pct / 100f);
            if (value.EndsWith("px") && float.TryParse(value.Replace("px", ""), out float px))
                return px;
            if (float.TryParse(value, out float num))
                return num;
            return 0;
        }

        /// <summary>
        /// Create Skia shader from Avalonia IBrush
        /// </summary>
        private SKShader CreateShaderFromBrush(Avalonia.Media.IBrush brush, SKRect bounds, float opacity)
        {
            if (brush == null) return null;
            
            try
            {
                if (brush is Avalonia.Media.LinearGradientBrush lgb)
                {
                    // Convert relative points to absolute
                    float startX = bounds.Left + (float)(lgb.StartPoint.Point.X * bounds.Width);
                    float startY = bounds.Top + (float)(lgb.StartPoint.Point.Y * bounds.Height);
                    float endX = bounds.Left + (float)(lgb.EndPoint.Point.X * bounds.Width);
                    float endY = bounds.Top + (float)(lgb.EndPoint.Point.Y * bounds.Height);
                    
                    var colors = new List<SKColor>();
                    var positions = new List<float>();
                    
                    foreach (var stop in lgb.GradientStops)
                    {
                        var c = stop.Color;
                        byte a = (byte)(c.A * opacity);
                        colors.Add(new SKColor(c.R, c.G, c.B, a));
                        positions.Add((float)stop.Offset);
                    }
                    
                    if (colors.Count < 2) return null;
                    
                    var mode = lgb.SpreadMethod == Avalonia.Media.GradientSpreadMethod.Repeat 
                        ? SKShaderTileMode.Repeat 
                        : (lgb.SpreadMethod == Avalonia.Media.GradientSpreadMethod.Reflect 
                            ? SKShaderTileMode.Mirror 
                            : SKShaderTileMode.Clamp);
                    
                    return SKShader.CreateLinearGradient(
                        new SKPoint(startX, startY),
                        new SKPoint(endX, endY),
                        colors.ToArray(),
                        positions.ToArray(),
                        mode);
                }
                else if (brush is Avalonia.Media.RadialGradientBrush rgb)
                {
                    // Convert relative center to absolute
                    float cx = bounds.Left + (float)(rgb.Center.Point.X * bounds.Width);
                    float cy = bounds.Top + (float)(rgb.Center.Point.Y * bounds.Height);
                    
                    // Get radius - use RadiusX/RadiusY or fall back to defaults
                    float radiusX = (float)(rgb.RadiusX.Scalar * (rgb.RadiusX.Unit == Avalonia.RelativeUnit.Relative ? bounds.Width : 1));
                    float radiusY = (float)(rgb.RadiusY.Scalar * (rgb.RadiusY.Unit == Avalonia.RelativeUnit.Relative ? bounds.Height : 1));
                    float radius = Math.Max(radiusX, radiusY);
                    
                    var colors = new List<SKColor>();
                    var positions = new List<float>();
                    
                    foreach (var stop in rgb.GradientStops)
                    {
                        var c = stop.Color;
                        byte a = (byte)(c.A * opacity);
                        colors.Add(new SKColor(c.R, c.G, c.B, a));
                        positions.Add((float)stop.Offset);
                    }
                    
                    if (colors.Count < 2) return null;
                    
                    var mode = rgb.SpreadMethod == Avalonia.Media.GradientSpreadMethod.Repeat 
                        ? SKShaderTileMode.Repeat 
                        : (rgb.SpreadMethod == Avalonia.Media.GradientSpreadMethod.Reflect 
                            ? SKShaderTileMode.Mirror 
                            : SKShaderTileMode.Clamp);
                    
                    return SKShader.CreateRadialGradient(
                        new SKPoint(cx, cy),
                        radius,
                        colors.ToArray(),
                        positions.ToArray(),
                        mode);
                }
                else if (brush is Avalonia.Media.SolidColorBrush scb)
                {
                    var c = scb.Color;
                    byte a = (byte)(c.A * opacity);
                    return SKShader.CreateColor(new SKColor(c.R, c.G, c.B, a));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CreateShaderFromBrush error: {ex.Message}");
            }
            
            return null;
        }

        /// <summary>
        /// Apply CSS filter effects to SKPaint
        /// </summary>
        private void ApplyFilter(SKPaint paint, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter) || filter.Equals("none", StringComparison.OrdinalIgnoreCase))
                return;
            
            // Parse filter functions
            var funcMatches = Regex.Matches(filter, @"(\w+)\s*\(([^)]+)\)");
            
            foreach (Match m in funcMatches)
            {
                string func = m.Groups[1].Value.ToLowerInvariant();
                string arg = m.Groups[2].Value.Trim();
                
                switch (func)
                {
                    case "blur":
                        float blurAmount = ParseCssLength(arg);
                        if (blurAmount > 0)
                        {
                            paint.ImageFilter = SKImageFilter.CreateBlur(blurAmount, blurAmount);
                        }
                        break;
                    
                    case "grayscale":
                        float grayAmount = ParsePercentOrDecimal(arg);
                        if (grayAmount > 0)
                        {
                            var cm = new float[]
                            {
                                0.2126f + 0.7874f * (1 - grayAmount), 0.7152f - 0.7152f * (1 - grayAmount), 0.0722f - 0.0722f * (1 - grayAmount), 0, 0,
                                0.2126f - 0.2126f * (1 - grayAmount), 0.7152f + 0.2848f * (1 - grayAmount), 0.0722f - 0.0722f * (1 - grayAmount), 0, 0,
                                0.2126f - 0.2126f * (1 - grayAmount), 0.7152f - 0.7152f * (1 - grayAmount), 0.0722f + 0.9278f * (1 - grayAmount), 0, 0,
                                0, 0, 0, 1, 0
                            };
                            paint.ColorFilter = SKColorFilter.CreateColorMatrix(cm);
                        }
                        break;
                    
                    case "brightness":
                        float bright = ParsePercentOrDecimal(arg);
                        if (Math.Abs(bright - 1) > 0.01f)
                        {
                            var cm = new float[]
                            {
                                bright, 0, 0, 0, 0,
                                0, bright, 0, 0, 0,
                                0, 0, bright, 0, 0,
                                0, 0, 0, 1, 0
                            };
                            paint.ColorFilter = SKColorFilter.CreateColorMatrix(cm);
                        }
                        break;
                    
                    case "contrast":
                        float contrast = ParsePercentOrDecimal(arg);
                        if (Math.Abs(contrast - 1) > 0.01f)
                        {
                            float t = (1 - contrast) / 2 * 255;
                            var cm = new float[]
                            {
                                contrast, 0, 0, 0, t,
                                0, contrast, 0, 0, t,
                                0, 0, contrast, 0, t,
                                0, 0, 0, 1, 0
                            };
                            paint.ColorFilter = SKColorFilter.CreateColorMatrix(cm);
                        }
                        break;
                    
                    case "sepia":
                        float sepiaAmount = ParsePercentOrDecimal(arg);
                        if (sepiaAmount > 0)
                        {
                            var cm = new float[]
                            {
                                0.393f + 0.607f * (1 - sepiaAmount), 0.769f - 0.769f * (1 - sepiaAmount), 0.189f - 0.189f * (1 - sepiaAmount), 0, 0,
                                0.349f - 0.349f * (1 - sepiaAmount), 0.686f + 0.314f * (1 - sepiaAmount), 0.168f - 0.168f * (1 - sepiaAmount), 0, 0,
                                0.272f - 0.272f * (1 - sepiaAmount), 0.534f - 0.534f * (1 - sepiaAmount), 0.131f + 0.869f * (1 - sepiaAmount), 0, 0,
                                0, 0, 0, 1, 0
                            };
                            paint.ColorFilter = SKColorFilter.CreateColorMatrix(cm);
                        }
                        break;
                    
                    case "opacity":
                        float opacityVal = ParsePercentOrDecimal(arg);
                        paint.Color = paint.Color.WithAlpha((byte)(opacityVal * 255));
                        break;
                    
                    case "invert":
                        float invertAmount = ParsePercentOrDecimal(arg);
                        if (invertAmount > 0)
                        {
                            var cm = new float[]
                            {
                                1 - 2 * invertAmount, 0, 0, 0, invertAmount * 255,
                                0, 1 - 2 * invertAmount, 0, 0, invertAmount * 255,
                                0, 0, 1 - 2 * invertAmount, 0, invertAmount * 255,
                                0, 0, 0, 1, 0
                            };
                            paint.ColorFilter = SKColorFilter.CreateColorMatrix(cm);
                        }
                        break;
                        
                    case "saturate":
                        float satAmount = ParsePercentOrDecimal(arg);
                        {
                            // Saturation matrix using luminance coefficients
                            float s = satAmount;
                            float lr = 0.2126f;
                            float lg = 0.7152f;
                            float lb = 0.0722f;
                            var cm = new float[]
                            {
                                lr + s * (1 - lr), lg - s * lg, lb - s * lb, 0, 0,
                                lr - s * lr, lg + s * (1 - lg), lb - s * lb, 0, 0,
                                lr - s * lr, lg - s * lg, lb + s * (1 - lb), 0, 0,
                                0, 0, 0, 1, 0
                            };
                            paint.ColorFilter = SKColorFilter.CreateColorMatrix(cm);
                        }
                        break;
                        
                    case "hue-rotate":
                        {
                            // Parse angle (degrees)
                            float angle = 0;
                            arg = arg.Trim();
                            if (arg.EndsWith("deg"))
                            {
                                float.TryParse(arg.Replace("deg", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out angle);
                            }
                            else if (arg.EndsWith("rad"))
                            {
                                float.TryParse(arg.Replace("rad", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out angle);
                                angle = angle * (180f / (float)Math.PI); // Convert radians to degrees
                            }
                            else if (arg.EndsWith("turn"))
                            {
                                float.TryParse(arg.Replace("turn", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out angle);
                                angle = angle * 360f;
                            }
                            else
                            {
                                float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out angle);
                            }
                            
                            if (Math.Abs(angle) > 0.1f)
                            {
                                float rad = angle * (float)Math.PI / 180f;
                                float cos = (float)Math.Cos(rad);
                                float sin = (float)Math.Sin(rad);
                                
                                // Hue rotation matrix
                                var cm = new float[]
                                {
                                    0.213f + cos * 0.787f - sin * 0.213f, 0.715f - cos * 0.715f - sin * 0.715f, 0.072f - cos * 0.072f + sin * 0.928f, 0, 0,
                                    0.213f - cos * 0.213f + sin * 0.143f, 0.715f + cos * 0.285f + sin * 0.140f, 0.072f - cos * 0.072f - sin * 0.283f, 0, 0,
                                    0.213f - cos * 0.213f - sin * 0.787f, 0.715f - cos * 0.715f + sin * 0.715f, 0.072f + cos * 0.928f + sin * 0.072f, 0, 0,
                                    0, 0, 0, 1, 0
                                };
                                paint.ColorFilter = SKColorFilter.CreateColorMatrix(cm);
                            }
                        }
                        break;
                        
                    case "drop-shadow":
                        {
                            // Parse drop-shadow(offset-x offset-y blur-radius color)
                            // This is different from box-shadow - it follows the alpha channel
                            var shadowParts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            float dx = 0, dy = 0, blur = 0;
                            SKColor shadowColor = SKColors.Black;
                            
                            int numericIndex = 0;
                            foreach (var part in shadowParts)
                            {
                                if (numericIndex < 3)
                                {
                                    float val = ParseCssLength(part);
                                    if (numericIndex == 0) dx = val;
                                    else if (numericIndex == 1) dy = val;
                                    else if (numericIndex == 2) blur = val;
                                    
                                    if (val != 0 || part == "0" || part == "0px")
                                        numericIndex++;
                                }
                                else
                                {
                                    var color = CssParser.ParseColor(part);
                                    if (color.HasValue)
                                    {
                                        var c = color.Value;
                                        shadowColor = new SKColor(c.R, c.G, c.B, c.A);
                                    }
                                }
                            }
                            
                            if (blur > 0 || dx != 0 || dy != 0)
                            {
                                paint.ImageFilter = SKImageFilter.CreateDropShadow(dx, dy, blur / 2, blur / 2, shadowColor);
                            }
                        }
                        break;
                }
            }
        }

        private float ParsePercentOrDecimal(string value)
        {
            value = value.Trim();
            if (value.EndsWith("%"))
            {
                if (float.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct))
                    return pct / 100f;
            }
            else if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float dec))
            {
                return dec;
            }
            return 1;
        }

        /// <summary>
        /// Convert integer to Roman numeral
        /// </summary>
        private string ToRomanNumeral(int number)
        {
            if (number <= 0 || number > 3999) return number.ToString();
            
            string[] thousands = { "", "M", "MM", "MMM" };
            string[] hundreds = { "", "C", "CC", "CCC", "CD", "D", "DC", "DCC", "DCCC", "CM" };
            string[] tens = { "", "X", "XX", "XXX", "XL", "L", "LX", "LXX", "LXXX", "XC" };
            string[] ones = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX" };
            
            return thousands[number / 1000] +
                   hundreds[(number % 1000) / 100] +
                   tens[(number % 100) / 10] +
                   ones[number % 10];
        }

        /// <summary>
        /// Draw box shadow for an element
        /// </summary>
        private void DrawBoxShadow(SKCanvas canvas, SKRect box, float borderRadius, List<BoxShadowParsed> shadows, float opacity)
        {
            if (shadows == null || shadows.Count == 0) return;
            
            foreach (var shadow in shadows)
            {
                if (shadow.Inset) continue; // Inset shadows drawn separately
                
                var shadowRect = new SKRect(
                    box.Left + shadow.OffsetX - shadow.SpreadRadius,
                    box.Top + shadow.OffsetY - shadow.SpreadRadius,
                    box.Right + shadow.OffsetX + shadow.SpreadRadius,
                    box.Bottom + shadow.OffsetY + shadow.SpreadRadius
                );
                
                using (var paint = new SKPaint())
                {
                    paint.IsAntialias = true;
                    paint.Style = SKPaintStyle.Fill;
                    
                    var color = shadow.Color;
                    color = color.WithAlpha((byte)(color.Alpha * opacity));
                    paint.Color = color;
                    
                    if (shadow.BlurRadius > 0)
                    {
                        paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, shadow.BlurRadius / 2);
                    }
                    
                    if (borderRadius > 0)
                        canvas.DrawRoundRect(shadowRect, borderRadius, borderRadius, paint);
                    else
                        canvas.DrawRect(shadowRect, paint);
                }
            }
        }

        /// <summary>
        /// Draw borders with full style support (solid, dashed, dotted, double, groove, ridge, inset, outset)
        /// </summary>
        private void DrawStyledBorders(SKCanvas canvas, BoxModel box, CssComputed layoutStyle, float borderRadius, float opacity)
        {
            var borderBox = box.BorderBox;
            var border = box.Border;
            
            // Get border color
            SKColor borderColor = SKColors.Black;
            if (layoutStyle?.BorderBrushColor.HasValue == true)
            {
                var c = layoutStyle.BorderBrushColor.Value;
                byte alpha = (byte)(c.A * opacity);
                borderColor = new SKColor(c.R, c.G, c.B, alpha);
            }
            
            // Get border styles
            string styleTop = layoutStyle?.BorderStyleTop ?? "solid";
            string styleRight = layoutStyle?.BorderStyleRight ?? "solid";
            string styleBottom = layoutStyle?.BorderStyleBottom ?? "solid";
            string styleLeft = layoutStyle?.BorderStyleLeft ?? "solid";
            
            // If all borders are the same style and width, use optimized path
            if (styleTop == styleRight && styleRight == styleBottom && styleBottom == styleLeft &&
                Math.Abs(border.Top - border.Right) < 0.1 && Math.Abs(border.Right - border.Bottom) < 0.1 && 
                Math.Abs(border.Bottom - border.Left) < 0.1)
            {
                float strokeWidth = (float)Math.Max(1, border.Top);
                DrawBorderWithStyle(canvas, borderBox, borderRadius, strokeWidth, borderColor, styleTop, true, true, true, true);
                return;
            }
            
            // Draw each border side separately
            if (border.Top > 0 && styleTop != "none" && styleTop != "hidden")
            {
                DrawBorderSide(canvas, borderBox, (float)border.Top, borderColor, styleTop, "top", borderRadius);
            }
            if (border.Right > 0 && styleRight != "none" && styleRight != "hidden")
            {
                DrawBorderSide(canvas, borderBox, (float)border.Right, borderColor, styleRight, "right", borderRadius);
            }
            if (border.Bottom > 0 && styleBottom != "none" && styleBottom != "hidden")
            {
                DrawBorderSide(canvas, borderBox, (float)border.Bottom, borderColor, styleBottom, "bottom", borderRadius);
            }
            if (border.Left > 0 && styleLeft != "none" && styleLeft != "hidden")
            {
                DrawBorderSide(canvas, borderBox, (float)border.Left, borderColor, styleLeft, "left", borderRadius);
            }
        }

        /// <summary>
        /// Draw a single border side with the specified style
        /// </summary>
        private void DrawBorderSide(SKCanvas canvas, SKRect borderBox, float width, SKColor color, string style, string side, float borderRadius)
        {
            using (var paint = new SKPaint { IsAntialias = true })
            {
                paint.Color = color;
                paint.StrokeWidth = width;
                paint.Style = SKPaintStyle.Stroke;
                
                // Apply path effect based on style
                ApplyBorderPathEffect(paint, style, width);
                
                // Determine coordinates for this side
                float x1, y1, x2, y2;
                float halfWidth = width / 2;
                
                switch (side)
                {
                    case "top":
                        x1 = borderBox.Left;
                        y1 = borderBox.Top + halfWidth;
                        x2 = borderBox.Right;
                        y2 = borderBox.Top + halfWidth;
                        break;
                    case "right":
                        x1 = borderBox.Right - halfWidth;
                        y1 = borderBox.Top;
                        x2 = borderBox.Right - halfWidth;
                        y2 = borderBox.Bottom;
                        break;
                    case "bottom":
                        x1 = borderBox.Left;
                        y1 = borderBox.Bottom - halfWidth;
                        x2 = borderBox.Right;
                        y2 = borderBox.Bottom - halfWidth;
                        break;
                    case "left":
                    default:
                        x1 = borderBox.Left + halfWidth;
                        y1 = borderBox.Top;
                        x2 = borderBox.Left + halfWidth;
                        y2 = borderBox.Bottom;
                        break;
                }
                
                // Handle 3D styles (groove, ridge, inset, outset)
                if (style == "groove" || style == "ridge" || style == "inset" || style == "outset")
                {
                    Draw3DBorderSide(canvas, x1, y1, x2, y2, width, color, style, side);
                }
                else if (style == "double")
                {
                    DrawDoubleBorderSide(canvas, x1, y1, x2, y2, width, color, side);
                }
                else
                {
                    canvas.DrawLine(x1, y1, x2, y2, paint);
                }
            }
        }

        /// <summary>
        /// Draw a full border (all sides) with a single style
        /// </summary>
        private void DrawBorderWithStyle(SKCanvas canvas, SKRect borderBox, float borderRadius, float strokeWidth, SKColor color, string style, bool top, bool right, bool bottom, bool left)
        {
            using (var paint = new SKPaint { IsAntialias = true })
            {
                paint.Color = color;
                paint.StrokeWidth = strokeWidth;
                paint.Style = SKPaintStyle.Stroke;
                
                ApplyBorderPathEffect(paint, style, strokeWidth);
                
                // Handle 3D styles specially
                if (style == "groove" || style == "ridge")
                {
                    Draw3DBoxBorder(canvas, borderBox, borderRadius, strokeWidth, color, style);
                }
                else if (style == "inset" || style == "outset")
                {
                    Draw3DBoxBorder(canvas, borderBox, borderRadius, strokeWidth, color, style);
                }
                else if (style == "double")
                {
                    DrawDoubleBoxBorder(canvas, borderBox, borderRadius, strokeWidth, color);
                }
                else
                {
                    if (borderRadius > 0)
                        canvas.DrawRoundRect(borderBox, borderRadius, borderRadius, paint);
                    else
                        canvas.DrawRect(borderBox, paint);
                }
            }
        }

        /// <summary>
        /// Apply SKPathEffect based on border style
        /// </summary>
        private void ApplyBorderPathEffect(SKPaint paint, string style, float strokeWidth)
        {
            switch (style)
            {
                case "dashed":
                    // Dashes are typically 3x stroke width with 1x gap
                    float dashLen = Math.Max(6, strokeWidth * 3);
                    float gapLen = Math.Max(3, strokeWidth);
                    paint.PathEffect = SKPathEffect.CreateDash(new float[] { dashLen, gapLen }, 0);
                    break;
                    
                case "dotted":
                    // Dots are typically 1:1 ratio
                    float dotSize = Math.Max(1, strokeWidth);
                    paint.PathEffect = SKPathEffect.CreateDash(new float[] { dotSize, dotSize * 2 }, 0);
                    paint.StrokeCap = SKStrokeCap.Round;
                    break;
                    
                // solid, double, groove, ridge, inset, outset use solid lines (with special rendering)
                default:
                    paint.PathEffect = null;
                    break;
            }
        }

        /// <summary>
        /// Draw a 3D border side (groove, ridge, inset, outset)
        /// </summary>
        private void Draw3DBorderSide(SKCanvas canvas, float x1, float y1, float x2, float y2, float width, SKColor baseColor, string style, string side)
        {
            // Calculate light and dark colors
            SKColor lightColor = LightenColor(baseColor, 0.5f);
            SKColor darkColor = DarkenColor(baseColor, 0.5f);
            
            // Determine which color goes on which half based on style and side
            SKColor firstColor, secondColor;
            bool isTopOrLeft = side == "top" || side == "left";
            
            switch (style)
            {
                case "groove":
                    firstColor = isTopOrLeft ? darkColor : lightColor;
                    secondColor = isTopOrLeft ? lightColor : darkColor;
                    break;
                case "ridge":
                    firstColor = isTopOrLeft ? lightColor : darkColor;
                    secondColor = isTopOrLeft ? darkColor : lightColor;
                    break;
                case "inset":
                    firstColor = isTopOrLeft ? darkColor : lightColor;
                    secondColor = firstColor;
                    break;
                case "outset":
                    firstColor = isTopOrLeft ? lightColor : darkColor;
                    secondColor = firstColor;
                    break;
                default:
                    firstColor = secondColor = baseColor;
                    break;
            }
            
            float halfWidth = width / 2;
            
            using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke })
            {
                // Draw first half
                paint.StrokeWidth = halfWidth;
                paint.Color = firstColor;
                
                float offset1 = halfWidth / 2;
                float dx = 0, dy = 0;
                if (side == "top") dy = -offset1;
                else if (side == "bottom") dy = offset1;
                else if (side == "left") dx = -offset1;
                else if (side == "right") dx = offset1;
                
                canvas.DrawLine(x1 + dx, y1 + dy, x2 + dx, y2 + dy, paint);
                
                // Draw second half
                paint.Color = secondColor;
                dx = -dx; dy = -dy;
                canvas.DrawLine(x1 + dx, y1 + dy, x2 + dx, y2 + dy, paint);
            }
        }

        /// <summary>
        /// Draw a double border side
        /// </summary>
        private void DrawDoubleBorderSide(SKCanvas canvas, float x1, float y1, float x2, float y2, float width, SKColor color, string side)
        {
            // Double borders: two lines with a gap equal to 1/3 width each
            float lineWidth = width / 3;
            float gap = lineWidth;
            
            using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke })
            {
                paint.StrokeWidth = lineWidth;
                paint.Color = color;
                
                float offset = lineWidth + gap / 2;
                float dx = 0, dy = 0;
                if (side == "top" || side == "bottom") dy = side == "top" ? -offset/2 : offset/2;
                else dx = side == "left" ? -offset/2 : offset/2;
                
                // Outer line
                canvas.DrawLine(x1 - dx, y1 - dy, x2 - dx, y2 - dy, paint);
                // Inner line
                canvas.DrawLine(x1 + dx, y1 + dy, x2 + dx, y2 + dy, paint);
            }
        }

        /// <summary>
        /// Draw a 3D box border (all sides with 3D effect)
        /// </summary>
        private void Draw3DBoxBorder(SKCanvas canvas, SKRect borderBox, float borderRadius, float strokeWidth, SKColor baseColor, string style)
        {
            SKColor lightColor = LightenColor(baseColor, 0.5f);
            SKColor darkColor = DarkenColor(baseColor, 0.5f);
            
            SKColor topLeftColor, bottomRightColor;
            
            if (style == "inset" || style == "groove")
            {
                topLeftColor = darkColor;
                bottomRightColor = lightColor;
            }
            else // outset, ridge
            {
                topLeftColor = lightColor;
                bottomRightColor = darkColor;
            }
            
            float halfWidth = strokeWidth / 2;
            
            // For groove/ridge, draw two sets of borders
            if (style == "groove" || style == "ridge")
            {
                using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = halfWidth })
                {
                    // Outer border
                    var outerBox = new SKRect(borderBox.Left + halfWidth/2, borderBox.Top + halfWidth/2, 
                                              borderBox.Right - halfWidth/2, borderBox.Bottom - halfWidth/2);
                    
                    paint.Color = topLeftColor;
                    canvas.DrawLine(outerBox.Left, outerBox.Bottom, outerBox.Left, outerBox.Top, paint);
                    canvas.DrawLine(outerBox.Left, outerBox.Top, outerBox.Right, outerBox.Top, paint);
                    
                    paint.Color = bottomRightColor;
                    canvas.DrawLine(outerBox.Right, outerBox.Top, outerBox.Right, outerBox.Bottom, paint);
                    canvas.DrawLine(outerBox.Right, outerBox.Bottom, outerBox.Left, outerBox.Bottom, paint);
                    
                    // Inner border (reverse colors)
                    var innerBox = new SKRect(borderBox.Left + halfWidth*1.5f, borderBox.Top + halfWidth*1.5f, 
                                              borderBox.Right - halfWidth*1.5f, borderBox.Bottom - halfWidth*1.5f);
                    
                    paint.Color = bottomRightColor;
                    canvas.DrawLine(innerBox.Left, innerBox.Bottom, innerBox.Left, innerBox.Top, paint);
                    canvas.DrawLine(innerBox.Left, innerBox.Top, innerBox.Right, innerBox.Top, paint);
                    
                    paint.Color = topLeftColor;
                    canvas.DrawLine(innerBox.Right, innerBox.Top, innerBox.Right, innerBox.Bottom, paint);
                    canvas.DrawLine(innerBox.Right, innerBox.Bottom, innerBox.Left, innerBox.Bottom, paint);
                }
            }
            else // inset, outset
            {
                using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = strokeWidth })
                {
                    var insetBox = new SKRect(borderBox.Left + halfWidth, borderBox.Top + halfWidth, 
                                              borderBox.Right - halfWidth, borderBox.Bottom - halfWidth);
                    
                    paint.Color = topLeftColor;
                    canvas.DrawLine(insetBox.Left, insetBox.Bottom, insetBox.Left, insetBox.Top, paint);
                    canvas.DrawLine(insetBox.Left, insetBox.Top, insetBox.Right, insetBox.Top, paint);
                    
                    paint.Color = bottomRightColor;
                    canvas.DrawLine(insetBox.Right, insetBox.Top, insetBox.Right, insetBox.Bottom, paint);
                    canvas.DrawLine(insetBox.Right, insetBox.Bottom, insetBox.Left, insetBox.Bottom, paint);
                }
            }
        }

        /// <summary>
        /// Draw a double box border (all sides)
        /// </summary>
        private void DrawDoubleBoxBorder(SKCanvas canvas, SKRect borderBox, float borderRadius, float strokeWidth, SKColor color)
        {
            float lineWidth = strokeWidth / 3;
            
            using (var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = lineWidth })
            {
                paint.Color = color;
                
                // Outer border
                var outerBox = new SKRect(borderBox.Left + lineWidth/2, borderBox.Top + lineWidth/2, 
                                          borderBox.Right - lineWidth/2, borderBox.Bottom - lineWidth/2);
                if (borderRadius > 0)
                    canvas.DrawRoundRect(outerBox, borderRadius, borderRadius, paint);
                else
                    canvas.DrawRect(outerBox, paint);
                
                // Inner border
                var innerBox = new SKRect(borderBox.Left + strokeWidth - lineWidth/2, borderBox.Top + strokeWidth - lineWidth/2, 
                                          borderBox.Right - strokeWidth + lineWidth/2, borderBox.Bottom - strokeWidth + lineWidth/2);
                if (borderRadius > 0)
                    canvas.DrawRoundRect(innerBox, Math.Max(0, borderRadius - strokeWidth + lineWidth), Math.Max(0, borderRadius - strokeWidth + lineWidth), paint);
                else
                    canvas.DrawRect(innerBox, paint);
            }
        }

        /// <summary>
        /// Lighten a color by a factor (0-1)
        /// </summary>
        private SKColor LightenColor(SKColor color, float factor)
        {
            int r = Math.Min(255, (int)(color.Red + (255 - color.Red) * factor));
            int g = Math.Min(255, (int)(color.Green + (255 - color.Green) * factor));
            int b = Math.Min(255, (int)(color.Blue + (255 - color.Blue) * factor));
            return new SKColor((byte)r, (byte)g, (byte)b, color.Alpha);
        }

        /// <summary>
        /// Darken a color by a factor (0-1)
        /// </summary>
        private SKColor DarkenColor(SKColor color, float factor)
        {
            int r = (int)(color.Red * (1 - factor));
            int g = (int)(color.Green * (1 - factor));
            int b = (int)(color.Blue * (1 - factor));
            return new SKColor((byte)r, (byte)g, (byte)b, color.Alpha);
        }

        /// <summary>
        /// Draw inset shadows
        /// </summary>
        private void DrawInsetShadow(SKCanvas canvas, SKRect box, float borderRadius, List<BoxShadowParsed> shadows, float opacity)
        {
            if (shadows == null) return;
            
            foreach (var shadow in shadows.Where(s => s.Inset))
            {
                canvas.Save();
                
                // Clip to the box
                if (borderRadius > 0)
                    canvas.ClipRoundRect(new SKRoundRect(box, borderRadius), SKClipOperation.Intersect);
                else
                    canvas.ClipRect(box, SKClipOperation.Intersect);
                
                // Draw inner shadow by drawing a larger shadow outside and letting it bleed in
                var shadowRect = new SKRect(
                    box.Left - 1000 + shadow.OffsetX,
                    box.Top - 1000 + shadow.OffsetY,
                    box.Right + 1000 + shadow.OffsetX,
                    box.Bottom + 1000 + shadow.OffsetY
                );
                
                using (var paint = new SKPaint())
                {
                    paint.IsAntialias = true;
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 1000;
                    
                    var color = shadow.Color;
                    color = color.WithAlpha((byte)(color.Alpha * opacity));
                    paint.Color = color;
                    
                    if (shadow.BlurRadius > 0)
                    {
                        paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, shadow.BlurRadius / 2);
                    }
                    
                    canvas.DrawRect(shadowRect, paint);
                }
                
                canvas.Restore();
            }
        }

        /// <summary>
        /// Draw text with word-spacing and letter-spacing support
        /// </summary>
        private void DrawTextWithSpacing(SKCanvas canvas, string text, float x, float y, SKPaint paint, double? wordSpacing, double? letterSpacing)
        {
            if (wordSpacing == null && letterSpacing == null)
            {
                // No spacing adjustments needed, use standard drawing
                try
                {
                    using (var shaper = new SKShaper(paint.Typeface))
                    {
                        canvas.DrawShapedText(shaper, text, x, y, paint);
                    }
                }
                catch
                {
                    canvas.DrawText(text, x, y, paint);
                }
                return;
            }
            
            if (letterSpacing != null && letterSpacing != 0)
            {
                // Draw character by character for letter-spacing
                float currentX = x;
                foreach (char c in text)
                {
                    string charStr = c.ToString();
                    canvas.DrawText(charStr, currentX, y, paint);
                    float charWidth = paint.MeasureText(charStr);
                    currentX += charWidth + (float)letterSpacing.Value;
                }
            }
            else if (wordSpacing != null && wordSpacing != 0)
            {
                // Draw word by word for word-spacing
                var words = text.Split(' ');
                float currentX = x;
                float spaceWidth = paint.MeasureText(" ") + (float)wordSpacing.Value;
                
                for (int i = 0; i < words.Length; i++)
                {
                    if (!string.IsNullOrEmpty(words[i]))
                    {
                        try
                        {
                            using (var shaper = new SKShaper(paint.Typeface))
                            {
                                canvas.DrawShapedText(shaper, words[i], currentX, y, paint);
                            }
                        }
                        catch
                        {
                            canvas.DrawText(words[i], currentX, y, paint);
                        }
                        currentX += paint.MeasureText(words[i]);
                    }
                    
                    if (i < words.Length - 1)
                    {
                        currentX += spaceWidth;
                    }
                }
            }
        }

        /// <summary>
        /// Calculate text width considering word-spacing and letter-spacing
        /// </summary>
        private float MeasureTextWithSpacing(SKPaint paint, string text, double? wordSpacing, double? letterSpacing)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            
            float baseWidth = paint.MeasureText(text);
            
            if (letterSpacing != null && letterSpacing != 0)
            {
                // Add letter-spacing for each character (except last)
                baseWidth += (text.Length - 1) * (float)letterSpacing.Value;
            }
            
            if (wordSpacing != null && wordSpacing != 0)
            {
                // Count spaces and add word-spacing
                int spaceCount = text.Count(c => c == ' ');
                baseWidth += spaceCount * (float)wordSpacing.Value;
            }
            
            return baseWidth;
        }

        /// <summary>
        /// Draw text decoration (underline, overline, line-through)
        /// </summary>
        private void DrawTextDecoration(SKCanvas canvas, TextDecorationParsed deco, SKRect textBox, float fontSize, SKColor textColor)
        {
            if (!deco.Underline && !deco.Overline && !deco.LineThrough) return;
            
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.Color = deco.Color ?? textColor;
                paint.StrokeWidth = Math.Max(1, fontSize / 14);
                paint.Style = SKPaintStyle.Stroke;
                
                // Apply line style
                if (deco.Style == "dashed")
                {
                    paint.PathEffect = SKPathEffect.CreateDash(new float[] { 4, 2 }, 0);
                }
                else if (deco.Style == "dotted")
                {
                    paint.PathEffect = SKPathEffect.CreateDash(new float[] { 1, 2 }, 0);
                }
                
                // Underline
                if (deco.Underline)
                {
                    float y = textBox.Bottom - 2;
                    canvas.DrawLine(textBox.Left, y, textBox.Right, y, paint);
                }
                
                // Overline
                if (deco.Overline)
                {
                    float y = textBox.Top + 2;
                    canvas.DrawLine(textBox.Left, y, textBox.Right, y, paint);
                }
                
                // Line-through
                if (deco.LineThrough)
                {
                    float y = textBox.MidY;
                    canvas.DrawLine(textBox.Left, y, textBox.Right, y, paint);
                }
            }
        }
    }
}
