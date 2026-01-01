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
        private ImmutablePaintTree _lastPaintTree;
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
            
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            try
            {
                // PHASE 1: Layout using the new LayoutEngine
                var layoutEngine = new LayoutEngine(
                    styles ?? new Dictionary<Node, CssComputed>(),
                    _viewportWidth,
                    _viewportHeight,
                    null,
                    baseUrl);
                
                _lastLayout = layoutEngine.ComputeLayout(root, _viewportWidth, 
                    _viewportHeight);
                FenLogger.Debug($"[PERF] Layout Phase: {stopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);
                stopwatch.Restart();
                
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
                    // Standardized: using FenLogger instead of hardcoded paths
                    FenLogger.Debug($"[SkiaDomRenderer] DOM Dump: {sb}", LogCategory.Rendering);
                } catch {}
                
                FenLogger.Debug($"[SkiaDomRenderer] Copied {boxCount} boxes for rendering.", LogCategory.Rendering);
                
                // PHASE 2: Build Paint Tree
                FenLogger.Debug($"[SkiaDomRenderer] Invoke NewPaintTreeBuilder... Root={root.GetType().Name} BoxCount={_boxes.Count}");
                var paintTree = NewPaintTreeBuilder.Build(
                    root,
                    _boxes,
                    styles,
                    _viewportWidth,
                    _viewportHeight,
                    baseUrl);
                _lastPaintTree = paintTree;
                FenLogger.Debug($"[PERF] Paint Tree Phase: {stopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);
                stopwatch.Restart();
                
                // PHASE 3: Render
                SKColor bgColor = SKColors.White;
                
                try 
                {
                    string log = $"[Render] Root={root?.GetType().Name}, StylesNull={styles==null}, Count={styles?.Count}\n";
                    if (root is Element rootEl && styles != null)
                    {
                        if (styles.TryGetValue(rootEl, out var rootStyle) && rootStyle != null)
                        {
                            log += $"HTML Style Found. BG={rootStyle.BackgroundColor}\n";
                            if (rootStyle.Map.ContainsKey("background")) log += $"  Map[background] = '{rootStyle.Map["background"]}'\n";
                            if (rootStyle.Map.ContainsKey("background-color")) log += $"  Map[background-color] = '{rootStyle.Map["background-color"]}'\n";
                            log += $"  Tag={rootEl.Tag}\n";

                            if (rootStyle.BackgroundColor.HasValue && rootStyle.BackgroundColor.Value.Alpha > 0)
                            {
                                bgColor = rootStyle.BackgroundColor.Value;
                                log += $"-> Using HTML BG: {bgColor}\n";
                            }
                        }
                        else { log += "HTML Style NOT Found.\n"; }

                        if (bgColor == SKColors.White)
                        {
                            var body = rootEl.Children?.FirstOrDefault(c => c is Element e && e.TagName?.ToLowerInvariant() == "body") as Element;
                            log += $"Body Element={body!=null}\n";
                            if (body != null)
                            {
                                if (styles.TryGetValue(body, out var bodyStyle) && bodyStyle != null)
                                {
                                    log += $"BODY Style Found. BG={bodyStyle.BackgroundColor}\n";
                                    if (bodyStyle.BackgroundColor.HasValue && bodyStyle.BackgroundColor.Value.Alpha > 0)
                                    {
                                        bgColor = bodyStyle.BackgroundColor.Value;
                                        log += $"-> Using BODY BG: {bgColor}\n";
                                    }
                                }
                                else { log += "BODY Style NOT Found.\n"; }
                            }
                        }
                    }

                } 
                catch {}
                
                _renderer.Render(canvas, paintTree, viewport, bgColor);
                FenLogger.Debug($"[PERF] Skia Draw Phase: {stopwatch.ElapsedMilliseconds}ms", LogCategory.Rendering);
                
                // Callback with layout info
                float totalHeight = _lastLayout?.ContentHeight ?? _viewportHeight;
                onLayoutUpdated?.Invoke(new SKSize(_viewportWidth, totalHeight), CurrentOverlays);
            }
            catch (Exception ex)
            {
                // Log error but don't crash
                FenLogger.Error($"[SkiaDomRenderer] Render error: {ex}", LogCategory.Rendering);
                canvas.Clear(SKColors.White);
            }
        }
        
        /// <summary>
        /// Hit test at document coordinates.
        /// </summary>
        public bool HitTest(float x, float y, out HitTestResult result)
        {
            result = HitTestResult.None;
            if (_lastPaintTree == null || _lastPaintTree.Roots == null) return false;

            // Traverse paint nodes in REVERSE order (top-to-bottom)
            var allNodes = new List<PaintNodeBase>();
            CollectAllNodes(_lastPaintTree.Roots, allNodes);

            for (int i = allNodes.Count - 1; i >= 0; i--)
            {
                var ptNode = allNodes[i];
                if (ptNode.Bounds.Contains(x, y))
                {
                    var domNode = ptNode.SourceNode;
                    var element = domNode as Element;
                    if (element == null && domNode?.Parent is Element parentEl) element = parentEl;

                    if (element != null)
                    {
                        if (_lastStyles != null && _lastStyles.TryGetValue(element, out var style))
                        {
                            if (style.PointerEvents == "none") continue;
                        }

                        // Found the top-most interactive element
                        string href = null;
                        var interactive = FindInteractiveAncestor(element);
                        string tagName = element.Tag;
                        string elementId = element.GetAttribute("id");

                        if (interactive != null)
                        {
                            if (interactive.Tag == "a")
                            {
                                href = interactive.GetAttribute("href");
                            }
                            else if (interactive.Tag == "button")
                            {
                                tagName = "button";
                                element = interactive;
                            }
                        }

                        bool isClickable = !string.IsNullOrEmpty(href) || tagName == "button" || tagName == "input";
                        bool isFocusable = isClickable || tagName == "input" || tagName == "textarea" || tagName == "select";
                        bool isEditable = tagName == "input" || tagName == "textarea";

                        result = new HitTestResult(
                            TagName: tagName?.ToLowerInvariant() ?? "",
                            Href: href,
                            Cursor: !string.IsNullOrEmpty(href) ? CursorType.Pointer : (isEditable ? CursorType.Text : CursorType.Default),
                            IsClickable: isClickable,
                            IsFocusable: isFocusable,
                            IsEditable: isEditable,
                            ElementId: elementId,
                            NativeElement: element,
                            BoundingBox: ptNode.Bounds
                        );
                        return true;
                    }
                }
            }

            return false;
        }

        private void CollectAllNodes(IReadOnlyList<PaintNodeBase> nodes, List<PaintNodeBase> result)
        {
            if (nodes == null) return;
            foreach (var node in nodes)
            {
                result.Add(node);
                CollectAllNodes(node.Children, result);
            }
        }
        
        private Element FindInteractiveAncestor(Element element)
        {
            var current = element;
            while (current != null)
            {
                if (string.Equals(current.Tag, "a", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(current.Tag, "button", StringComparison.OrdinalIgnoreCase))
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
