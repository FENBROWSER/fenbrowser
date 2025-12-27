using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Interaction;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Adapter class that bridges the old SkiaDomRenderer API to the new clean-slate rendering pipeline.
    /// 
    /// This maintains backward compatibility with existing code in Host, UI, and Tests
    /// while internally delegating to LayoutEngine, NewPaintTreeBuilder, and SkiaRenderer.
    /// 
    /// NOTE: This is a transitional class - once all consumers are updated to use
    /// the new pipeline directly, this class should be removed.
    /// </summary>
    public class SkiaDomRenderer
    {
        /// <summary>
        /// Feature flag: set to false to bypass new pipeline (for debugging).
        /// Default: true (new pipeline is active).
        /// </summary>
        public static bool UseNewRenderPipeline { get; set; } = true;
        
        private readonly SkiaRenderer _renderer = new SkiaRenderer();
        private readonly Dictionary<Node, BoxModel> _boxes = new Dictionary<Node, BoxModel>();
        private IReadOnlyDictionary<Node, CssComputed> _lastStyles;
        private LayoutResult _lastLayout;
        private float _viewportWidth;
        private float _viewportHeight;
        
        /// <summary>
        /// Current overlays for input elements.
        /// </summary>
        public List<InputOverlayData> CurrentOverlays { get; } = new List<InputOverlayData>();
        
        /// <summary>
        /// Last layout result.
        /// </summary>
        public LayoutResult LastLayout => _lastLayout;
        
        /// <summary>
        /// Get the layout box for a specific element.
        /// </summary>
        public BoxModel GetElementBox(Node node)
        {
            if (node != null && _boxes.TryGetValue(node, out var box))
                return box;
            return null;
        }
        
        /// <summary>
        /// Main render entry point - performs layout and paint.
        /// </summary>
        public void Render(
            Node root, 
            SKCanvas canvas, 
            Dictionary<Node, CssComputed> styles, 
            SKRect viewport, 
            string baseUrl = null, 
            Action<SKSize, List<InputOverlayData>> onLayoutUpdated = null, 
            SKSize? separateLayoutViewport = null)
        {
            if (root == null || canvas == null) return;
            
            CurrentOverlays.Clear();
            _lastStyles = styles;
            
            float layoutWidth = separateLayoutViewport?.Width ?? viewport.Width;
            float layoutHeight = separateLayoutViewport?.Height ?? viewport.Height;
            
            _viewportWidth = layoutWidth > 0 ? layoutWidth : 1920;
            _viewportHeight = layoutHeight > 0 ? layoutHeight : 1080;
            
            try
            {
                // PHASE 1: Layout using the new LayoutEngine
                var layoutEngine = new LayoutEngine(
                    styles ?? new Dictionary<Node, CssComputed>(),
                    _viewportWidth,
                    _viewportHeight);
                
                _lastLayout = layoutEngine.ComputeLayout(root, _viewportWidth, 
                    _viewportHeight);
                
                // Copy boxes for hit testing
                _boxes.Clear();
                int boxCount = 0;
                foreach (var box in layoutEngine.AllBoxes)
                {
                    _boxes[box.Key] = box.Value;
                    boxCount++;
                }

                // Debug: Dump DOM tree with Boxes
                try { 
                    var sb = new System.Text.StringBuilder();
                    DumpDom(root, 0, sb, styles, _boxes);
                    System.IO.File.WriteAllText(@"C:\Users\udayk\Videos\FENBROWSER\dom_dump.txt", sb.ToString());
                } catch {}
                
                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[SkiaDomRenderer] Copied {boxCount} boxes for rendering.\r\n"); } catch {}
                
                // PHASE 2: Build Paint Tree
                var paintTree = NewPaintTreeBuilder.Build(
                    root,
                    _boxes,
                    styles,
                    _viewportWidth,
                    _viewportHeight);
                
                // PHASE 3: Render
                SKColor bgColor = SKColors.White;
                if (root is Element rootEl && styles != null)
                {
                    // 1. Check root (html) background
                    if (styles.TryGetValue(rootEl, out var rootStyle) && 
                        rootStyle.BackgroundColor.HasValue && 
                        rootStyle.BackgroundColor.Value.Alpha > 0)
                    {
                        bgColor = rootStyle.BackgroundColor.Value;
                    }
                    else
                    {
                        // 2. Propagate body background if html is transparent
                        // Find body element
                        var body = rootEl.Children?.FirstOrDefault(c => c is Element e && e.TagName == "body") as Element;
                        if (body != null && styles.TryGetValue(body, out var bodyStyle) && 
                            bodyStyle.BackgroundColor.HasValue &&
                            bodyStyle.BackgroundColor.Value.Alpha > 0)
                        {
                            bgColor = bodyStyle.BackgroundColor.Value;
                        }
                    }
                }
                
                _renderer.Render(canvas, paintTree, viewport, bgColor);
                
                // Callback with layout info
                float totalHeight = _lastLayout?.ContentHeight ?? _viewportHeight;
                onLayoutUpdated?.Invoke(new SKSize(_viewportWidth, totalHeight), CurrentOverlays);
            }
            catch (Exception ex)
            {
                // Log error but don't crash
                FenLogger.Error($"[SkiaDomRenderer] Render error: {ex.Message}", LogCategory.Rendering);
                canvas.Clear(SKColors.White);
            }
        }
        
        /// <summary>
        /// Hit test at document coordinates.
        /// </summary>
        public bool HitTest(float x, float y, out HitTestResult result)
        {
            result = HitTestResult.None;
            HitTestResult? bestMatch = null;
            float minArea = float.MaxValue;
            
            // Find element at point using cached boxes
            foreach (var kvp in _boxes)
            {
                if (kvp.Value.BorderBox.Contains(x, y))
                {
                    var node = kvp.Key;
                    var element = node as Element;
                    
                    if (element != null)
                    {
                        // Fix for Interaction: Check pointer-events
                        if (_lastStyles != null && _lastStyles.TryGetValue(element, out var style))
                        {
                            if (style.PointerEvents == "none") continue;
                        }
                        
                        // Heuristic: Pick the match with the smallest area (deepest/narrowest element)
                        float area = kvp.Value.BorderBox.Width * kvp.Value.BorderBox.Height;
                        if (area <= minArea)
                        {
                            minArea = area;
                            
                            string href = null;
                            string tagName = element.Tag;
                            string elementId = element.GetAttribute("id");
                            
                            // Check if link
                            bool isLink = false;
                            var linkAncestor = FindLinkAncestor(element);
                            if (linkAncestor != null)
                            {
                                href = linkAncestor.GetAttribute("href");
                                isLink = !string.IsNullOrEmpty(href);
                            }
                            
                            bool isClickable = isLink || tagName == "button" || tagName == "input";
                            bool isFocusable = isClickable || tagName == "input" || tagName == "textarea" || tagName == "select";
                            bool isEditable = tagName == "input" || tagName == "textarea";
                            
                            bestMatch = new HitTestResult(
                                TagName: tagName?.ToLowerInvariant() ?? "",
                                Href: href,
                                Cursor: isLink ? CursorType.Pointer : (isEditable ? CursorType.Text : CursorType.Default),
                                IsClickable: isClickable,
                                IsFocusable: isFocusable,
                                IsEditable: isEditable,
                                ElementId: elementId,
                                NativeElement: element,
                                BoundingBox: kvp.Value.BorderBox
                            );
                        }
                    }
                }
            }
            
            if (bestMatch.HasValue)
            {
                result = bestMatch.Value;
                return true;
            }
            
            return false;
        }
        
        private Element FindLinkAncestor(Element element)
        {
            var current = element;
            while (current != null)
            {
                if (string.Equals(current.Tag, "a", StringComparison.OrdinalIgnoreCase))
                    return current;
                current = current.Parent as Element;
            }
            return null;
        }
        
        /// <summary>
        /// Handle hover state changes.
        /// </summary>
        public void OnHover(Element current, Element previous)
        {
            ElementStateManager.Instance.SetHoveredElement(current);
        }
        
        /// <summary>
        /// Get box model for a node.
        /// </summary>
        public BoxModel GetBox(Node node)
        {
            return _boxes.TryGetValue(node, out var box) ? box : null;
        }

        private static void DumpDom(Node root, int startDepth, System.Text.StringBuilder sb, Dictionary<Node, CssComputed> styles, IReadOnlyDictionary<Node, Layout.BoxModel> boxes = null)
        {
            if (root == null) return;
            
            var stack = new Stack<(Node node, int depth)>();
            stack.Push((root, startDepth));
            
            while (stack.Count > 0)
            {
                var (node, depth) = stack.Pop();
                if (node == null) continue;
                
                string tag = (node as Element)?.TagName ?? (node.IsText ? "#text" : node.NodeName);
                int childCount = node.Children?.Count ?? 0;
                sb.Append(new string(' ', depth * 2))
                  .Append($"[DOM DUMP] Type: {node.NodeType}, Tag: {tag}, Inst: {node.GetHashCode()}, Children: {childCount}");
                
                // Add Styles info
                if (styles != null && styles.TryGetValue(node, out var style))
                {
                    sb.Append($" [Display: {style.Display ?? "null"}, Vis: {style.Visibility ?? "null"}]");
                }
                
                // Add Box info
                if (boxes != null && boxes.TryGetValue(node, out var box) && box != null)
                {
                    sb.Append($" [Box: {box.ContentBox.Width:F1}x{box.ContentBox.Height:F1} @ {box.ContentBox.Left:F1},{box.ContentBox.Top:F1}]");
                }

                // Add Attributes info for Elements
                if (node is Element el && el.Attributes != null && el.Attributes.Count > 0)
                {
                    sb.Append(" {");
                    bool first = true;
                    foreach (var attr in el.Attributes)
                    {
                        if (!first) sb.Append(", ");
                        sb.Append($"{attr.Key}='{attr.Value}'");
                        first = false;
                    }
                    sb.Append("}");
                }
                
                if (node is Text t && !string.IsNullOrWhiteSpace(t.Data))
                {
                    string snippet = t.Data.Length > 20 ? t.Data.Substring(0, 20) + "..." : t.Data;
                    sb.Append(" [").Append(snippet.Replace("\r", "").Replace("\n", " ")).Append("]");
                }
                sb.AppendLine();
                
                if (node.Children != null && node.Children.Count > 0)
                {
                    // Push in reverse order to maintain original iteration order
                    for (int i = node.Children.Count - 1; i >= 0; i--)
                    {
                        stack.Push((node.Children[i], depth + 1));
                    }
                }
            }
        }
    }
}
