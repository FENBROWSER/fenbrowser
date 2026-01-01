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
    /// Constructs an ImmutablePaintTree from LayoutResult and Style data.
    /// 
    /// RESPONSIBILITIES:
    /// - Convert LayoutResult → ImmutablePaintTree
    /// - Resolve stacking contexts
    /// - Apply display:none filtering
    /// - Handle overflow clipping
    /// - Enforce z-index ordering (strict paint order)
    /// - Create opacity groups
    /// - Flatten inline content to positioned glyphs
    /// 
    /// MUST NEVER:
    /// - Call Skia
    /// - Mutate layout
    /// - Interpret CSS rules (beyond what's already computed)
    /// - Execute JS
    /// </summary>
    public sealed class NewPaintTreeBuilder
    {
        private readonly IReadOnlyDictionary<Node, Layout.BoxModel> _boxes;
        private readonly IReadOnlyDictionary<Node, CssComputed> _styles;
        private readonly float _viewportWidth;
        private readonly float _viewportHeight;
        private readonly string _baseUri;
        private int _frameId;
        
        private NewPaintTreeBuilder(
            IReadOnlyDictionary<Node, Layout.BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            float viewportWidth,
            float viewportHeight,
            string baseUri)
        {
            _boxes = boxes ?? throw new ArgumentNullException(nameof(boxes));
            _styles = styles ?? new Dictionary<Node, CssComputed>();
            _viewportWidth = viewportWidth;
            _viewportHeight = viewportHeight;
            _baseUri = baseUri;
        }
        
        /// <summary>
        /// Builds an immutable paint tree from layout and style data.
        /// </summary>
        public static ImmutablePaintTree Build(
            Node root,
            IReadOnlyDictionary<Node, Layout.BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            float viewportWidth,
            float viewportHeight,
            string baseUri = null,
            int frameId = 0)
        {
            FenBrowser.Core.FenLogger.Debug($"[PAINT-TREE] Build called. Root={(root != null ? root.GetType().Name : "NULL")} Boxes={boxes?.Count} Styles={styles?.Count}");
            if (root == null || boxes == null || boxes.Count == 0)
            {
                return ImmutablePaintTree.Empty;
            }
            
            var builder = new NewPaintTreeBuilder(boxes, styles, viewportWidth, viewportHeight, baseUri);
            builder._frameId = frameId;
            
            // Build the root stacking context
            var rootContext = new BuilderStackingContext(root);
            builder.BuildRecursive(root, rootContext);
            
            // Flatten stacking contexts into paint order
            var rootNodes = rootContext.Flatten();
            
            /* [PERF-REMOVED] */

            return new ImmutablePaintTree(rootNodes, frameId);
        }
        
        /// <summary>
        /// Recursively builds paint nodes for an element and its children.
        /// </summary>
        private void BuildRecursive(Node node, BuilderStackingContext currentContext)
        {
            try { System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); }
            catch (InsufficientExecutionStackException) { return; }

            if (node == null) return;
            
            // Process children for Document/Fragment even if they don't have boxes themselves
            if (node is Document || node is DocumentFragment)
            {
                ProcessChildren(node, currentContext);
                return;
            }
            
            // Only process Elements and Text nodes for actual painting
            if (!(node is Element) && !(node is Text)) return;
            
            // Get box model - if no box, element is not rendered (e.g., display:none)
            if (!_boxes.TryGetValue(node, out var box) || box == null) return;
            
            // Get computed style
            _styles.TryGetValue(node, out var style);
            
            // Skip hidden elements
            if (ShouldHide(node, style)) return;
            
            // Determine if this creates a new stacking context
            bool createsStackingContext = DetermineCreatesStackingContext(style);
            int zIndex = style?.ZIndex ?? 0;
            
            // Build paint nodes for this element
            var paintNodes = BuildPaintNodesForElement(node, box, style);
            
            if (createsStackingContext)
            {
                // Create a new stacking context
                var childContext = new BuilderStackingContext(node)
                {
                    ZIndex = zIndex,
                    PaintNodes = paintNodes
                };
                
                // Add to parent context based on z-index
                currentContext.AddChildContext(childContext);
                
                // Process children in the new context
                ProcessChildren(node, childContext);
            }
            else
            {
                // Check if positioned (affects paint order)
                string pos = style?.Position?.ToLowerInvariant();
                bool isPositioned = pos == "absolute" || pos == "fixed";
                
                if (isPositioned)
                {
                    // Positioned elements go to a special layer in the current context
                    currentContext.AddPositionedNodes(paintNodes, zIndex);
                }
                else
                {
                    // Normal flow - add to current context
                    currentContext.AddFlowNodes(paintNodes);
                }
                
                // Process children in the current context
                ProcessChildren(node, currentContext);
            }
        }
        
        private void ProcessChildren(Node node, BuilderStackingContext context)
        {
            // Form controls (INPUT, TEXTAREA) are replaced elements; we handle their content rendering explicitly.
            // Skipping children prevents double-rendering of text.
            if (node is Element e && (e.TagName?.ToUpperInvariant() == "TEXTAREA" || e.TagName?.ToUpperInvariant() == "INPUT"))
                return;

            if (node != null && node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    BuildRecursive(child, context);
                }
            }
        }
        
        /// <summary>
        /// Builds concrete paint nodes for an element.
        /// </summary>
        private List<PaintNodeBase> BuildPaintNodesForElement(Node node, Layout.BoxModel box, CssComputed style)
        {
            var nodes = new List<PaintNodeBase>();
            SKRect bounds = box.BorderBox;
            
            // Poll interactive state
            Element elemNode = node as Element;
            bool isFocused = elemNode != null && ElementStateManager.Instance.IsFocused(elemNode);
            bool isHovered = elemNode != null && ElementStateManager.Instance.IsHovered(elemNode);
            
            // 0. Shadow (below background)
            var shadowNode = BuildBoxShadowNode(node, bounds, style);
            if (shadowNode != null) nodes.Add(shadowNode);

            // 1. Background
            var bgNode = BuildBackgroundNode(node, bounds, style, isFocused, isHovered);
            if (bgNode != null) nodes.Add(bgNode);
            
            // 2. Border
            var borderNode = BuildBorderNode(node, bounds, style, isFocused, isHovered);
            if (borderNode != null) nodes.Add(borderNode);
            
            // 3. Content (text or image)
            if (node is Text textNode)
            {
                var textNodes = BuildTextNode(textNode, box, style, isFocused, isHovered);
                if (textNodes != null) nodes.AddRange(textNodes);
            }
            else if (node is Element elem)
            {
                string tagUpper = elem.Tag?.ToUpperInvariant();
                if (tagUpper == "IMG")
                {
                    FenBrowser.Core.FenLogger.Debug($"[PAINT-BUILD] Found IMG element id={elem.GetAttribute("id")} src={(elem.GetAttribute("src")?.Length > 60 ? elem.GetAttribute("src")?.Substring(0,60) + "..." : elem.GetAttribute("src"))}");
                }
                if (IsImageElement(elem) || tagUpper == "SVG")
                {
                    var imageNode = BuildImageOrSvgNode(elem, box, style, isFocused, isHovered);
                    if (imageNode != null) nodes.Add(imageNode);
                }
                else if (elem.Tag?.ToUpperInvariant() == "INPUT" || elem.Tag?.ToUpperInvariant() == "TEXTAREA")
                {
                    // Inputs are still single line for now (simple implementation)
                    var inputNode = BuildInputTextNode(elem, box, style, isFocused, isHovered);
                    if (inputNode != null) nodes.Add(inputNode);
                }
            }
            
            // Wrap in OpacityGroupPaintNode if needed (group-based opacity only)
            if (style?.Opacity.HasValue == true && style.Opacity.Value < 1.0)
            {
                var groupNode = new OpacityGroupPaintNode
                {
                    Bounds = bounds,
                    Opacity = (float)style.Opacity.Value,
                    Children = nodes
                };
                return new List<PaintNodeBase> { groupNode };
            }
            
            return nodes;
        }

        private BackgroundPaintNode BuildBackgroundNode(Node node, SKRect bounds, CssComputed style, bool isFocused, bool isHovered)
        {
            SKColor? bgColor = style?.BackgroundColor;
            
            // UA Defaults if transparent
            if (bgColor == null || bgColor.Value.Alpha == 0)
            {
                if (node is Element e)
                {
                    string tag = e.Tag?.ToUpperInvariant();
                    string type = e.GetAttribute("type")?.ToLowerInvariant();
                    
                    if (tag == "INPUT" && type != "hidden")
                    {
                        // Default Inputs to White
                         bgColor = SKColors.White;
                    }
                    else if (tag == "BUTTON" || (tag == "INPUT" && (type == "button" || type == "submit" || type == "reset")))
                    {
                        // Default Buttons to Light Gray
                        bgColor = new SKColor(240, 240, 240);
                    }
                }
            }
            
            if (bgColor == null || bgColor.Value.Alpha == 0)
                return null;
            
            return new BackgroundPaintNode
            {
                Bounds = bounds,
                SourceNode = node,
                Color = bgColor,
                BorderRadius = ExtractBorderRadius(style),
                IsFocused = isFocused,
                IsHovered = isHovered
            };
        }
        
        private BorderPaintNode BuildBorderNode(Node node, SKRect bounds, CssComputed style, bool isFocused, bool isHovered)
        {
            float[] widths = null;
            if (style != null)
            {
                var bt = style.BorderThickness;
                widths = new float[4] { (float)bt.Top, (float)bt.Right, (float)bt.Bottom, (float)bt.Left };
            }

            // UA Defaults
            if (widths == null || widths.All(w => w <= 0))
            {
                 if (node is Element e)
                 {
                    string tag = e.Tag?.ToUpperInvariant();
                    string type = e.GetAttribute("type")?.ToLowerInvariant();
                    
                    if ((tag == "INPUT" && type != "hidden") || tag == "BUTTON" || (tag == "INPUT" && (type == "button" || type == "submit" || type == "reset")))
                    {
                        // Default Border
                        widths = new float[4] { 1, 1, 1, 1 };
                    }
                 }
            }

            if (widths == null || widths.All(w => w <= 0)) return null;
            
            SKColor borderColor = style.BorderBrushColor ?? SKColors.Black;
            SKColor[] colors = new SKColor[4]
            {
                borderColor,
                borderColor,
                borderColor,
                borderColor
            };
            
            string[] styles = new string[4]
            {
                style.BorderStyleTop ?? "solid",
                style.BorderStyleRight ?? "solid",
                style.BorderStyleBottom ?? "solid",
                style.BorderStyleLeft ?? "solid"
            };
            
            return new BorderPaintNode
            {
                Bounds = bounds,
                SourceNode = node,
                Widths = widths,
                Colors = colors,
                Styles = styles,
                BorderRadius = ExtractBorderRadius(style),
                IsFocused = isFocused,
                IsHovered = isHovered
            };
        }

        private BoxShadowPaintNode BuildBoxShadowNode(Node node, SKRect bounds, CssComputed style)
        {
            if (string.IsNullOrEmpty(style?.BoxShadow) || style.BoxShadow == "none") return null;
            return ParseBoxShadow(node, style.BoxShadow, bounds, ExtractBorderRadius(style));
        }

        private BoxShadowPaintNode ParseBoxShadow(Node node, string shadowStr, SKRect bounds, float[] borderRadius)
        {
            try
            {
                // Basic parser for "0 2px 4px rgba(0,0,0,0.2)"
                // TODO: Support multiple shadows (comma separated)
                var parts = shadowStr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) return null;

                float offsetX = 0, offsetY = 0, blur = 0, spread = 0;
                SKColor color = SKColors.Black;
                bool inset = shadowStr.Contains("inset");
                
                int valIndex = 0;
                if (parts[0] == "inset") valIndex++;

                // Parse lengths
                if (valIndex < parts.Length && float.TryParse(parts[valIndex].Replace("px", ""), out float v1)) { offsetX = v1; valIndex++; }
                if (valIndex < parts.Length && float.TryParse(parts[valIndex].Replace("px", ""), out float v2)) { offsetY = v2; valIndex++; }
                if (valIndex < parts.Length && float.TryParse(parts[valIndex].Replace("px", ""), out float v3)) { blur = v3; valIndex++; }
                if (valIndex < parts.Length && float.TryParse(parts[valIndex].Replace("px", ""), out float v4)) { spread = v4; valIndex++; }

                // Attempt to parse color from remaining parts
                // This is tricky because color can be "rgba(0, 0, 0, 0.2)" which has spaces
                string colorStr = "";
                for (int i = valIndex; i < parts.Length; i++)
                {
                    if (parts[i] == "inset") { inset = true; continue; }
                    colorStr += parts[i];
                }

                if (!string.IsNullOrEmpty(colorStr))
                {
                   if (colorStr.StartsWith("rgba")) 
                   {
                       // Re-assemble rgba string if split
                       int start = shadowStr.IndexOf("rgba");
                       if (start >= 0) {
                           int end = shadowStr.IndexOf(")", start);
                           if (end > start) colorStr = shadowStr.Substring(start, end - start + 1);
                       }
                   }
                   
                   SKColor.TryParse(colorStr, out color);
                }

                return new BoxShadowPaintNode
                {
                    Bounds = bounds,
                    SourceNode = node,
                    Offset = new SKPoint(offsetX, offsetY),
                    Blur = blur,
                    Spread = spread,
                    Color = color,
                    BorderRadius = borderRadius,
                    Inset = inset
                };
            }
            catch 
            {
                return null;
            }
        }
        
        // CHANGED: Returned list of nodes to support multi-line text
        private List<TextPaintNode> BuildTextNode(FenBrowser.Core.Dom.Text textNode, Layout.BoxModel box, CssComputed style, bool isFocused, bool isHovered)
        {
            if (string.IsNullOrEmpty(textNode.Data)) return null;
            
            // Get parent style for text rendering
            var parentStyle = style;
            if (parentStyle == null && textNode.Parent != null)
            {
                _styles.TryGetValue(textNode.Parent, out parentStyle);
            }
            
            string fontFamily = parentStyle?.FontFamilyName;
            int weight = parentStyle?.FontWeight ?? 400;
            SKFontStyleSlant slant = (parentStyle?.FontStyle == SKFontStyleSlant.Italic) ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;

            var typeface = TextLayoutHelper.ResolveTypeface(fontFamily, textNode.Data, weight, slant);
            
            // RULE 2: FenEngine controls font size and line-height, not external libraries
            // Enforce minimum 16px font size for readable text
            float fontSize = (float)(parentStyle?.FontSize ?? 16);
            if (fontSize < 10) fontSize = 16f; // Force readable minimum
            
            // Extract text decorations
            List<string> textDecorations = null;
            string decorValue = parentStyle?.TextDecoration;
            
            if (!string.IsNullOrWhiteSpace(decorValue) && !decorValue.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                textDecorations = decorValue.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            }
            else
            {
                // UA Default: Underline for links
                var ancestor = textNode.Parent as Element;
                bool inNav = false;
                Element anchorEl = null;
                while (ancestor != null)
                {
                    string tag = ancestor.Tag?.ToUpperInvariant();
                    if (tag == "NAV" || tag == "HEADER" || ancestor.Id == "main-nav" || ancestor.Id == "site") inNav = true;
                    if (tag == "A") anchorEl = ancestor;
                    ancestor = ancestor.Parent as Element;
                }
                
                if (anchorEl != null && !inNav)
                {
                    string href = anchorEl.GetAttribute("href");
                    if (!string.IsNullOrWhiteSpace(href)) textDecorations = new List<string> { "underline" };
                }
            }
            
            // Text color
             SKColor color = parentStyle?.ForegroundColor ?? SKColors.Black;

            // MULTI-LINE SUPPORT
            // If BoxModel has Lines populated (from TextLayoutComputer), use them.
            if (box.Lines != null && box.Lines.Count > 0)
            {
                var list = new List<TextPaintNode>();
                foreach (var line in box.Lines)
                {
                    // Calculate absolute bounds for this line
                    float absX = box.ContentBox.Left + line.Origin.X;
                    float absY = box.ContentBox.Top + line.Origin.Y;
                    var lineBounds = new SKRect(absX, absY, absX + line.Width, absY + line.Height);
                    
                    // Origin for text drawing (Baseline)
                    var textOrigin = new SKPoint(absX, absY + line.Baseline);

                    list.Add(new TextPaintNode
                    {
                        Bounds = lineBounds,
                        SourceNode = textNode,
                        Color = color,
                        FontSize = fontSize,
                        Typeface = typeface,
                        TextOrigin = textOrigin,
                        FallbackText = line.Text, // Each line node holds its own text segment
                        TextDecorations = textDecorations,
                        IsFocused = isFocused,
                        IsHovered = isHovered
                    });
                }
                return list;
            }

            // FALLBACK (Single Line) - OLD LOGIC
            string displayText = System.Text.RegularExpressions.Regex.Replace(textNode.Data, @"\s+", " ");
            if (displayText.Contains("&#"))
            {
                displayText = displayText.Replace("&#10003;", "✔")
                                         .Replace("&#x2713;", "✔")
                                         .Replace("&#10004;", "✔")
                                         .Replace("&#x2714;", "✔");
            }
            if (displayText.Contains("&amp;")) displayText = displayText.Replace("&amp;", "&");
            
            return new List<TextPaintNode> 
            {
                new TextPaintNode
                {
                    Bounds = box.ContentBox,
                    SourceNode = textNode,
                    Color = color,
                    FontSize = fontSize,
                    Typeface = typeface,
                    TextOrigin = new SKPoint(box.ContentBox.Left, box.ContentBox.Top + fontSize * 0.9f),
                    FallbackText = displayText,
                    TextDecorations = textDecorations,
                    IsFocused = isFocused,
                    IsHovered = isHovered
                }
            };
        }
        
        private ImagePaintNode BuildImageOrSvgNode(Element elem, Layout.BoxModel box, CssComputed style, bool isFocused, bool isHovered)
        {
            string url = null;
            string tag = elem.Tag?.ToUpperInvariant();
            
            if (tag == "IMG")
            {
                url = elem.GetAttribute("src");
                
                // Resolve Relative URLs
                if (!string.IsNullOrEmpty(url) && 
                    !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && 
                    !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && 
                    !string.IsNullOrEmpty(_baseUri))
                {
                    try 
                    {
                        if (url.StartsWith("//"))
                        {
                            var scheme = new Uri(_baseUri).Scheme;
                            url = scheme + ":" + url;
                        }
                        else if (url.StartsWith("/"))
                        {
                            var uri = new Uri(_baseUri);
                            url = $"{uri.Scheme}://{uri.Host}{url}";
                        }
                        else
                        {
                            var uri = new Uri(_baseUri);
                            url = $"{uri.Scheme}://{uri.Host}/{url}";
                        }
                    }
                    catch {}
                }

                FenBrowser.Core.FenLogger.Debug($"[IMG-BUILD] Tag={tag} URL={(url?.Length > 80 ? url?.Substring(0, 80) + "..." : url)}");
                FenBrowser.Core.FenLogger.Debug($"[IMG-BUILD] box.ContentBox={box.ContentBox.Width}x{box.ContentBox.Height} @ {box.ContentBox.Left},{box.ContentBox.Top}");
                var bitmap = ImageLoader.GetImage(url);
                FenBrowser.Core.FenLogger.Debug($"[IMG-BUILD] Bitmap={(bitmap != null ? $"{bitmap.Width}x{bitmap.Height}" : "NULL")}");
                
                // Image loading is handled upstream - we just reference the bounds
                return new ImagePaintNode
                {
                    Bounds = box.ContentBox,
                    Bitmap = bitmap,
                    ObjectFit = style?.ObjectFit ?? "fill"
                };
            }
            else if (tag == "SVG")
            {
                // Parse SVG attributes for better rasterization
                float? svgW = null;
                float? svgH = null;
                if (elem.Attributes != null)
                {
                    if (elem.Attributes.TryGetValue("width", out var wStr) && float.TryParse(wStr, out var w)) svgW = w;
                    if (elem.Attributes.TryGetValue("height", out var hStr) && float.TryParse(hStr, out var h)) svgH = h;
                    
                    // Parse viewBox for intrinsic dimensions
                    string viewBox = null;
                    if (elem.Attributes.TryGetValue("viewBox", out viewBox) || elem.Attributes.TryGetValue("viewbox", out viewBox))
                    {
                        var parts = viewBox.Split(new[] {' ', ','}, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4)
                        {
                            if (!svgW.HasValue && float.TryParse(parts[2], out var vbW)) svgW = vbW;
                            if (!svgH.HasValue && float.TryParse(parts[3], out var vbH)) svgH = vbH;
                        }
                    }
                }

                // Use intrinsic SVG dimensions if available, otherwise use box (but clamp to prevent massive renders)
                float finalW = svgW ?? box.ContentBox.Width;
                float finalH = svgH ?? box.ContentBox.Height;
                
                // Clamp to reasonable SVG icon size to prevent massive rasterizations
                if (finalW > 200) finalW = svgW ?? 24;
                if (finalH > 200) finalH = svgH ?? 24;
                
                if (finalW <= 0) finalW = 24;
                if (finalH <= 0) finalH = 24;

                string svgContent = elem.ToHtml();
                
                // CRITICAL FIX: Replace 'currentColor' with actual resolved color
                if (style != null && svgContent.Contains("currentColor"))
                {
                    var fg = style.ForegroundColor ?? SKColors.Black;
                    string hexColor = $"#{fg.Red:X2}{fg.Green:X2}{fg.Blue:X2}";
                    svgContent = svgContent.Replace("currentColor", hexColor);
                    svgContent = svgContent.Replace("fill=\"currentColor\"", $"fill=\"{hexColor}\"");
                    svgContent = svgContent.Replace("stroke=\"currentColor\"", $"stroke=\"{hexColor}\"");
                }
                
                // CRITICAL FIX: Fallback for 'var(--bbQxAb)' (Google specific) or generic vars
                if (svgContent.Contains("var("))
                {
                    // For now, naive fallback to foreground color if variable resolution fails
                    // This handles the Google search icon issue
                     var fg = style.ForegroundColor ?? SKColors.Black;
                     string hexColor = $"#{fg.Red:X2}{fg.Green:X2}{fg.Blue:X2}";
                     
                     if (svgContent.Contains("--bbQxAb"))
                     {
                         // var(--bbQxAb) is usually grey/blue on Google. Let's use #5f6368 (Google Grey 700) or style color.
                         svgContent = svgContent.Replace("var(--bbQxAb)", "#5f6368");
                         /* [PERF-REMOVED] */
                     }
                }

                string dataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svgContent));
                
                var tuple = ImageLoader.GetImageTuple(dataUri, false, null, (int)finalW, (int)finalH);
                var (bitmap, isLazy) = ((SKBitmap, bool))tuple;
                
                try { 
                    if (bitmap == null) System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[SVG-DEBUG] Bitmap is NULL for SVG. Content: {svgContent.Substring(0, Math.Min(50, svgContent.Length))}...\r\n");
                    else System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[SVG-DEBUG] Created bitmap {bitmap.Width}x{bitmap.Height} for SVG {finalW}x{finalH}. Color fixed? {svgContent.Contains("#")}\r\n");
                } catch {}

                return new ImagePaintNode
                {
                    Bounds = box.ContentBox,
                    SourceNode = elem,
                    Bitmap = bitmap,
                    ObjectFit = "contain",
                    IsFocused = isFocused,
                    IsHovered = isHovered
                };
            }
            return null; // No image or SVG to paint
        }



        private TextPaintNode BuildInputTextNode(Element elem, Layout.BoxModel box, CssComputed style, bool isFocused, bool isHovered)
        {
             // Enhanced input support
             string type = elem.GetAttribute("type")?.ToLowerInvariant() ?? "text";
             string[] validTypes = { "text", "search", "password", "email", "url", "tel", "submit", "button", "reset" };
             if (!validTypes.Contains(type)) return null;

             string value = elem.GetAttribute("value");
             string placeholder = elem.GetAttribute("placeholder");
             
             // Textarea text extraction (from children)
             if (type == "text" && elem.TagName?.ToUpperInvariant() == "TEXTAREA" && string.IsNullOrEmpty(value))
             {
                 if (elem.Children != null)
                 {
                     System.Text.StringBuilder sb = new System.Text.StringBuilder();
                     foreach(var c in elem.Children) { if (c is Text t) sb.Append(t.Data); }
                     value = sb.ToString();
                 }
             }

             // Use placeholder if value is empty
             bool isPlaceholder = false;
             if (string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(placeholder))
             {
                 value = placeholder;
                 isPlaceholder = true;
             }
             
             if (string.IsNullOrEmpty(value)) return null;
             
             // Collapse whitespace for input values too
             value = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ");

             string fontFamily = style?.FontFamilyName;
             int weight = style?.FontWeight ?? 400;
             SKFontStyleSlant slant = (style?.FontStyle == SKFontStyleSlant.Italic) ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;

             var typeface = TextLayoutHelper.ResolveTypeface(fontFamily, value, weight, slant);
             float fontSize = (float)(style?.FontSize ?? 13.3333f); // 13.33px is default user agent font size
             
             using var paint = new SKPaint { Typeface = typeface, TextSize = fontSize, IsAntialias = true };
             float textWidth = paint.MeasureText(value);
             
             // Fix: Use ContentBox to respect padding
             float x = box.ContentBox.Left;
             // Fix: Center vertically within the ContentBox (assuming single line)
             float y = box.ContentBox.Top + (box.ContentBox.Height / 2) + (fontSize * 0.35f);

             // Alignment logic
             SKTextAlign align = style?.TextAlign ?? SKTextAlign.Left;
             
             // Defaults based on type
             if (align == SKTextAlign.Left && (type == "submit" || type == "button" || type == "reset"))
             {
                 align = SKTextAlign.Center;
             }

             if (align == SKTextAlign.Center)
             {
                 // Center within ContentBox
                 x = box.ContentBox.Left + (box.ContentBox.Width - textWidth) / 2;
             }
             else if (align == SKTextAlign.Right)
             {
                 // Right align within ContentBox
                 x = box.ContentBox.Right - textWidth;
             }
             // else Left: x is already ContentBox.Left

             // Color
             SKColor textColor = style?.ForegroundColor ?? SKColors.Black;
             if (isPlaceholder)
             {
                 // Dim placeholder text
                 textColor = new SKColor(textColor.Red, textColor.Green, textColor.Blue, (byte)(textColor.Alpha * 0.6));
             }

             return new TextPaintNode
             {
                 Bounds = box.PaddingBox, // Clip to padding box? Or ContentBox? Text usually allowed to overflow into padding? Clipping usually happens at BorderBox.
                 Color = textColor,
                 FontSize = fontSize,
                 Typeface = typeface,
                 TextOrigin = new SKPoint(x, y),
                 FallbackText = type == "password" ? new string('●', value.Length) : value,
                 IsFocused = isFocused,
                 IsHovered = isHovered
             };
        }
        
        private static float[] ExtractBorderRadius(CssComputed style)
        {
            if (style == null) return null;
            
            var br = style.BorderRadius;
            if (br.TopLeft == 0 && br.TopRight == 0 && br.BottomRight == 0 && br.BottomLeft == 0)
                return null;
            
            return new float[]
            {
                (float)br.TopLeft,
                (float)br.TopRight,
                (float)br.BottomRight,
                (float)br.BottomLeft
            };
        }
        
        private static bool IsImageElement(Element elem)
        {
            return elem.Tag?.ToUpperInvariant() == "IMG";
        }
        
        private static bool ShouldHide(Node node, CssComputed style)
        {
            if (node == null) return true;
            
            // Use string comparison for Display and Visibility
            if (style != null && string.Equals(style.Display, "none", StringComparison.OrdinalIgnoreCase)) return true;
            if (style != null && (string.Equals(style.Visibility, "hidden", StringComparison.OrdinalIgnoreCase) || 
                                 string.Equals(style.Visibility, "collapse", StringComparison.OrdinalIgnoreCase))) return true;
            
            string tag = node.NodeName?.ToUpperInvariant();
            return tag == "HEAD" || tag == "SCRIPT" || tag == "STYLE" || tag == "META" || tag == "LINK" || tag == "TITLE" || tag == "NOSCRIPT" || tag == "IFRAME";
        }
        
        /// <summary>
        /// Determines if an element creates a new stacking context.
        /// CSS 2.1 + CSS3 rules.
        /// </summary>
        private static bool DetermineCreatesStackingContext(CssComputed style)
        {
            if (style == null) return false;
            
            string pos = style.Position?.ToLowerInvariant();
            
            // position: fixed or sticky always creates stacking context
            if (pos == "fixed" || pos == "sticky") return true;
            
            // position: absolute or relative with z-index != auto creates stacking context
            if ((pos == "absolute" || pos == "relative") && style.ZIndex.HasValue)
                return true;
            
            // opacity < 1 creates stacking context
            if (style.Opacity.HasValue && style.Opacity.Value < 1.0)
                return true;
            
            // transform != none creates stacking context
            if (!string.IsNullOrEmpty(style.Transform) && style.Transform != "none")
                return true;
            
            // will-change with certain values creates stacking context
            if (!string.IsNullOrEmpty(style.WillChange) && style.WillChange != "auto")
                return true;
            
            return false;
        }
        
        /// <summary>
        /// Internal stacking context for building.
        /// Gathers nodes and child contexts, then flattens to paint order.
        /// </summary>
        private class BuilderStackingContext
        {
            public Node SourceNode { get; }
            public int ZIndex { get; set; }
            public List<PaintNodeBase> PaintNodes { get; set; } = new List<PaintNodeBase>();
            
            // Categorized by paint order (CSS spec)
            private readonly List<BuilderStackingContext> _negativeZContexts = new List<BuilderStackingContext>();
            private readonly List<PaintNodeBase> _flowNodes = new List<PaintNodeBase>();
            private readonly List<(List<PaintNodeBase> nodes, int zIndex)> _positionedNodes = new List<(List<PaintNodeBase>, int)>();
            private readonly List<BuilderStackingContext> _zeroZContexts = new List<BuilderStackingContext>();
            private readonly List<BuilderStackingContext> _positiveZContexts = new List<BuilderStackingContext>();
            
            public BuilderStackingContext(Node source)
            {
                SourceNode = source;
            }
            
            public void AddChildContext(BuilderStackingContext child)
            {
                if (child.ZIndex < 0)
                    _negativeZContexts.Add(child);
                else if (child.ZIndex == 0)
                    _zeroZContexts.Add(child);
                else
                    _positiveZContexts.Add(child);
            }
            
            public void AddFlowNodes(List<PaintNodeBase> nodes)
            {
                _flowNodes.AddRange(nodes);
            }
            
            public void AddPositionedNodes(List<PaintNodeBase> nodes, int zIndex)
            {
                _positionedNodes.Add((nodes, zIndex));
            }
            
            /// <summary>
            /// Flattens this stacking context into paint order per CSS spec:
            /// 1. Background & borders of root
            /// 2. Negative z-index children
            /// 3. In-flow content (block, float, inline)
            /// 4. Positioned children with z-index:auto or 0
            /// 5. Positive z-index children
            /// </summary>
            public List<PaintNodeBase> Flatten()
            {
                try { System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); }
                catch (InsufficientExecutionStackException) { return new List<PaintNodeBase>(); }

                var result = new List<PaintNodeBase>();
                
                // 1. Own paint nodes (background, border)
                result.AddRange(PaintNodes);
                
                // 2. Negative z-index stacking contexts (sorted)
                foreach (var ctx in _negativeZContexts.OrderBy(c => c.ZIndex))
                {
                    result.AddRange(ctx.Flatten());
                }
                
                // 3. In-flow content
                result.AddRange(_flowNodes);
                
                // 4. Zero z-index stacking contexts
                foreach (var ctx in _zeroZContexts)
                {
                    result.AddRange(ctx.Flatten());
                }
                
                // 4b. Positioned elements (sorted by z-index)
                foreach (var (nodes, _) in _positionedNodes.OrderBy(p => p.zIndex))
                {
                    result.AddRange(nodes);
                }
                
                // 5. Positive z-index stacking contexts (sorted)
                foreach (var ctx in _positiveZContexts.OrderBy(c => c.ZIndex))
                {
                    result.AddRange(ctx.Flatten());
                }
                
                return result;
            }
        }
    }
}
