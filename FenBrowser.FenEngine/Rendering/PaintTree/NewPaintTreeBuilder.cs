// SpecRef: CSS2.1 Appendix E (stacking contexts and painting order)
// CapabilityId: PAINT-STACKING-ORDER-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout;
using FenBrowser.Core.Logging;
using SkiaSharp;
using FenBrowser.FenEngine.Typography;
using System.Text.RegularExpressions;
using FenBrowser.FenEngine.Rendering.Css;

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
        private int _normalizedBoxRectCount;
        private int _normalizedClipRectCount;
        
        // CSS Counters state - tracks counter values during tree traversal
        private readonly Dictionary<string, int> _counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        
        private readonly Interaction.ScrollManager _scrollManager;

        private static bool IsOverflowClipMode(string overflow)
        {
            return string.Equals(overflow, "hidden", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(overflow, "clip", StringComparison.OrdinalIgnoreCase);
        }
        
        private NewPaintTreeBuilder(
            IReadOnlyDictionary<Node, Layout.BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            float viewportWidth,
            float viewportHeight,
            Interaction.ScrollManager scrollManager,
            string baseUri)
        {
            _boxes = boxes ?? throw new ArgumentNullException(nameof(boxes));
            _styles = styles ?? new Dictionary<Node, CssComputed>();
            _viewportWidth = viewportWidth;
            _viewportHeight = viewportHeight;
            _scrollManager = scrollManager;
            _baseUri = baseUri;
        }
        
        private readonly HashSet<Element> _topLayerElements = new HashSet<Element>();
        private bool _renderingTopLayer = false;
        
        /// <summary>
        /// Builds an immutable paint tree from layout and style data.
        /// </summary>
        public static ImmutablePaintTree Build(
            Node root,
            IReadOnlyDictionary<Node, Layout.BoxModel> boxes,
            IReadOnlyDictionary<Node, CssComputed> styles,
            float viewportWidth,
            float viewportHeight,
            Interaction.ScrollManager scrollManager,
            string baseUri = null,
            int frameId = 0)
        {
            DiagnosticPaths.AppendRootText("debug_paint_start.txt", $"Build Start: Root={root?.GetType().Name} BoxCount={boxes?.Count}\n");
            
            FenBrowser.Core.EngineLogCompat.Debug($"[PAINT-TREE] Build called. Root={(root != null ? root.GetType().Name : "NULL")} Boxes={boxes?.Count} Styles={styles?.Count}");
            if (root == null || boxes == null || boxes.Count == 0)
            {
                return ImmutablePaintTree.Empty;
            }
            
            var builder = new NewPaintTreeBuilder(boxes, styles, viewportWidth, viewportHeight, scrollManager, baseUri);
            builder._frameId = frameId;
            
            // Populate Top Layer set
            Document doc = root as Document;
            if (doc != null)
            {
                foreach (var el in doc.TopLayer)
                    builder._topLayerElements.Add(el);
            }
            
            // Build the root stacking context
            var rootContext = new BuilderStackingContext(root);
            builder.BuildRecursive(root, rootContext, 0, null, false);
            
            // Flatten stacking contexts into paint order
            var rootNodes = rootContext.Flatten();
            
            // Render Top Layer
            if (builder._topLayerElements.Count > 0 && doc != null)
            {
                builder._renderingTopLayer = true;
                foreach (var modal in doc.TopLayer)
                {
                    // Verify if it has a box (rendered)
                    if (boxes.ContainsKey(modal))
                    {
                        // Add Backdrop
                        // Grey semi-transparent overlay
                        rootNodes.Add(new CustomPaintNode
                        {
                            Bounds = new SKRect(0, 0, viewportWidth, viewportHeight),
                            PaintAction = (canvas, bounds) =>
                            {
                                using var p = new SKPaint { Color = new SKColor(0, 0, 0, 30), IsAntialias = false }; // ~12% opacity
                                canvas.DrawRect(bounds, p);
                            }
                        });
                        
                        // Render Modal
                        // We use a fresh stacking context for the modal tree
                        var modalContext = new BuilderStackingContext(modal);
                        builder.BuildRecursive(modal, modalContext, 0, null, false);
                        var modalNodes = modalContext.Flatten();
                        rootNodes.AddRange(modalNodes);
                    }
                }
                builder._renderingTopLayer = false;
            }

            EngineLog.Write(
                LogSubsystem.Paint,
                LogSeverity.Info,
                "Paint tree build completed",
                LogMarker.None,
                default,
                new Dictionary<string, object?>
                {
                    ["frameId"] = frameId,
                    ["rootCount"] = rootNodes?.Count ?? 0,
                    ["normalizedBoxRects"] = builder._normalizedBoxRectCount,
                    ["normalizedClipRects"] = builder._normalizedClipRectCount
                });

            return new ImmutablePaintTree(rootNodes, frameId);
        }
        
        /// <summary>
        /// Recursively builds paint nodes for an element and its children.
        /// </summary>
        private void BuildRecursive(Node node, BuilderStackingContext currentContext, int depth, BuilderStackingContext escapeContext = null, bool ancestorVisibilityHidden = false)
        {
            if (depth > 128) return; 
            try { System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); }
            catch (InsufficientExecutionStackException) { return; }
            
            // Skip Top Layer elements during normal pass
            if (!_renderingTopLayer && node is Element el && _topLayerElements.Contains(el))
                return;

            if (node == null) return;

            if (node is Text traceTextNode && ShouldTraceWhatIsMyBrowserText(traceTextNode.Data))
            {
                global::FenBrowser.Core.EngineLogCompat.Info(
                    $"[WIMB-TEXT-VISIT] Text='{traceTextNode.Data}' Parent=<{traceTextNode.ParentElement?.TagName ?? "null"}>",
                    LogCategory.Rendering);
            }
            
            // Process children for Document/Fragment even if they don't have boxes themselves
            if (node is Document || node is DocumentFragment)
            {
                ProcessChildren(node, currentContext, depth + 1, escapeContext, ancestorVisibilityHidden);
                return;
            }
            
            // Only process Elements and Text nodes for actual painting
            if (!(node is Element) && !(node is Text)) return;
            
            // Get computed style
            // If node is Text, use parent's style
            CssComputed style = null;
            if (node.NodeType == NodeType.Text && node.ParentNode != null)
            {
                _styles.TryGetValue(node.ParentNode, out style);
            }
            else
            {
                _styles.TryGetValue(node, out style);
            }

            // Get box model. Some inline SVG elements fail to receive a direct layout box even though
            // the parent wrapper is sized; synthesize a paint box from parent geometry in that case.
            if (!TryResolvePaintBox(node, style, out var box) || box == null)
            {
                if (node is Text missingTextNode && ShouldTraceWhatIsMyBrowserText(missingTextNode.Data))
                {
                    global::FenBrowser.Core.EngineLogCompat.Info(
                        $"[WIMB-TEXT-MISS] Text='{missingTextNode.Data}' Parent=<{missingTextNode.ParentElement?.TagName ?? "null"}> HasStyle={(style != null)}",
                        LogCategory.Rendering);
                }

                // If the element itself doesn't have a paint box, still allow its children to render.
                // This prevents small inline wrappers (e.g., SPAN around SVG icons) from swallowing content.
                if (node is Element && !ShouldHide(node, style))
                {
                    ProcessChildren(node, currentContext, depth + 1, escapeContext, ancestorVisibilityHidden);
                }
                return;
            }

            SanitizeBoxForPaint(box);

            // ABSOLUTE POSITION ESCAPE LOGIC
            // If this node is absolute, and we have a valid escape context (from a static parent), use it.
            if (escapeContext != null && style != null && string.Equals(style.Position, "absolute", StringComparison.OrdinalIgnoreCase))
            {
                 currentContext = escapeContext;
                 escapeContext = null; 
            }
            
            // Skip hidden elements (display: none)
            if (ShouldHide(node, style)) {
                 if (FenBrowser.Core.Logging.DebugConfig.LogPaintCommands && depth < 20)
                     global::FenBrowser.Core.EngineLogCompat.Log($"[PAINT-SKIP] {(node as Element)?.TagName} Reason=ShouldHide", FenBrowser.Core.Logging.LogCategory.Paint);
                 return;
            }
            
            // Process CSS counters (counter-reset and counter-increment)
            ProcessCounters(style);
            
            // Determine if this creates a new stacking context
            bool createsStackingContext = DetermineCreatesStackingContext(style);
            int zIndex = style?.ZIndex ?? 0;

            /*
            if (FenBrowser.Core.Logging.DebugConfig.LogPaintCommands && depth < 20)
            {
                 var logEl = node as Element;
                 string cls = logEl?.GetAttribute("class");
                 if (string.IsNullOrEmpty(cls) || FenBrowser.Core.Logging.DebugConfig.ShouldLog(cls))
                 {
                     string scInfo = createsStackingContext ? $" SC=True Z={zIndex}" : "";
                     string clipInfo = (style?.Overflow == "hidden" || style?.OverflowX == "hidden") ? " Clipped=True" : "";
                     string visInfo = (style?.Visibility == "hidden") ? " Vis=Hidden" : "";
                     global::FenBrowser.Core.EngineLogCompat.Log($"[PAINT-NODE] {new string(' ', depth)}{(node as Element)?.TagName} {scInfo}{clipInfo}{visInfo} Rect={box.BorderBox}", LogCategory.Paint);
                 }
            }
            */
            
            // Build paint nodes for this element
            // VISIBILITY CHECK: If visibility is hidden, we do NOT generate visual paint nodes for this element
            // However, we MUST still traverse children (as they might be visible) and handle stacking contexts/opacity/overflow.
            string visibility = style?.Visibility?.Trim().ToLowerInvariant();
            bool isExplicitlyVisible = string.Equals(visibility, "visible", StringComparison.OrdinalIgnoreCase);
            bool isExplicitlyHidden = string.Equals(visibility, "hidden", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(visibility, "collapse", StringComparison.OrdinalIgnoreCase);
            bool nodeVisibilityHidden = isExplicitlyHidden || (ancestorVisibilityHidden && !isExplicitlyVisible);

            List<PaintNodeBase> paintNodes = null;
            if (!nodeVisibilityHidden)
            {
                paintNodes = BuildPaintNodesForElement(node, box, style);
            }
            else
            {
                paintNodes = new List<PaintNodeBase>(); // Empty list for invisible element
            }
            
            // Handle Sticky Positioning
            if (style?.Position?.ToLowerInvariant() == "sticky")
            {
                // Find Scroll Container
                var resolvedScrollContainer = FindNearestScrollContainer(node as Element);
                float scrollX = 0;
                float scrollY = 0;
                SKRect containerRect = new SKRect(0, 0, _viewportWidth, _viewportHeight); // Default to viewport
                
                if (resolvedScrollContainer != null)
                {
                    // Container is an element
                    if (_boxes.TryGetValue(resolvedScrollContainer, out var cBox))
                    {
                        containerRect = cBox.PaddingBox;
                    }
                    var state = _scrollManager?.GetScrollState(resolvedScrollContainer);
                    if (state != null)
                    {
                        scrollX = state.ScrollX;
                        scrollY = state.ScrollY;
                    }
                }
                else
                {
                    // Container is Viewport (Root)
                    // If root document is scrollable?
                    // Usually window scroll. We assume viewport acts as window scroll if no element container?
                    // Currently we only support element scrolling via ScrollManager?
                    // If ScrollManager tracks root scroll, use keys like 'null' or Document?
                    // For now, assume element scrolling or nothing.
                    var rootState = _scrollManager?.GetScrollState(null); // Try get global scroll
                     if (rootState != null)
                    {
                        scrollX = rootState.ScrollX;
                        scrollY = rootState.ScrollY;
                    }
                }

                // Calculate Sticky Offset
                // 1. Calculate the 'natural' position relative to the scroll container's content box
                //    Layout coordinates are already absolute document coordinates.
                //    So naturalY = box.MarginBox.Top.
                
                float translationX = 0;
                float translationY = 0;
                
                // Top Constraint
                // stickyY = max(naturalY, scrollY + top)
                if (style.Top.HasValue) // Assume pixels for now
                {
                    float topConstraint = (float)style.Top.Value;
                    float naturalY = box.MarginBox.Top;
                    
                    // The element effectively shifts down to stay visible
                    // TargetY (absolute) = max(naturalY, scrollY + containerRect.Top + topConstraint)
                    // Note: ScrollY is positive. ContainerRect.Top is its document position.
                    
                    // If container is viewport(0,0), then TargetY = max(naturalY, scrollY + top).
                    // If container is an element at 100, and we scrolled 50px inside it. 
                    // Visual Top of container content starts at 100 - 50 = 50.
                    // We want element to be at 100 (container visual top) + topConstraint.
                    // Equivalent Abs Y = ContainerRect.Top + ScrollY + topConstraint ??
                    // No. 
                    // Let's think in "Flow Relative" coords?
                    // VisualY = AbsY - ScrollY.
                    // We want VisualY >= ContainerRect.Top (visually) + TopConstraint?
                    // Actually, for a nested scroll container, VisualY relative to screen.
                    
                    // Simple logic:
                    // Sticky element sticks to the "scroll port".
                    // Scroll Port Top (in document coords) = ContainerRect.Top + ScrollY.
                    
                    float scrollPortTop = (resolvedScrollContainer == null ? 0 : containerRect.Top) + scrollY;
                    float targetY = Math.Max(naturalY, scrollPortTop + topConstraint);
                    
                    // Limit by Containing Block (Parent)
                    // The sticky element cannot escape its parent.
                    if (node.ParentElement is Element parentEl && _boxes.TryGetValue(parentEl, out var parentBox))
                    {
                        float limitY = parentBox.ContentBox.Bottom - box.MarginBox.Height - (float)style.Margin.Bottom;
                        targetY = Math.Min(targetY, limitY);
                    }
                    
                    translationY = targetY - naturalY;
                }
                
                // Apply wrapper if translation needed
                if (Math.Abs(translationX) > 0.1f || Math.Abs(translationY) > 0.1f)
                {
                     paintNodes = new List<PaintNodeBase> 
                     { 
                         new StickyPaintNode 
                         { 
                             StickyOffset = new SKPoint(translationX, translationY),
                             Children = paintNodes,
                             Bounds = box.BorderBox // Approx
                         } 
                     };
                }
            }

            if (createsStackingContext)
            {
                // Create a new stacking context
                var childContext = new BuilderStackingContext(node)
                {
                    ZIndex = zIndex,
                    PaintNodes = paintNodes,
                    MaskImage = style?.MaskImage,
                    // Use BorderBox for masking area by default (standard box-mask)
                    MaskBounds = box.BorderBox,
                    Opacity = (float)(style.Opacity ?? 1.0),
                    Filter = style?.Filter,
                    BackdropFilter = style?.BackdropFilter
                };

                // Parse CSS transform and set on stacking context
                if (!string.IsNullOrEmpty(style?.Transform) && style.Transform != "none")
                {
                    var cssTransform = CssTransform3D.Parse(style.Transform);
                    if (cssTransform.HasTransform)
                    {
                        var matrix = cssTransform.ToSKMatrix(box.BorderBox);
                        if (matrix != SKMatrix.Identity)
                        {
                            // Determine transform-origin (default is center of border box per CSS spec)
                            float ox = box.BorderBox.MidX;
                            float oy = box.BorderBox.MidY;

                            if (!string.IsNullOrEmpty(style.TransformOrigin))
                            {
                                ParseTransformOrigin(style.TransformOrigin, box.BorderBox, out ox, out oy);
                            }

                            // Build full matrix with transform-origin:
                            // Effect: translate(ox,oy) * transform * translate(-ox,-oy) * point
                            // PreConcat is right-multiply, so we build: origin * matrix * inverseOrigin
                            var origin = SKMatrix.CreateTranslation(ox, oy);
                            var inverseOrigin = SKMatrix.CreateTranslation(-ox, -oy);
                            var full = SKMatrix.CreateIdentity();
                            full = full.PreConcat(origin);
                            full = full.PreConcat(matrix);
                            full = full.PreConcat(inverseOrigin);

                            childContext.TransformMatrix = full;
                        }
                    }
                }

                // Check for overflow/scroll
                bool isScrollable = (style?.OverflowX == "scroll" || style?.OverflowX == "auto" || 
                                     style?.OverflowY == "scroll" || style?.OverflowY == "auto");
                bool isClipped = IsOverflowClipMode(style?.Overflow) ||
                                 IsOverflowClipMode(style?.OverflowX) ||
                                 IsOverflowClipMode(style?.OverflowY);

                if (isScrollable || isClipped)
                {
                    // Scrollable containers clip purely to padding box (usually) or content box?
                    // Spec: overflow applies to padding box.
                    childContext.ClipBounds = box.PaddingBox;
                }

                if (isScrollable && _scrollManager != null)
                {
                    var scrollElement = node as Element;
                    if (scrollElement != null)
                    {
                        var viewportW = Math.Max(0f, box.PaddingBox.Width);
                        var viewportH = Math.Max(0f, box.PaddingBox.Height);
                        var (contentW, contentH) = EstimateScrollableContentSize(scrollElement, box.PaddingBox);
                        _scrollManager.SetScrollBounds(scrollElement, contentW, contentH, viewportW, viewportH);

                        // Execute scroll snap from live paint/layout geometry when there is recent user input.
                        if (!string.IsNullOrWhiteSpace(style?.ScrollSnapType) &&
                            !style.ScrollSnapType.Equals("none", StringComparison.OrdinalIgnoreCase))
                        {
                            var state = _scrollManager.GetScrollState(scrollElement);
                            if (HasRecentScrollInputHint(state))
                            {
                                _scrollManager.PerformSnap(scrollElement, style, ResolveElementRectForSnap, ResolveElementStyleForSnap);
                            }
                        }

                        var scrollState = _scrollManager.GetScrollState(scrollElement);
                        if (scrollState != null)
                        {
                            childContext.ScrollOffset = new SKPoint(scrollState.ScrollX, scrollState.ScrollY);
                        }
                    }
                }
                
                // Add to parent context based on z-index
                currentContext.AddChildContext(childContext);
                
                // Process children in the new context
                ProcessChildren(node, childContext, depth + 1, null, nodeVisibilityHidden);
            }
            else
            {
                // Check if positioned (affects paint order)
                string pos = style?.Position?.ToLowerInvariant();
                bool isPositioned = pos == "absolute" || pos == "fixed" || pos == "sticky";
                bool isFloat = !string.IsNullOrEmpty(style?.Float) && (style.Float == "left" || style.Float == "right");
                
                string display = style?.Display?.ToLowerInvariant() ?? "inline";
                bool isInlineLevel = display == "inline" || display == "inline-block" || display == "inline-flex" || display == "inline-grid" || display == "inline-table";
                if (node is Text) isInlineLevel = true;

                // 1. Add element's background/border nodes (UNCLIPPED by self)
                //    (paintNodes contains the background, border, etc.)
                if (isPositioned)
                {
                    currentContext.AddPositionedNodes(paintNodes, zIndex);
                }
                else if (isFloat)
                {
                    currentContext.AddFloatNodes(paintNodes);
                }
                else if (isInlineLevel)
                {
                    currentContext.AddInlineNodes(paintNodes);
                }
                else
                {
                    currentContext.AddBlockNodes(paintNodes);
                }

                // 2. Process Children (Clipped or Normal)
                bool isClipped = (IsOverflowClipMode(style?.Overflow) || IsOverflowClipMode(style?.OverflowX) || IsOverflowClipMode(style?.OverflowY) ||
                                  style?.Overflow == "scroll" || style?.OverflowX == "scroll" || style?.OverflowY == "scroll");

                if (isClipped)
                {
                    // Create Clip Node for children
                    var paddingBox = NormalizeRectForPaint(box.PaddingBox, box.BorderBox, clampToContainer: true);
                    if (!RectsEqual(box.PaddingBox, paddingBox))
                    {
                        _normalizedClipRectCount++;
                    }

                    var radius = ExtractBorderRadius(box, style);
                    SKPath clipPath = null;
                    SKRect clipRect = paddingBox;

                    // Calculate rounded clip if needed
                    if (radius != null && (radius[0].X > 0 || radius[0].Y > 0 || radius[1].X > 0 || radius[1].Y > 0 || 
                                           radius[2].X > 0 || radius[2].Y > 0 || radius[3].X > 0 || radius[3].Y > 0))
                    {
                         float topW = (float)style.BorderThickness.Top;
                         float rightW = (float)style.BorderThickness.Right;
                         float botW = (float)style.BorderThickness.Bottom;
                         float leftW = (float)style.BorderThickness.Left;
                         
                         var radii = new SKPoint[4];
                         // Inner radius = Outer radius - Border thickness (clamped to 0)
                         radii[0] = new SKPoint(Math.Max(0, radius[0].X - leftW), Math.Max(0, radius[0].Y - topW));
                         radii[1] = new SKPoint(Math.Max(0, radius[1].X - rightW), Math.Max(0, radius[1].Y - topW));
                         radii[2] = new SKPoint(Math.Max(0, radius[2].X - rightW), Math.Max(0, radius[2].Y - botW));
                         radii[3] = new SKPoint(Math.Max(0, radius[3].X - leftW), Math.Max(0, radius[3].Y - botW));
                         
                         var rrect = new SKRoundRect();
                         rrect.SetRectRadii(paddingBox, radii);
                         clipPath = new SKPath();
                         clipPath.AddRoundRect(rrect);
                    }

                    // Collect children into a temporary context and flatten them
                    var tempCtx = new BuilderStackingContext(node);
                    
                    // Keep descendants inside this overflow clip context.
                    // Escaping absolute descendants from static ancestors produces visible leaks
                    // (Acid2 lower-face strip) and diverges from expected clipping behavior.
                    BuilderStackingContext nextEscapeContext = null;

                    // Inherit scroll offset logic if strictly needed, but for visual clipping Flatten() handles list construction.
                    // Important: Recursion here puts children into tempCtx.
                    ProcessChildren(node, tempCtx, depth + 1, nextEscapeContext, nodeVisibilityHidden);
                    
                    var clippedChildren = tempCtx.Flatten();
                    
                    if (clippedChildren.Count > 0)
                    {
                        clipRect = NormalizeRectForPaint(clipRect, paddingBox, clampToContainer: true);
                        var clipNode = new ClipPaintNode
                        {
                            Bounds = paddingBox,
                            ClipRect = clipRect,
                            ClipPath = clipPath,
                            Children = clippedChildren,
                            SourceNode = node
                        };
                        
                        // Add ClipNode to context (same bucket as element usually, or Block)
                        var clipList = new List<PaintNodeBase> { clipNode };
                        
                        if (isPositioned) currentContext.AddPositionedNodes(clipList, zIndex);
                        else if (isFloat) currentContext.AddFloatNodes(clipList);
                        else if (isInlineLevel) currentContext.AddInlineNodes(clipList);
                        else currentContext.AddBlockNodes(clipList);
                    }
                }
                else
                {
                    // No clipping - process children directly into current context
                    ProcessChildren(node, currentContext, depth + 1, escapeContext, nodeVisibilityHidden);
                }
            }
        }

        private bool TryResolvePaintBox(Node node, CssComputed style, out Layout.BoxModel box)
        {
            box = null;
            if (node is not Element element)
            {
                return _boxes.TryGetValue(node, out box) && box != null;
            }

            string tag = element.TagName?.ToUpperInvariant() ?? string.Empty;
            bool isSvg = tag == "SVG" || tag.EndsWith(":SVG", StringComparison.Ordinal);

            bool hasExisting = _boxes.TryGetValue(node, out var existingBox) && existingBox != null;
            if (!isSvg)
            {
                box = existingBox;
                return hasExisting;
            }

            bool hasExplicitWidth = style?.Width.HasValue == true && style.Width.Value > 0;
            bool hasExplicitHeight = style?.Height.HasValue == true && style.Height.Value > 0;
            bool hasExplicitAttrWidth = TryParseSvgLengthAttribute(element, "width", out var attrW);
            bool hasExplicitAttrHeight = TryParseSvgLengthAttribute(element, "height", out var attrH);
            bool hasExplicitSize = hasExplicitWidth || hasExplicitHeight || hasExplicitAttrWidth || hasExplicitAttrHeight;

            // Keep normal layout boxes when they look sane.
            if (hasExisting)
            {
                float bw = existingBox.ContentBox.Width;
                float bh = existingBox.ContentBox.Height;
                bool hasUsableSize = bw > 2f && bh > 2f;
                bool explicitNonZero = hasExplicitSize && bw > 0f && bh > 0f;
                if (hasUsableSize || explicitNonZero)
                {
                    box = existingBox;
                    return true;
                }
            }

            // Some inline SVG icons (Google material icons, search controls) miss direct layout boxes.
            // Walk up to the nearest ancestor with geometry and synthesize a paint box.
            Layout.BoxModel parentBox = null;
            Node anchor = element.ParentNode;
            while (anchor != null)
            {
                // If an ancestor is hidden (HTML hidden attribute or display:none), the SVG
                // should not be rendered — bail out instead of synthesizing a box.
                if (anchor is Element anchorElem)
                {
                    if (anchorElem.HasAttribute("hidden"))
                    {
                        box = null;
                        return false;
                    }
                    if (_styles.TryGetValue(anchor, out var ancStyle) &&
                        string.Equals(ancStyle?.Display, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        box = null;
                        return false;
                    }
                }

                if (_boxes.TryGetValue(anchor, out parentBox) &&
                    parentBox != null &&
                    parentBox.ContentBox.Width > 0f &&
                    parentBox.ContentBox.Height > 0f)
                {
                    break;
                }
                anchor = anchor.ParentNode;
            }

            if (parentBox == null)
            {
                box = existingBox;
                return box != null;
            }

            float width = 0f;
            float height = 0f;

            if (hasExplicitWidth)
            {
                width = (float)style.Width.Value;
            }
            if (hasExplicitHeight)
            {
                height = (float)style.Height.Value;
            }

            if (width <= 0f && hasExplicitAttrWidth)
            {
                width = attrW;
            }
            if (height <= 0f && hasExplicitAttrHeight)
            {
                height = attrH;
            }

            float parentW = parentBox.ContentBox.Width;
            float parentH = parentBox.ContentBox.Height;

            // Use viewBox ratio when available.
            if ((width <= 0f || height <= 0f) && TryParseSvgViewBoxSize(element, out var vbW, out var vbH))
            {
                if (parentW > 0f && parentH > 0f)
                {
                    float scale = Math.Min(parentW / vbW, parentH / vbH);
                    if (!float.IsNaN(scale) && !float.IsInfinity(scale) && scale > 0f)
                    {
                        if (width <= 0f) width = vbW * scale;
                        if (height <= 0f) height = vbH * scale;
                    }
                }
                else
                {
                    if (width <= 0f) width = vbW;
                    if (height <= 0f) height = vbH;
                }
            }

            if (width <= 0f)
            {
                // Icon wrappers are generally <= 64px and should default to 24px glyphs.
                if (parentW > 0f && parentW <= 64f)
                {
                    width = Math.Min(parentW, 24f);
                }
                else
                {
                    width = parentW > 0f ? parentW : 24f;
                }
            }
            if (height <= 0f)
            {
                if (style?.LineHeight.HasValue == true && style.LineHeight.Value > 0)
                {
                    height = (float)style.LineHeight.Value;
                }
                else
                {
                    float parentHeight = parentH > 0 ? parentH : 24f;
                    height = Math.Min(parentHeight, width);
                }
            }

            parentW = parentW > 0 ? parentW : width;
            parentH = parentH > 0 ? parentH : height;

            width = Math.Max(1f, Math.Min(width, parentW));
            height = Math.Max(1f, Math.Min(height, parentH));

            float left = parentBox.ContentBox.Left;
            float top = parentBox.ContentBox.Top;

            if (parentW > width)
            {
                left += (parentW - width) * 0.5f;
            }
            if (parentH > height)
            {
                top += (parentH - height) * 0.5f;
            }

            box = Layout.BoxModel.FromContentBox(left, top, width, height);
            return true;
        }

        private static SKRect NormalizeRectForPaint(SKRect rect, SKRect? container = null, bool clampToContainer = false)
        {
            return LayoutHelper.NormalizeRect(rect, container, clampToContainer);
        }

        private void SanitizeBoxForPaint(Layout.BoxModel box)
        {
            if (box == null) return;

            var originalMargin = box.MarginBox;
            var originalBorder = box.BorderBox;
            var originalPadding = box.PaddingBox;
            var originalContent = box.ContentBox;

            var margin = NormalizeRectForPaint(originalMargin);
            var border = NormalizeRectForPaint(originalBorder);
            var padding = NormalizeRectForPaint(originalPadding, border, clampToContainer: true);
            var content = NormalizeRectForPaint(originalContent, padding, clampToContainer: true);

            box.MarginBox = margin;
            box.BorderBox = border;
            box.PaddingBox = padding;
            box.ContentBox = content;

            if (!RectsEqual(originalMargin, margin)) _normalizedBoxRectCount++;
            if (!RectsEqual(originalBorder, border)) _normalizedBoxRectCount++;
            if (!RectsEqual(originalPadding, padding)) _normalizedBoxRectCount++;
            if (!RectsEqual(originalContent, content)) _normalizedBoxRectCount++;
        }

        private static bool RectsEqual(SKRect left, SKRect right, float epsilon = 0.01f)
        {
            return Math.Abs(left.Left - right.Left) <= epsilon &&
                   Math.Abs(left.Top - right.Top) <= epsilon &&
                   Math.Abs(left.Right - right.Right) <= epsilon &&
                   Math.Abs(left.Bottom - right.Bottom) <= epsilon;
        }

        private static bool ShouldTraceWhatIsMyBrowserText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.Contains("My browser", StringComparison.Ordinal) ||
                   text.Contains("Guides", StringComparison.Ordinal) ||
                   text.Contains("Detect my settings", StringComparison.Ordinal) ||
                   text.Contains("Tools", StringComparison.Ordinal) ||
                   text.Contains("Chrome 146 on Windows 10", StringComparison.Ordinal) ||
                   text.Contains("Your web browser is up to date", StringComparison.Ordinal) ||
                   text.Contains("Your Web Browser's Settings", StringComparison.Ordinal) ||
                   text.Contains("Now that you know what browser you're using", StringComparison.Ordinal) ||
                   text.Contains("How to enable JavaScript", StringComparison.Ordinal) ||
                   text.Contains("No - JavaScript is not enabled", StringComparison.Ordinal) ||
                   text.Contains("Could not be detected because Javascript is disabled", StringComparison.Ordinal) ||
                   text.Contains("Yes - JavaScript is enabled", StringComparison.Ordinal) ||
                   text.Contains("Yes - Cookies are enabled", StringComparison.Ordinal) ||
                   text.Contains("Please wait...", StringComparison.Ordinal);
        }

        private static string NormalizeRenderableText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text
                .Replace("âœ“", "✓", StringComparison.Ordinal)
                .Replace("âœ”", "✔", StringComparison.Ordinal)
                .Replace("âœ˜", "✘", StringComparison.Ordinal);
        }

        private static IReadOnlyList<PositionedGlyph> BuildPaintGlyphs(
            string text,
            string fontFamily,
            float fontSize,
            int fontWeight,
            SKPoint origin)
        {
            if (string.IsNullOrWhiteSpace(text) || fontSize <= 0)
            {
                return null;
            }

            var glyphRun = _fontService.ShapeText(text, fontFamily ?? "Segoe UI", fontSize, fontWeight);
            if (glyphRun.Glyphs == null || glyphRun.Glyphs.Length == 0)
            {
                return null;
            }

            var glyphs = new List<PositionedGlyph>(glyphRun.Glyphs.Length);
            int renderableGlyphCount = 0;
            foreach (var glyph in glyphRun.Glyphs)
            {
                var positioned = new PositionedGlyph(
                    glyph.GlyphId,
                    origin.X + glyph.X,
                    origin.Y + glyph.Y);
                if (positioned.IsRenderable)
                {
                    renderableGlyphCount++;
                }

                glyphs.Add(positioned);
            }

            return renderableGlyphCount > 0 ? glyphs : null;
        }

        private static bool TryParseSvgLengthAttribute(Element element, string attributeName, out float value)
        {
            value = 0f;
            string raw = element.GetAttribute(attributeName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            raw = raw.Trim();
            int count = 0;
            while (count < raw.Length)
            {
                char ch = raw[count];
                if ((ch >= '0' && ch <= '9') || ch == '.' || ch == '-')
                {
                    count++;
                    continue;
                }

                break;
            }

            if (count == 0)
            {
                return false;
            }

            string numeric = raw.Substring(0, count);
            if (!float.TryParse(numeric, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                value = 0f;
                return false;
            }

            value = Math.Max(0f, value);
            return true;
        }

        private static bool TryParseSvgViewBoxSize(Element element, out float width, out float height)
        {
            width = 0f;
            height = 0f;

            string raw = element.GetAttribute("viewBox") ?? element.GetAttribute("viewbox");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var parts = raw.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                return false;
            }

            if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) ||
                !float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h))
            {
                return false;
            }

            width = Math.Abs(w);
            height = Math.Abs(h);
            return width > 0f && height > 0f;
        }

        private Element FindNearestScrollContainer(Element startNode)
        {
            var curr = startNode?.ParentElement;
            while (curr != null)
            {
                if (_styles.TryGetValue(curr, out var s))
                {
                    bool scroll = (s.OverflowX == "scroll" || s.OverflowX == "auto" || 
                                   s.OverflowY == "scroll" || s.OverflowY == "auto");
                    if (scroll) return curr;
                }
                curr = curr.ParentElement;
            }
            return null; // Viewport
        }

        private SKRect ResolveElementRectForSnap(Element element)
        {
            if (element == null) return SKRect.Empty;
            if (_boxes.TryGetValue(element, out var box) && box != null)
            {
                return box.BorderBox;
            }
            return SKRect.Empty;
        }

        private CssComputed ResolveElementStyleForSnap(Element element)
        {
            if (element == null) return null;
            if (_styles.TryGetValue(element, out var style))
            {
                return style;
            }
            return null;
        }

        private (float ContentWidth, float ContentHeight) EstimateScrollableContentSize(Element container, SKRect paddingBox)
        {
            float maxRight = Math.Max(0f, paddingBox.Width);
            float maxBottom = Math.Max(0f, paddingBox.Height);

            if (container?.ChildNodes == null || container.ChildNodes.Length == 0)
            {
                return (maxRight, maxBottom);
            }

            var stack = new Stack<Node>();
            for (int i = 0; i < container.ChildNodes.Length; i++)
            {
                stack.Push(container.ChildNodes[i]);
            }

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == null) continue;

                if (_boxes.TryGetValue(current, out var childBox) && childBox != null)
                {
                    float right = childBox.MarginBox.Right - paddingBox.Left;
                    float bottom = childBox.MarginBox.Bottom - paddingBox.Top;
                    maxRight = Math.Max(maxRight, right);
                    maxBottom = Math.Max(maxBottom, bottom);
                }

                if (current.ChildNodes != null)
                {
                    for (int i = 0; i < current.ChildNodes.Length; i++)
                    {
                        stack.Push(current.ChildNodes[i]);
                    }
                }
            }

            return (Math.Max(maxRight, paddingBox.Width), Math.Max(maxBottom, paddingBox.Height));
        }

        private static bool HasRecentScrollInputHint(Interaction.ScrollState state)
        {
            if (state == null) return false;
            bool hasMagnitude = Math.Abs(state.LastInputDeltaX) > 0.01f ||
                                Math.Abs(state.LastInputDeltaY) > 0.01f ||
                                Math.Abs(state.LastVelocityX) > 0.01f ||
                                Math.Abs(state.LastVelocityY) > 0.01f;
            if (!hasMagnitude) return false;

            var age = DateTime.UtcNow - state.LastScrollUpdateUtc;
            return age <= TimeSpan.FromMilliseconds(600);
        }
        
        private void ProcessChildren(Node node, BuilderStackingContext context, int depth, BuilderStackingContext escapeContext = null, bool ancestorVisibilityHidden = false)
        {
            // Form controls (INPUT, TEXTAREA) are replaced elements; we handle their content rendering explicitly.
            // Skipping children prevents double-rendering of text.
            if (node is Element e && (e.TagName?.ToUpperInvariant() == "TEXTAREA" || e.TagName?.ToUpperInvariant() == "INPUT"))
                return;

            CssComputed style = null;
            if (node is Element el) _styles.TryGetValue(el, out style);

            // 1. ::before
            if (style?.Before?.PseudoElementInstance != null)
            {
                BuildRecursive(style.Before.PseudoElementInstance, context, depth + 1, escapeContext, ancestorVisibilityHidden);
            }

            if (node != null && node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    BuildRecursive(child, context, depth + 1, escapeContext, ancestorVisibilityHidden);
                }
            }

            // 2. ::after
            if (style?.After?.PseudoElementInstance != null)
            {
                BuildRecursive(style.After.PseudoElementInstance, context, depth + 1, escapeContext, ancestorVisibilityHidden);
            }
        }
        
        /// <summary>
        /// Builds concrete paint nodes for an element.
        /// </summary>
        private List<PaintNodeBase> BuildPaintNodesForElement(Node node, Layout.BoxModel box, CssComputed style)
        {
            var nodes = new List<PaintNodeBase>();
            SKRect bounds = NormalizeRectForPaint(box.BorderBox);
            
            // Poll interactive state
            Element elemNode = node as Element;
            bool isFocused = elemNode != null && ElementStateManager.Instance.IsFocused(elemNode);
            // Paint-time hover chrome should be direct-target only; ancestor-chain hover remains for CSS matching.
            bool isHovered = elemNode != null && ReferenceEquals(ElementStateManager.Instance.HoveredElement, elemNode);
            
            // CRITICAL FIX: Disable focus ring for root elements (HTML/BODY) to prevent "Blue Edge" 
            if (elemNode != null)
            {
                string tag = elemNode.TagName?.ToUpperInvariant();
                // Prevent focus ring on root document/body which causes persistent blue borders
                if (tag == "HTML" || tag == "BODY")
                {
                    isFocused = false;
                }
            }
            
            // 0. Shadow (below background)
            var shadowNodes = BuildBoxShadowNodes(node, bounds, style, box);
            if (shadowNodes != null) nodes.AddRange(shadowNodes);

            // SPECIAL: Inline non-atomic elements (SPAN, A, etc.) need constructed backgrounds from children.
            // Replaced/atomic inline elements (IMG/OBJECT/INPUT/VIDEO/...) must keep box-scoped background-image
            // painting and should not route through the inline-fragment background builder.
            bool isAtomicInlineElement = elemNode != null && IsAtomicInlinePaintElement(elemNode);
            bool isInlineGroup = elemNode != null &&
                                 string.Equals(style?.Display, "inline", StringComparison.OrdinalIgnoreCase) &&
                                 !(node is Text) &&
                                 !isAtomicInlineElement;
            
            if (isInlineGroup)
            {
                 var inlineNodes = BuildInlineBackgroundAndBorder(node, style, isFocused, isHovered);
                 if (inlineNodes != null) nodes.AddRange(inlineNodes);
            }
            else
            {
                // Standard Block/Atomic/Replaced Behavior

            if (node is Element || node is PseudoElement)
            {
                // 1. Background
                var bgNode = BuildBackgroundNode(node, box, style, isFocused, isHovered);
                if (bgNode != null) nodes.Add(bgNode);

                var bgImgNode = BuildBackgroundImageNode(node, box, style);
                if (bgImgNode != null) nodes.Add(bgImgNode);
                
                // 2. Border
                var borderNode = BuildBorderNode(node, box, style, isFocused, isHovered);
                if (borderNode != null) nodes.Add(borderNode);
            }
            }
            
            // 3. Content (text or image)
            if (node is Text textNode)
            {
                var textNodes = BuildTextNode(textNode, box, style, isFocused, isHovered);
                if (textNodes != null) nodes.AddRange(textNodes);
            }
            else if (node is Element elem)
            {
                string tagUpper = elem.TagName?.ToUpperInvariant();
                if (tagUpper == "IMG")
                {
                    global::FenBrowser.Core.EngineLogCompat.Debug($"[PAINT-BUILD] Found IMG element id={elem.GetAttribute("id")} src={(elem.GetAttribute("src")?.Length > 60 ? elem.GetAttribute("src")?.Substring(0,60) + "..." : elem.GetAttribute("src"))}");
                }
                if (tagUpper.Contains("SVG"))
                {
                     global::FenBrowser.Core.EngineLogCompat.Debug($"[SVG-CANDIDATE] <{elem.TagName}> id={elem.GetAttribute("id")} class={elem.GetAttribute("class")} src={elem.GetAttribute("src")}", FenBrowser.Core.Logging.LogCategory.Rendering);
                }
                if (IsImageElement(elem) || tagUpper == "SVG" || tagUpper.EndsWith(":SVG")) // Handle namespace?
                {
                    var imageNode = BuildImageOrSvgNode(elem, box, style, isFocused, isHovered);
                    if (imageNode != null) nodes.Add(imageNode);
                }
                else if (elem.TagName?.ToUpperInvariant() == "INPUT")
                {
                    string inputType = elem.GetAttribute("type")?.ToLowerInvariant() ?? "text";
                    if (inputType == "checkbox" || inputType == "radio" || inputType == "color" || inputType == "range" ||
                        inputType == "date" || inputType == "time" || inputType == "datetime-local" || inputType == "number" || inputType == "file")
                    {
                        var specialNode = BuildSpecialInputNode(elem, box, style, isFocused, isHovered);
                        if (specialNode != null) nodes.Add(specialNode);
                    }
                    else
                    {
                        var inputNode = BuildInputTextNode(elem, box, style, isFocused, isHovered);
                        if (inputNode != null) nodes.Add(inputNode);
                    }
                }
                else if (elem.TagName?.ToUpperInvariant() == "TEXTAREA")
                {
                    var inputNode = BuildInputTextNode(elem, box, style, isFocused, isHovered);
                    if (inputNode != null) nodes.Add(inputNode);
                }
                else if (elem.TagName?.ToUpperInvariant() == "RUBY")
                {
                    // Ruby annotation - render RT above base text
                    var rubyNodes = BuildRubyTextNode(elem, box, style);
                    if (rubyNodes != null) nodes.AddRange(rubyNodes);
                }
                else if (elem.TagName?.ToUpperInvariant() == "VIDEO")
                {
                    var videoNode = BuildVideoPlaceholder(elem, box, style);
                    if (videoNode != null) nodes.Add(videoNode);
                }
                else if (elem.TagName?.ToUpperInvariant() == "AUDIO")
                {
                    var audioNode = BuildAudioPlaceholder(elem, box, style);
                    if (audioNode != null) nodes.Add(audioNode);
                }
                else if (elem.TagName?.ToUpperInvariant() == "IFRAME")
                {
                    var iframeNode = BuildIframePlaceholder(elem, box, style);
                    if (iframeNode != null) nodes.Add(iframeNode);
                }
                else if (tagUpper == "PROGRESS")
                {
                    var progressNode = BuildProgressBar(elem, box, style);
                    if (progressNode != null) nodes.Add(progressNode);
                }
                else if (tagUpper == "METER")
                {
                    var meterNode = BuildMeterBar(elem, box, style);
                    if (meterNode != null) nodes.Add(meterNode);
                }
            }
            // 4. List Marker (if display: list-item)
            if (string.Equals(style?.Display, "list-item", StringComparison.OrdinalIgnoreCase))
            {
                var markerNode = BuildListMarkerNode(node, box, style, isFocused, isHovered);
                if (markerNode != null) nodes.Add(markerNode);
            }
            
            // 5. Pseudo-elements (::before and ::after)
            if (node is Element pseudoParent && style != null)
            {
                // ::before pseudo-element
                if (style.Before != null && !string.IsNullOrEmpty(style.Before.Content))
                {
                    var beforeNodes = BuildPseudoElementNodes(pseudoParent, box, style.Before, "before");
                    if (beforeNodes != null && beforeNodes.Count > 0)
                    {
                        nodes.InsertRange(0, beforeNodes); // Insert at beginning
                    }
                }
                
                // ::after pseudo-element
                if (style.After != null && !string.IsNullOrEmpty(style.After.Content))
                {
                    var afterNodes = BuildPseudoElementNodes(pseudoParent, box, style.After, "after");
                    if (afterNodes != null && afterNodes.Count > 0)
                    {
                        nodes.AddRange(afterNodes); // Add at end
                    }
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
        
        /// <summary>
        /// Builds a paint node for ::before or ::after pseudo-element content.
        /// </summary>
        private List<PaintNodeBase> BuildPseudoElementNodes(Element parent, Layout.BoxModel parentBox, CssComputed pseudoStyle, string position)
        {
            if (parentBox == null || pseudoStyle == null)
            {
                return null;
            }

            string content = pseudoStyle.Content;
            if (string.IsNullOrEmpty(content) || content == "none" || content == "normal")
            {
                return null;
            }

            var pseudoNode = pseudoStyle.PseudoElementInstance;

            Layout.BoxModel pseudoBox = null;
            if (pseudoNode != null)
            {
                _boxes.TryGetValue(pseudoNode, out pseudoBox);
            }

            // Fallback when pseudo box is unavailable: keep pseudo paint anchored to parent content box.
            if (pseudoBox == null)
            {
                pseudoBox = new Layout.BoxModel
                {
                    ContentBox = parentBox.ContentBox,
                    PaddingBox = parentBox.ContentBox,
                    BorderBox = parentBox.ContentBox,
                    MarginBox = parentBox.ContentBox
                };
            }

            var sourceNode = (Node)pseudoNode ?? parent;
            var nodes = new List<PaintNodeBase>();

            // Pseudo elements can be purely geometric (e.g. Acid2 nose triangles with content: "").
            var bgNode = BuildBackgroundNode(sourceNode, pseudoBox, pseudoStyle, isFocused: false, isHovered: false);
            if (bgNode != null)
            {
                nodes.Add(bgNode);
            }

            var bgImgNode = BuildBackgroundImageNode(sourceNode, pseudoBox, pseudoStyle);
            if (bgImgNode != null)
            {
                nodes.Add(bgImgNode);
            }

            var borderNode = BuildBorderNode(sourceNode, pseudoBox, pseudoStyle, isFocused: false, isHovered: false);
            if (borderNode != null)
            {
                nodes.Add(borderNode);
            }

            // Text content remains supported when pseudo has authored string content.
            string textContent = ParseContentValue(content, parent);
            if (!string.IsNullOrEmpty(textContent))
            {
                float fontSize = (float)(pseudoStyle.FontSize ?? 16.0);
                CssComputed parentStyle = null;
                _styles?.TryGetValue(parent, out parentStyle);
                var color = pseudoStyle.ForegroundColor ?? parentStyle?.ForegroundColor ?? SKColors.Black;
                string fontFamily = pseudoStyle.FontFamilyName ?? "sans-serif";
                int fontWeight = pseudoStyle.FontWeight ?? 400;

                var contentBounds = pseudoBox.ContentBox;
                nodes.Add(new CustomPaintNode
                {
                    Bounds = contentBounds,
                    SourceNode = sourceNode,
                    PaintAction = (canvas, bounds) =>
                    {
                        using var paint = new SKPaint
                        {
                            Color = color,
                            TextSize = fontSize,
                            IsAntialias = true,
                            Typeface = SKTypeface.FromFamilyName(
                                fontFamily,
                                fontWeight >= 700 ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                                SKFontStyleWidth.Normal,
                                SKFontStyleSlant.Upright)
                        };

                        float textWidth = paint.MeasureText(textContent);
                        float drawX = position == "before" ? bounds.Left : bounds.Right - textWidth;
                        float drawY = bounds.Top + fontSize;
                        canvas.DrawText(textContent, drawX, drawY, paint);
                    }
                });
            }

            return nodes.Count > 0 ? nodes : null;
        }
        
        /// <summary>
        /// Parses CSS content property value into actual text.
        /// </summary>
        private string ParseContentValue(string content, Element parent)
        {
            if (string.IsNullOrEmpty(content)) return null;
            
            content = content.Trim();
            
            // Handle quoted strings: "text" or 'text'
            if ((content.StartsWith("\"") && content.EndsWith("\"")) ||
                (content.StartsWith("'") && content.EndsWith("'")))
            {
                return content.Substring(1, content.Length - 2);
            }
            
            // Handle attr() function
            if (content.StartsWith("attr(") && content.EndsWith(")"))
            {
                string attrName = content.Substring(5, content.Length - 6).Trim();
                return parent?.GetAttribute(attrName) ?? "";
            }
            
            // Handle counter() function
            if (content.StartsWith("counter(") && content.EndsWith(")"))
            {
                string args = content.Substring(8, content.Length - 9).Trim();
                string[] parts = args.Split(',');
                string counterName = parts[0].Trim();
                string listStyle = parts.Length > 1 ? parts[1].Trim() : "decimal";
                
                int value = _counters.ContainsKey(counterName) ? _counters[counterName] : 0;
                return FormatCounterValue(value, listStyle);
            }
            
            // Handle counters() function (for nested lists with separator)
            if (content.StartsWith("counters(") && content.EndsWith(")"))
            {
                string args = content.Substring(9, content.Length - 10).Trim();
                // counters(name, "separator", style)
                // For now, just return the current counter value
                string[] parts = args.Split(',');
                string counterName = parts[0].Trim().Trim('"', '\'');
                
                int value = _counters.ContainsKey(counterName) ? _counters[counterName] : 0;
                return value.ToString();
            }
            
            // Handle open-quote / close-quote
            if (content == "open-quote") return "\"";
            if (content == "close-quote") return "\"";
            
            // Return as-is for other values
            return content;
        }
        
        /// <summary>
        /// Processes counter-reset and counter-increment for the current element.
        /// </summary>
        private void ProcessCounters(CssComputed style)
        {
            if (style == null) return;
            
            // Process counter-reset
            if (!string.IsNullOrEmpty(style.CounterReset))
            {
                ParseCounterDirective(style.CounterReset, isReset: true);
            }
            
            // Process counter-increment
            if (!string.IsNullOrEmpty(style.CounterIncrement))
            {
                ParseCounterDirective(style.CounterIncrement, isReset: false);
            }
        }
        
        /// <summary>
        /// Parses counter-reset or counter-increment directive.
        /// Format: "name1 value1 name2 value2" or just "name1 name2"
        /// </summary>
        private void ParseCounterDirective(string directive, bool isReset)
        {
            if (string.IsNullOrWhiteSpace(directive) || directive == "none") return;
            
            string[] tokens = directive.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            
            for (int i = 0; i < tokens.Length; i++)
            {
                string name = tokens[i];
                int value = 0;
                
                // Check if next token is a number
                if (i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out int parsedValue))
                {
                    value = parsedValue;
                    i++; // Skip the number
                }
                
                if (isReset)
                {
                    _counters[name] = value;
                }
                else
                {
                    // Increment (default by 1 if no value specified)
                    if (value == 0) value = 1;
                    if (_counters.ContainsKey(name))
                        _counters[name] += value;
                    else
                        _counters[name] = value;
                }
            }
        }
        
        /// <summary>
        /// Formats a counter value according to list-style-type.
        /// </summary>
        private static string FormatCounterValue(int value, string listStyle)
        {
            listStyle = listStyle?.Trim().ToLowerInvariant() ?? "decimal";
            
            switch (listStyle)
            {
                case "decimal":
                    return value.ToString();
                case "decimal-leading-zero":
                    return value.ToString("D2");
                case "lower-roman":
                    return ToRoman(value).ToLowerInvariant();
                case "upper-roman":
                    return ToRoman(value);
                case "lower-alpha":
                case "lower-latin":
                    return value > 0 && value <= 26 ? ((char)('a' + value - 1)).ToString() : value.ToString();
                case "upper-alpha":
                case "upper-latin":
                    return value > 0 && value <= 26 ? ((char)('A' + value - 1)).ToString() : value.ToString();
                case "disc":
                    return "•";
                case "circle":
                    return "○";
                case "square":
                    return "■";
                case "lower-greek":
                    return value > 0 ? char.ConvertFromUtf32(0x03B1 + ((value - 1) % 24)) : value.ToString();
                case "armenian":
                    // Simplified Armenian numerals (1-38). Fallback to decimal beyond range.
                    string[] armenian = { "Ա", "Բ", "Գ", "Դ", "Ե", "Զ", "Է", "Ը", "Թ", "Ժ",
                                          "Ի", "Լ", "Խ", "Ծ", "Կ", "Հ", "Ձ", "Ղ", "Ճ", "Մ",
                                          "Յ", "Ն", "Շ", "Ո", "Չ", "Պ", "Ջ", "Ռ", "Ս", "Վ",
                                          "Տ", "Ր", "Ց", "Ու", "Փ", "Ք", "Օ", "Ֆ" };
                    if (value >= 1 && value <= armenian.Length) return armenian[value - 1];
                    return value.ToString();
                case "georgian":
                    // Georgian numeral (an) simplified 1-38
                    string[] georgian = { "ა", "ბ", "გ", "დ", "ე", "ვ", "ზ", "ჱ", "თ", "ი",
                                          "კ", "ლ", "მ", "ნ", "ჲ", "ო", "პ", "ჟ", "რ", "ს",
                                          "ტ", "უ", "ფ", "ქ", "ღ", "ყ", "შ", "ჩ", "ც", "ძ",
                                          "წ", "ჭ", "ხ", "ჴ", "ჯ", "ჰ", "ჵ", "ჶ" };
                    if (value >= 1 && value <= georgian.Length) return georgian[value - 1];
                    return value.ToString();
                default:
                    return value.ToString();
            }
        }
        
        /// <summary>
        /// Converts a number to Roman numerals.
        /// </summary>
        private static string ToRoman(int number)
        {
            if (number <= 0 || number > 3999) return number.ToString();
            
            var romanNumerals = new (int, string)[]
            {
                (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
                (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
                (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
            };
            
            var result = new System.Text.StringBuilder();
            foreach (var (val, numeral) in romanNumerals)
            {
                while (number >= val)
                {
                    result.Append(numeral);
                    number -= val;
                }
            }
            return result.ToString();
        }

        private List<PaintNodeBase> BuildInlineBackgroundAndBorder(Node node, CssComputed style, bool isFocused, bool isHovered)
        {
            if (style == null) return null;
            bool hasBg = style.BackgroundColor.HasValue && style.BackgroundColor.Value.Alpha > 0;
            var bt = style.BorderThickness;
            bool hasBorder = (bt.Left > 0 || bt.Right > 0 || bt.Top > 0 || bt.Bottom > 0);
            
            if (!hasBg && !hasBorder) return null;

            // 1. Collect all content bounds
            var rects = new List<SKRect>();
            CollectContentRects(node, rects);
            
            if (rects.Count == 0)
            {
                if (_boxes.TryGetValue(node, out var ownBox))
                {
                    rects.Add(ownBox.BorderBox);
                }
                else
                {
                    return null;
                }
            }
            
            // 2. Group by Line (Y) - approximate using helper
            var lines = GroupRectsByLine(rects);
            
            var resultNodes = new List<PaintNodeBase>();
            _boxes.TryGetValue(node, out var box); // Get box for radius resolution
            var fullRadius = ExtractBorderRadius(box, style); // [TL, TR, BR, BL]

            for (int i = 0; i < lines.Count; i++)
            {
                var lineGroup = lines[i];
                float minX = float.MaxValue;
                float maxX = float.MinValue;
                float minY = float.MaxValue;
                float maxY = float.MinValue;
                
                foreach (var r in lineGroup)
                {
                    if (r.Left < minX) minX = r.Left;
                    if (r.Right > maxX) maxX = r.Right;
                    if (r.Top < minY) minY = r.Top;
                    if (r.Bottom > maxY) maxY = r.Bottom;
                }
                
                // 3. Apply Padding/Border expansion
                // Only First Line gets Left padding/border
                if (i == 0)
                {
                    minX -= (float)(style.Padding.Left + bt.Left);
                }
                
                // Only Last Line gets Right padding/border
                if (i == lines.Count - 1)
                {
                    maxX += (float)(style.Padding.Right + bt.Right);
                }
                
                // All lines get Top/Bottom expansion
                minY -= (float)(style.Padding.Top + bt.Top);
                maxY += (float)(style.Padding.Bottom + bt.Bottom);
                
                var finalRect = new SKRect(minX, minY, maxX, maxY);
                
                // Determines Radii for this slice
                SKPoint[] sliceRadius = new SKPoint[4];
                if (fullRadius != null && fullRadius.Length == 4)
                {
                     if (i == 0) { sliceRadius[0] = fullRadius[0]; sliceRadius[3] = fullRadius[3]; }
                     if (i == lines.Count - 1) { sliceRadius[1] = fullRadius[1]; sliceRadius[2] = fullRadius[2]; }
                }

                // 4. Create Nodes
                if (hasBg)
                {
                    resultNodes.Add(new BackgroundPaintNode 
                    { 
                        Bounds = finalRect, 
                        Color = style.BackgroundColor,
                        SourceNode = node,
                        BorderRadius = sliceRadius,
                        IsFocused = isFocused,
                        IsHovered = isHovered
                    });
                }
                
                if (hasBorder)
                {
                    // For slice, we only draw side borders on ends
                    float leftW = (i == 0) ? (float)bt.Left : 0;
                    float rightW = (i == lines.Count - 1) ? (float)bt.Right : 0;
                    
                    var borderNode = new BorderPaintNode
                    {
                        Bounds = finalRect,
                        SourceNode = node,
                        Widths = new float[] { (float)bt.Top, rightW, (float)bt.Bottom, leftW },
                        Colors = new SKColor[] { style.BorderBrushColor ?? SKColors.Black, style.BorderBrushColor ?? SKColors.Black, style.BorderBrushColor ?? SKColors.Black, style.BorderBrushColor ?? SKColors.Black },
                        Styles = new string[] { style.BorderStyleTop, style.BorderStyleRight, style.BorderStyleBottom, style.BorderStyleLeft }, 
                        BorderRadius = sliceRadius,
                        IsFocused = isFocused,
                        IsHovered = isHovered
                    };
                    resultNodes.Add(borderNode);
                }
            }
            
            return resultNodes;
        }

        private void CollectContentRects(Node node, List<SKRect> rects)
        {
             if (_boxes.TryGetValue(node, out var box))
             {
                 if (node is Text && box.Lines != null)
                 {
                     foreach(var line in box.Lines)
                     {
                         float absX = box.ContentBox.Left + line.Origin.X;
                         float absY = box.ContentBox.Top + line.Origin.Y;
                         rects.Add(new SKRect(absX, absY, absX + line.Width, absY + line.Height));
                     }
                     return; 
                 }
                 if (node is Element elem)
                 {
                     _styles.TryGetValue(elem, out var style);
                     bool isAtomic = style?.Display == "inline-block" || elem.TagName == "IMG" || elem.TagName == "INPUT" || elem.TagName == "BUTTON";
                     if (isAtomic)
                     {
                         rects.Add(box.BorderBox);
                         return;
                     }
                 }
             }
             
             if (node is Element e && e.Children != null)
             {
                 foreach(var child in e.Children)
                 {
                     CollectContentRects(child, rects);
                 }
             }
        }

        private List<List<SKRect>> GroupRectsByLine(List<SKRect> rects)
        {
            rects.Sort((a, b) => a.Top.CompareTo(b.Top));
            var groups = new List<List<SKRect>>();
            if (rects.Count == 0) return groups;
            
            var currentGroup = new List<SKRect> { rects[0] };
            groups.Add(currentGroup);
            float groupY1 = rects[0].Top;
            float groupY2 = rects[0].Bottom;
            
            for (int i = 1; i < rects.Count; i++)
            {
                var r = rects[i];
                float overlapStart = Math.Max(groupY1, r.Top);
                float overlapEnd = Math.Min(groupY2, r.Bottom);
                
                if (overlapEnd > overlapStart)
                {
                    currentGroup.Add(r);
                    groupY1 = Math.Min(groupY1, r.Top);
                    groupY2 = Math.Max(groupY2, r.Bottom);
                }
                else
                {
                    currentGroup = new List<SKRect> { r };
                    groups.Add(currentGroup);
                    groupY1 = r.Top;
                    groupY2 = r.Bottom;
                }
            }
            return groups;
        }

        private BackgroundPaintNode BuildBackgroundNode(Node node, Layout.BoxModel box, CssComputed style, bool isFocused, bool isHovered)
        {
            SKColor? bgColor = style?.BackgroundColor;
            bool cssSpecifiedBackground = style?.Map != null &&
                                          (style.Map.ContainsKey("background") || style.Map.ContainsKey("background-color"));
            bool hasAuthoredBackground = cssSpecifiedBackground || !string.IsNullOrWhiteSpace(style?.BackgroundImage);

            if ((bgColor == null || bgColor.Value.Alpha == 0) &&
                !string.IsNullOrWhiteSpace(style?.BackgroundImage) &&
                !style.BackgroundImage.Contains("gradient", StringComparison.OrdinalIgnoreCase) &&
                !style.BackgroundImage.Contains("url(", StringComparison.OrdinalIgnoreCase))
            {
                bgColor = CssLoader.TryColor(style.BackgroundImage);
            }
            
            // UA defaults must not override authored background declarations.
            // Form controls frequently receive color through `background:` shorthand;
            // if that shorthand is present but the computed color stayed transparent,
            // the correct fallback is "no engine default fill", not a white UA repaint.
            if (!hasAuthoredBackground && (bgColor == null || bgColor.Value.Alpha == 0))
            {
                if (node is Element e)
                {
                    string tag = e.TagName?.ToUpperInvariant();
                    string type = e.GetAttribute("type")?.ToLowerInvariant();
                    
                    if (tag == "BUTTON" || (tag == "INPUT" && (type == "button" || type == "submit" || type == "reset")))
                    {
                        // Default Buttons to Light Gray
                        bgColor = new SKColor(240, 240, 240);
                    }
                    else if ((tag == "INPUT" && type != "hidden") || tag == "TEXTAREA")
                    {
                        // Default Inputs and Textareas to White
                         bgColor = SKColors.White;
                    }
                    else if (tag == "SELECT")
                    {
                        // Default Selects to White
                        bgColor = SKColors.White;
                    }
                }
            }
            
            if (bgColor == null || bgColor.Value.Alpha == 0)
                return null;
            
            // Handle background-clip
            // Default: border-box
            SKRect renderBounds = box.BorderBox;
            var radius = ExtractBorderRadius(box, style); // Outer radii (for border-box)

            string clip = style?.BackgroundClip ?? "border-box";
            if (clip == "padding-box")
            {
                renderBounds = box.PaddingBox;
                // Adjust radius if needed?
                // For now, using Outer Radii on Padding Box is slightly wrong (should be Inner Radii),
                // but NewPaintTreeBuilder usually handles clipping via specific nodes.
                // However, BackgroundPaintNode takes a radius.
                // Let's compute proper inner radius for padding-box.
                if (radius != null)
                {
                    float topW = (float)style.BorderThickness.Top;
                    float rightW = (float)style.BorderThickness.Right;
                    float botW = (float)style.BorderThickness.Bottom;
                    float leftW = (float)style.BorderThickness.Left;
                    
                    radius[0] = new SKPoint(Math.Max(0, radius[0].X - leftW), Math.Max(0, radius[0].Y - topW));
                    radius[1] = new SKPoint(Math.Max(0, radius[1].X - rightW), Math.Max(0, radius[1].Y - topW));
                    radius[2] = new SKPoint(Math.Max(0, radius[2].X - rightW), Math.Max(0, radius[2].Y - botW));
                    radius[3] = new SKPoint(Math.Max(0, radius[3].X - leftW), Math.Max(0, radius[3].Y - botW));
                }
            }
            else if (clip == "content-box")
            {
                renderBounds = box.ContentBox;
                // Further reduce radius for Content Box
                if (radius != null)
                {
                    float topW = (float)style.BorderThickness.Top + (float)style.Padding.Top;
                    float rightW = (float)style.BorderThickness.Right + (float)style.Padding.Right;
                    float botW = (float)style.BorderThickness.Bottom + (float)style.Padding.Bottom;
                    float leftW = (float)style.BorderThickness.Left + (float)style.Padding.Left;
                    
                    radius[0] = new SKPoint(Math.Max(0, radius[0].X - leftW), Math.Max(0, radius[0].Y - topW));
                    radius[1] = new SKPoint(Math.Max(0, radius[1].X - rightW), Math.Max(0, radius[1].Y - topW));
                    radius[2] = new SKPoint(Math.Max(0, radius[2].X - rightW), Math.Max(0, radius[2].Y - botW));
                    radius[3] = new SKPoint(Math.Max(0, radius[3].X - leftW), Math.Max(0, radius[3].Y - botW));
                }
            }

            // Gradient support: translate CSS gradient string into SKShader
            SKShader gradient = null;
            if (!string.IsNullOrEmpty(style?.BackgroundImage) && style.BackgroundImage.Contains("gradient", StringComparison.OrdinalIgnoreCase))
            {
                gradient = TryCreateGradient(style.BackgroundImage, renderBounds);
            }

            return new BackgroundPaintNode
            {
                Bounds = renderBounds,
                SourceNode = node,
                Color = gradient == null ? bgColor : null,
                Gradient = gradient,
                BorderRadius = radius,
                IsFocused = isFocused,
                IsHovered = isHovered
            };
        }

        private SKShader TryCreateGradient(string cssValue, SKRect bounds)
        {
            if (string.IsNullOrEmpty(cssValue)) return null;
            var trimmed = cssValue.Trim();

            if (trimmed.StartsWith("linear-gradient", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("repeating-linear-gradient", StringComparison.OrdinalIgnoreCase))
            {
                return CreateLinearGradientShader(trimmed, bounds);
            }

            if (trimmed.StartsWith("radial-gradient", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("repeating-radial-gradient", StringComparison.OrdinalIgnoreCase))
            {
                return CreateRadialGradientShader(trimmed, bounds);
            }

            if (trimmed.StartsWith("conic-gradient", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("repeating-conic-gradient", StringComparison.OrdinalIgnoreCase))
            {
                return CreateConicGradientShader(trimmed, bounds);
            }

            return null;
        }

        private SKShader CreateLinearGradientShader(string css, SKRect bounds)
        {
            int start = css.IndexOf('(');
            int end = css.LastIndexOf(')');
            if (start < 0 || end <= start) return null;

            var inner = css.Substring(start + 1, end - start - 1);
            var parts = SplitTopLevelComma(inner);
            if (parts.Count < 2) return null;

            string first = parts[0].Trim();
            double angleDeg = 180; // default "to bottom"
            int colorStartIndex = 0;

            if (first.Contains("deg", StringComparison.OrdinalIgnoreCase))
            {
                if (double.TryParse(first.Replace("deg", "").Trim(), out var deg))
                {
                    angleDeg = deg;
                    colorStartIndex = 1;
                }
            }
            else if (first.StartsWith("to ", StringComparison.OrdinalIgnoreCase))
            {
                colorStartIndex = 1;
                angleDeg = DirectionToAngle(first);
            }

            var colors = new List<SKColor>();
            var positions = new List<float>();

            for (int i = colorStartIndex; i < parts.Count; i++)
            {
                var stop = parts[i].Trim();
                if (string.IsNullOrEmpty(stop)) continue;

                string colorPart = stop;
                float? pos = null;

                int split = FindLastTopLevelSpace(stop);
                if (split > 0 && split < stop.Length - 1)
                {
                    var posStr = stop.Substring(split + 1).Trim();
                    colorPart = stop.Substring(0, split).Trim();
                    if (posStr.EndsWith("%") && float.TryParse(posStr.TrimEnd('%'), out var pct))
                    {
                        pos = Math.Clamp(pct / 100f, 0f, 1f);
                    }
                }

                if (SKColor.TryParse(colorPart, out var color))
                {
                    colors.Add(color);
                    if (pos.HasValue) positions.Add(pos.Value);
                }
            }

            if (colors.Count == 0) return null;
            float[] posArr = positions.Count == colors.Count ? positions.ToArray() : null;

            double rad = angleDeg * Math.PI / 180.0;
            var dir = new SKPoint((float)Math.Sin(rad), -(float)Math.Cos(rad)); // CSS 0deg = to top
            float half = (float)Math.Max(bounds.Width, bounds.Height);
            var center = new SKPoint(bounds.MidX, bounds.MidY);
            var startPt = new SKPoint(center.X - dir.X * half, center.Y - dir.Y * half);
            var endPt = new SKPoint(center.X + dir.X * half, center.Y + dir.Y * half);

            return SKShader.CreateLinearGradient(startPt, endPt, colors.ToArray(), posArr, SKShaderTileMode.Clamp);
        }

        private SKShader CreateRadialGradientShader(string css, SKRect bounds)
        {
            int start = css.IndexOf('(');
            int end = css.LastIndexOf(')');
            if (start < 0 || end <= start) return null;

            var inner = css.Substring(start + 1, end - start - 1);
            var parts = SplitTopLevelComma(inner);
            if (parts.Count < 2) return null;

            var center = new SKPoint(bounds.MidX, bounds.MidY);
            float radius = Math.Max(bounds.Width, bounds.Height) / 2f;

            var colors = new List<SKColor>();
            var positions = new List<float>();

            for (int i = 0; i < parts.Count; i++)
            {
                var stop = parts[i].Trim();
                if (string.IsNullOrEmpty(stop)) continue;

                string colorPart = stop;
                float? pos = null;
                int split = FindLastTopLevelSpace(stop);
                if (split > 0 && split < stop.Length - 1)
                {
                    var posStr = stop.Substring(split + 1).Trim();
                    colorPart = stop.Substring(0, split).Trim();
                    if (posStr.EndsWith("%") && float.TryParse(posStr.TrimEnd('%'), out var pct))
                    {
                        pos = Math.Clamp(pct / 100f, 0f, 1f);
                    }
                }

                if (SKColor.TryParse(colorPart, out var color))
                {
                    colors.Add(color);
                    if (pos.HasValue) positions.Add(pos.Value);
                }
            }

            if (colors.Count == 0) return null;
            float[] posArr = positions.Count == colors.Count ? positions.ToArray() : null;

            return SKShader.CreateRadialGradient(center, radius, colors.ToArray(), posArr, SKShaderTileMode.Clamp);
        }

        private SKShader CreateConicGradientShader(string css, SKRect bounds)
        {
            // conic-gradient([from <angle>] [at <position>,] <color-stop-list>)
            // We map this to SKShader.CreateSweepGradient which sweeps 0→360° around a center.
            int pStart = css.IndexOf('(');
            int pEnd = css.LastIndexOf(')');
            if (pStart < 0 || pEnd <= pStart) return null;

            var inner = css.Substring(pStart + 1, pEnd - pStart - 1);
            var parts = SplitTopLevelComma(inner);
            if (parts.Count < 2) return null;

            float startAngleDeg = 0f;
            var center = new SKPoint(bounds.MidX, bounds.MidY);
            int colorStartIndex = 0;

            // Check for "from <angle>" or "at <position>" preamble in first segment
            string first = parts[0].Trim();
            if (first.StartsWith("from ", StringComparison.OrdinalIgnoreCase))
            {
                var anglePart = first.Substring(5).Trim();
                if (anglePart.EndsWith("deg", StringComparison.OrdinalIgnoreCase) &&
                    float.TryParse(anglePart.Substring(0, anglePart.Length - 3).Trim(), out var deg))
                {
                    startAngleDeg = deg;
                }
                colorStartIndex = 1;
            }
            else if (first.StartsWith("at ", StringComparison.OrdinalIgnoreCase))
            {
                // skip position specifier — use default center
                colorStartIndex = 1;
            }

            var colors = new List<SKColor>();
            var positions = new List<float>();

            for (int i = colorStartIndex; i < parts.Count; i++)
            {
                var stop = parts[i].Trim();
                if (string.IsNullOrEmpty(stop)) continue;

                string colorPart = stop;
                float? pos = null;
                int split = FindLastTopLevelSpace(stop);
                if (split > 0 && split < stop.Length - 1)
                {
                    var posStr = stop.Substring(split + 1).Trim();
                    colorPart = stop.Substring(0, split).Trim();
                    if (posStr.EndsWith("%") && float.TryParse(posStr.TrimEnd('%'), out var pct))
                        pos = Math.Clamp(pct / 100f, 0f, 1f);
                    else if (posStr.EndsWith("deg", StringComparison.OrdinalIgnoreCase) &&
                             float.TryParse(posStr.Substring(0, posStr.Length - 3).Trim(), out var degPos))
                        pos = Math.Clamp(degPos / 360f, 0f, 1f);
                }

                if (SKColor.TryParse(colorPart, out var color))
                {
                    colors.Add(color);
                    if (pos.HasValue) positions.Add(pos.Value);
                }
            }

            if (colors.Count == 0) return null;
            float[] posArr = positions.Count == colors.Count ? positions.ToArray() : null;

            // SKShader.CreateSweepGradient sweeps from startAngle to startAngle+360 around center.
            return SKShader.CreateSweepGradient(center, colors.ToArray(), posArr,
                SKShaderTileMode.Repeat, startAngleDeg, startAngleDeg + 360f);
        }

        private static double DirectionToAngle(string dir)
        {
            dir = dir.ToLowerInvariant().Replace("to", "").Trim();
            bool up = dir.Contains("top");
            bool down = dir.Contains("bottom");
            bool left = dir.Contains("left");
            bool right = dir.Contains("right");

            if (up && right) return 45;
            if (down && right) return 135;
            if (down && left) return 225;
            if (up && left) return 315;
            if (right) return 90;
            if (down) return 180;
            if (left) return 270;
            return 0;
        }

        private static int FindLastTopLevelSpace(string input)
        {
            int depth = 0;
            for (int i = input.Length - 1; i >= 0; i--)
            {
                char c = input[i];
                if (c == ')') depth++;
                else if (c == '(') depth--;
                else if (c == ' ' && depth == 0) return i;
            }
            return -1;
        }

        private static List<string> SplitTopLevelComma(string value)
        {
            var results = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    results.Add(value.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < value.Length) results.Add(value.Substring(start));
            return results;
        }

        private ImagePaintNode BuildBackgroundImageNode(Node node, Layout.BoxModel box, CssComputed style)
        {
            if (string.IsNullOrEmpty(style?.BackgroundImage) || style.BackgroundImage == "none") return null;

            string url = style.BackgroundImage.Trim();
            if (url.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                url = url.Substring(4, url.Length - 5).Trim('\'', '\"', ' ');
            }
            else
            {
                // Might be a gradient or other value we don't support yet as image
                return null;
            }

            // Resolve Relative URLs
            if (!string.IsNullOrEmpty(url) && 
                !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && 
                !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && 
                !string.IsNullOrEmpty(_baseUri))
            {
                try 
                {
                    var uri = new Uri(new Uri(_baseUri), url);
                    url = uri.ToString();
                }
                catch (Exception ex) { global::FenBrowser.Core.EngineLogCompat.Warn($"[IMG-BUILD] Failed resolving image URL against base URI: {ex.Message}", FenBrowser.Core.Logging.LogCategory.Rendering); }
            }

            var bitmap = ImageLoader.GetImage(url);
            global::FenBrowser.Core.EngineLogCompat.Debug($"[BG-IMG] URL={(url?.Length > 60 ? url.Substring(0, 60) + "..." : url)} Bitmap={(bitmap != null ? $"{bitmap.Width}x{bitmap.Height}" : "NULL")}");
            
            if (bitmap == null) return null;

            // Handle BackgroundSize and BackgroundPosition for Sprites
            SKRect? srcRect = null;
            if (bitmap.Width > 0 && bitmap.Height > 0)
            {
                float bgW = bitmap.Width;
                float bgH = bitmap.Height;

                // Simple Size Parsing (px only for now)
                if (!string.IsNullOrEmpty(style.BackgroundSize) && style.BackgroundSize.Contains("px"))
                {
                    var sizeParts = style.BackgroundSize.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (sizeParts.Length >= 2 && sizeParts[0].EndsWith("px") && sizeParts[1].EndsWith("px"))
                    {
                        float.TryParse(sizeParts[0].Replace("px", ""), out bgW);
                        float.TryParse(sizeParts[1].Replace("px", ""), out bgH);
                    }
                    else if (sizeParts.Length == 1 && sizeParts[0].EndsWith("px"))
                    {
                         float.TryParse(sizeParts[0].Replace("px", ""), out bgW);
                         // height auto
                    }
                }

                // Simple Position Parsing (px only for now)
                float posX = 0, posY = 0;
                if (!string.IsNullOrEmpty(style.BackgroundPosition) && style.BackgroundPosition.Contains("px"))
                {
                    var posParts = style.BackgroundPosition.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (posParts.Length >= 2)
                    {
                        float.TryParse(posParts[0].Replace("px", ""), out posX);
                        float.TryParse(posParts[1].Replace("px", ""), out posY);
                    }
                }

                // Scale factor if background-size differs from natural bitmap size
                float scaleX = bitmap.Width / bgW;
                float scaleY = bitmap.Height / bgH;

                // CSS position is often negative for sprites (offset from top-left)
                // Source Rect = ( -posX * scaleX, -posY * scaleY, boxWidth * scaleX, boxHeight * scaleY )
                srcRect = new SKRect(
                    -posX * scaleX,
                    -posY * scaleY,
                    (-posX + (float)box.PaddingBox.Width) * scaleX,
                    (-posY + (float)box.PaddingBox.Height) * scaleY
                );

                // Sanity check: cap to bitmap bounds
                srcRect = new SKRect(
                    Math.Max(0, srcRect.Value.Left),
                    Math.Max(0, srcRect.Value.Top),
                    Math.Min(bitmap.Width, srcRect.Value.Right),
                    Math.Min(bitmap.Height, srcRect.Value.Bottom)
                );
            }

            var clipBounds = ResolveBackgroundPaintBounds(box, style);
            var origin = ResolveBackgroundOriginPoint(box, style);
            var position = ResolveBackgroundPosition(style?.BackgroundPosition, clipBounds, bitmap, origin);
            var (tileModeX, tileModeY) = ResolveBackgroundTileModes(style?.BackgroundRepeat);

            float fixedOriginX = 0;
            float fixedOriginY = 0;
            if (string.Equals(style?.BackgroundAttachment, "fixed", StringComparison.OrdinalIgnoreCase))
            {
                var viewportScroll = _scrollManager?.GetScrollOffset(null) ?? (0f, 0f);
                fixedOriginX = viewportScroll.x;
                fixedOriginY = viewportScroll.y;
            }

            return new ImagePaintNode
            {
                Bounds = clipBounds,
                SourceNode = node,
                Bitmap = bitmap,
                SourceRect = srcRect,
                ObjectFit = "none",
                IsBackgroundImage = true,
                TileModeX = tileModeX,
                TileModeY = tileModeY,
                BackgroundOrigin = origin,
                BackgroundPosition = position,
                BackgroundAttachmentFixed = string.Equals(style?.BackgroundAttachment, "fixed", StringComparison.OrdinalIgnoreCase),
                FixedViewportOrigin = new SKPoint(fixedOriginX, fixedOriginY)
            };
        }
        
        private BorderPaintNode BuildBorderNode(Node node, Layout.BoxModel box, CssComputed style, bool isFocused, bool isHovered)
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
                    string tag = e.TagName?.ToUpperInvariant();
                    string type = e.GetAttribute("type")?.ToLowerInvariant();
                    
                    if ((tag == "INPUT" && type != "hidden") || tag == "BUTTON" || (tag == "INPUT" && (type == "button" || type == "submit" || type == "reset")))
                    {
                        // Default Border
                        widths = new float[4] { 1, 1, 1, 1 };
                    }
                 }
            }

            if (widths == null || widths.All(w => w <= 0)) return null;
            
            SKColor borderColor = (style?.BorderBrushColor) ?? SKColors.Black;
            var colors = ResolveBorderColors(style, borderColor);
            
            string[] styles = new string[4]
            {
                style?.BorderStyleTop ?? "solid",
                style?.BorderStyleRight ?? "solid",
                style?.BorderStyleBottom ?? "solid",
                style?.BorderStyleLeft ?? "solid"
            };
            
            return new BorderPaintNode
            {
                Bounds = box.BorderBox,
                SourceNode = node,
                Widths = widths,
                Colors = colors,
                Styles = styles,
                BorderRadius = ExtractBorderRadius(box, style),
                IsFocused = isFocused,
                IsHovered = isHovered
            };
        }

        private List<BoxShadowPaintNode> BuildBoxShadowNodes(Node node, SKRect bounds, CssComputed style, Layout.BoxModel box)
        {
            if (string.IsNullOrEmpty(style?.BoxShadow) || style.BoxShadow == "none") return null;
            return ParseBoxShadows(node, style.BoxShadow, bounds, ExtractBorderRadius(box, style));
        }

        private List<BoxShadowPaintNode> ParseBoxShadows(Node node, string shadowStr, SKRect bounds, SKPoint[] borderRadius)
        {
            var results = new List<BoxShadowPaintNode>();

            // Split by commas, but respect parentheses (rgba contains commas)
            var shadows = SplitShadowValues(shadowStr);

            foreach (var single in shadows)
            {
                var parsed = ParseSingleBoxShadow(node, single.Trim(), bounds, borderRadius);
                if (parsed != null) results.Add(parsed);
            }

            return results.Count > 0 ? results : null;
        }

        private static List<string> SplitShadowValues(string value)
        {
            var results = new List<string>();
            int depth = 0;
            int start = 0;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    results.Add(value.Substring(start, i - start));
                    start = i + 1;
                }
            }

            if (start < value.Length)
                results.Add(value.Substring(start));

            return results;
        }

        private BoxShadowPaintNode ParseSingleBoxShadow(Node node, string shadowStr, SKRect bounds, SKPoint[] borderRadius)
        {
            try
            {
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
        
        private static readonly SkiaFontService _fontService = new SkiaFontService();

        // CHANGED: Returned list of nodes to support multi-line text
        private List<TextPaintNode> BuildTextNode(Text textNode, Layout.BoxModel box, CssComputed style, bool isFocused, bool isHovered)
        {
            if (string.IsNullOrEmpty(textNode.Data)) return null;
            
            // Get parent style for text rendering
            var parentStyle = style;
            if (parentStyle == null && textNode.ParentElement != null)
            {
                _styles.TryGetValue(textNode.ParentElement, out parentStyle);
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
            
            if (!string.IsNullOrWhiteSpace(decorValue))
            {
                if (!decorValue.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    textDecorations = decorValue.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                }
                // If "none", textDecorations remains null, effectively clearing any defaults
            }
            else
            {
                // UA Default: Underline for links - ONLY if property is not specified
                var ancestor = textNode.ParentElement;
                bool inNav = false;
                Element anchorEl = null;
                while (ancestor != null)
                {
                    string tag = ancestor.TagName?.ToUpperInvariant();
                    if (tag == "NAV" || tag == "HEADER" || ancestor.Id == "main-nav" || ancestor.Id == "site") inNav = true;
                    if (tag == "A") 
                    {
                        anchorEl = ancestor;
                        break; 
                    }
                    ancestor = ancestor.ParentElement;
                }
                
                if (anchorEl != null && !inNav)
                {
                    // Check if anchor explicitly disabled underlines
                    _styles.TryGetValue(anchorEl, out var anchorStyle);
                    bool explicitlyNone = anchorStyle != null && 
                                        string.Equals(anchorStyle.TextDecoration, "none", StringComparison.OrdinalIgnoreCase);

                    if (!explicitlyNone)
                    {
                        string href = anchorEl.GetAttribute("href");
                        if (!string.IsNullOrWhiteSpace(href)) textDecorations = new List<string> { "underline" };
                    }
                }
            }
            
            // Text color
            // Respect explicit transparent text. Some pages, including Google's searchbox
            // mirror layers, intentionally paint hidden overlay text with color: transparent.
            SKColor color = parentStyle?.ForegroundColor ?? SKColors.Black;

            // MULTI-LINE SUPPORT
            // If BoxModel has Lines populated (from TextLayoutComputer), use them.
            if (box.Lines != null && box.Lines.Count > 0)
            {
                // DEBUG: Log geometry for alignment test nodes
                if (textNode.NodeName == "#text" && (box.Lines.Count < 5 || box.Lines.Count > 50))
                {
                    FenBrowser.Core.EngineLogCompat.Debug($"[PAINT-GEOMETRY] Node={textNode.GetHashCode()} Lines={box.Lines.Count} Box={box.ContentBox}");
                    foreach(var l in box.Lines)
                    {
                         var finalX = box.ContentBox.Left + l.Origin.X;
                         var finalY = box.ContentBox.Top + l.Origin.Y;
                         FenBrowser.Core.EngineLogCompat.Debug($"   - Line: '{l.Text}' Origin=({l.Origin.X}, {l.Origin.Y}) Final=({finalX}, {finalY})");
                    }
                }

                var list = new List<TextPaintNode>();

                
                // Calculate total line height to determine vertical centering offset
                float totalLineHeight = 0;
                foreach (var l in box.Lines)
                {
                    totalLineHeight += l.Height;
                }
                
                // FIX: Vertically center text when parent container is taller than text content
                // For stretched flex items, parent box height > text line height, so we offset the text
                float mlContainerHeight = totalLineHeight; // Default: no centering
                SKRect parentContentBox = box.ContentBox; // Default: use text's own box
                
                if (textNode.ParentNode != null && _boxes.TryGetValue(textNode.ParentNode, out var mlParentBox))
                {
                    parentContentBox = mlParentBox.ContentBox;
                    mlContainerHeight = parentContentBox.Height;
                }
                
                float verticalCenterOffset = 0;
                if (mlContainerHeight > totalLineHeight && totalLineHeight > 0)
                {
                    // FIXED: This logic was conflicting with InlineLayoutComputer which already positions lines.
                    // The line boxes are already laid out relative to their container.
                    // Vertically centering them again here pushes them out of bounds if container is taller.
                    verticalCenterOffset = 0;
                }
                
                // DEBUG: Log multi-line positioning values
                var sampleText = box.Lines.Count > 0 ? box.Lines[0].Text : "";
                // Show full text to debug whitespace
                if (FenBrowser.Core.Logging.DebugConfig.LogLayoutConstraints)
                {
                    FenBrowser.Core.EngineLogCompat.Info($"[ML-TEXT-POS] '{sampleText}' (Len={sampleText.Length}) TextTop={box.ContentBox.Top:F1} ParentTop={parentContentBox.Top:F1} ContainerH={mlContainerHeight:F1} TotalLH={totalLineHeight:F1} Lines={box.Lines.Count} Offset={verticalCenterOffset:F1}", FenBrowser.Core.Logging.LogCategory.Layout);
                }
                
                // Check if parent element requires text-overflow ellipsis
                bool applyEllipsis = false;
                float containerWidth = 0;
                if (textNode.ParentNode != null)
                {
                    CssComputed pStyle = null;
                    _styles.TryGetValue(textNode.ParentNode, out pStyle);
                    if (pStyle != null &&
                        string.Equals(pStyle.TextOverflow, "ellipsis", StringComparison.OrdinalIgnoreCase) &&
                        (pStyle.Overflow == "hidden" || pStyle.OverflowX == "hidden"))
                    {
                        applyEllipsis = true;
                        if (_boxes.TryGetValue(textNode.ParentNode, out var parentBox))
                        {
                            containerWidth = parentBox.ContentBox.Width;
                        }
                    }
                }

                foreach (var line in box.Lines)
                {
                    if (string.IsNullOrEmpty(line.Text))
                    {
                        continue;
                    }

                    // Calculate absolute bounds for this line
                    float absX = box.ContentBox.Left + line.Origin.X;
                    float absY = box.ContentBox.Top + line.Origin.Y + verticalCenterOffset;
                    float resolvedLineWidth = line.Width;
                    if (resolvedLineWidth <= 0 && !string.IsNullOrEmpty(line.Text))
                    {
                        var measurementStyle = parentStyle ?? style ?? new CssComputed();
                        string measurementFamily = measurementStyle.FontFamilyName ?? "Segoe UI";
                        float measurementSize = (float)(measurementStyle.FontSize ?? 16);
                        int measurementWeight = measurementStyle.FontWeight ?? 400;
                        resolvedLineWidth = _fontService.MeasureTextWidth(line.Text, measurementFamily, measurementSize, measurementWeight);
                        if (resolvedLineWidth <= 0) resolvedLineWidth = measurementSize * 0.6f;
                    }
                    if (resolvedLineWidth <= 0.01f || line.Height <= 0.01f)
                    {
                        continue;
                    }

                    // Apply text-overflow: ellipsis if needed
                    string lineDisplayText = NormalizeRenderableText(line.Text);
                    if (applyEllipsis && containerWidth > 0 && resolvedLineWidth > containerWidth)
                    {
                        using var measurePaint = new SKPaint { Typeface = typeface, TextSize = fontSize, IsAntialias = true };
                        float ellipsisWidth = measurePaint.MeasureText("...");
                        float availableForText = containerWidth - ellipsisWidth;

                        if (availableForText > 0)
                        {
                            string text = line.Text;
                            int lo = 0, hi = text.Length;
                            while (lo < hi)
                            {
                                int mid = (lo + hi + 1) / 2;
                                float w = measurePaint.MeasureText(text.Substring(0, mid));
                                if (w <= availableForText) lo = mid;
                                else hi = mid - 1;
                            }

                            lineDisplayText = lo > 0 ? text.Substring(0, lo) + "..." : "...";
                        }
                        else
                        {
                            lineDisplayText = "...";
                        }
                        resolvedLineWidth = containerWidth;
                    }

                    var lineBounds = new SKRect(absX, absY, absX + resolvedLineWidth, absY + line.Height);

                    // Origin for text drawing (Baseline)
                    var textOrigin = new SKPoint(absX, absY + line.Baseline);
                    var glyphs = BuildPaintGlyphs(lineDisplayText, fontFamily, fontSize, weight, textOrigin);

                    if (ShouldTraceWhatIsMyBrowserText(lineDisplayText))
                    {
                        string parentTag = textNode.ParentElement?.TagName ?? "null";
                        string grandParentTag = textNode.ParentElement?.ParentElement?.TagName ?? "null";
                        string boxSummary = $"Box=({box.ContentBox.Left:F1},{box.ContentBox.Top:F1},{box.ContentBox.Width:F1}x{box.ContentBox.Height:F1})";
                        string parentSummary = "ParentBox=null";
                        if (textNode.ParentNode != null && _boxes.TryGetValue(textNode.ParentNode, out var traceParentBox) && traceParentBox != null)
                        {
                            parentSummary = $"ParentBox=({traceParentBox.ContentBox.Left:F1},{traceParentBox.ContentBox.Top:F1},{traceParentBox.ContentBox.Width:F1}x{traceParentBox.ContentBox.Height:F1})";
                        }

                        global::FenBrowser.Core.EngineLogCompat.Info(
                            $"[WIMB-TEXT-BUILD] Text='{lineDisplayText}' Parent=<{parentTag}> GrandParent=<{grandParentTag}> Color=#{color.Red:X2}{color.Green:X2}{color.Blue:X2}{color.Alpha:X2} {boxSummary} {parentSummary} LineOrigin=({line.Origin.X:F1},{line.Origin.Y:F1}) LineSize=({resolvedLineWidth:F1}x{line.Height:F1}) Baseline={line.Baseline:F1} DrawOrigin=({textOrigin.X:F1},{textOrigin.Y:F1})",
                            LogCategory.Rendering);
                    }

                    list.Add(new TextPaintNode
                    {
                        Bounds = lineBounds,
                        SourceNode = textNode,
                        Color = color,
                        FontSize = fontSize,
                        Typeface = typeface,
                        Glyphs = glyphs,
                        TextOrigin = textOrigin,
                        FallbackText = lineDisplayText,
                        TextDecorations = textDecorations,
                        IsFocused = isFocused,
                        IsHovered = isHovered
                    });
                }
                if (list.Count > 0) return list;
            }



            // FALLBACK (Single Line) - OLD LOGIC
            string displayText = System.Text.RegularExpressions.Regex.Replace(textNode.Data, @"\s+", " ");
            if (displayText.Contains("&#"))
            {
                displayText = displayText.Replace("&#10003;", "✔")
                                         .Replace("&#x2713;", "✔")
                                         .Replace("&#10004;", "✔")
                                         .Replace("&#x2714;", "✔")
                                         .Replace("&#10007;", "✘")
                                         .Replace("&#10008;", "✘")
                                         .Replace("&#x2717;", "✘")
                                         .Replace("&#x2718;", "✘");
            }
            if (displayText.Contains("&amp;")) displayText = displayText.Replace("&amp;", "&");
            displayText = NormalizeRenderableText(System.Net.WebUtility.HtmlDecode(displayText));
            if (string.IsNullOrWhiteSpace(displayText))
            {
                return null;
            }

            float fallbackTextWidth = _fontService.MeasureTextWidth(
                displayText,
                parentStyle?.FontFamilyName ?? style?.FontFamilyName ?? "Segoe UI",
                fontSize,
                parentStyle?.FontWeight ?? style?.FontWeight ?? 400);
            if (fallbackTextWidth <= 0.01f && box.ContentBox.Width <= 0.01f)
            {
                return null;
            }

            // Apply text-overflow: ellipsis for fallback single-line path
            if (textNode.ParentNode != null)
            {
                CssComputed fbParentStyle = null;
                _styles.TryGetValue(textNode.ParentNode, out fbParentStyle);
                if (fbParentStyle != null &&
                    string.Equals(fbParentStyle.TextOverflow, "ellipsis", StringComparison.OrdinalIgnoreCase) &&
                    (fbParentStyle.Overflow == "hidden" || fbParentStyle.OverflowX == "hidden"))
                {
                    float fbContainerWidth = 0;
                    if (_boxes.TryGetValue(textNode.ParentNode, out var fbParentBox))
                    {
                        fbContainerWidth = fbParentBox.ContentBox.Width;
                    }

                    if (fbContainerWidth > 0 && fallbackTextWidth > fbContainerWidth)
                    {
                        using var fbMeasurePaint = new SKPaint { Typeface = typeface, TextSize = fontSize, IsAntialias = true };
                        float fbEllipsisWidth = fbMeasurePaint.MeasureText("...");
                        float fbAvailable = fbContainerWidth - fbEllipsisWidth;

                        if (fbAvailable > 0)
                        {
                            int lo = 0, hi = displayText.Length;
                            while (lo < hi)
                            {
                                int mid = (lo + hi + 1) / 2;
                                float w = fbMeasurePaint.MeasureText(displayText.Substring(0, mid));
                                if (w <= fbAvailable) lo = mid;
                                else hi = mid - 1;
                            }

                            displayText = lo > 0 ? displayText.Substring(0, lo) + "..." : "...";
                        }
                        else
                        {
                            displayText = "...";
                        }
                        fallbackTextWidth = fbContainerWidth;
                    }
                }
            }

            float fbLineHeight = fontSize * 1.2f;
            float resolvedBoxHeight = Math.Max(box.ContentBox.Height, fbLineHeight);
            float resolvedBoxWidth = Math.Max(box.ContentBox.Width, fallbackTextWidth);
            if (resolvedBoxWidth <= 0.01f || resolvedBoxHeight <= 0.01f)
            {
                return null;
            }

            var drawBounds = new SKRect(
                box.ContentBox.Left,
                box.ContentBox.Top,
                box.ContentBox.Left + resolvedBoxWidth,
                box.ContentBox.Top + resolvedBoxHeight);
            float baselineY = drawBounds.Top + fontSize * 0.85f;
            
            // DEBUG: Log positioning values to understand the issue
            if (FenBrowser.Core.Logging.DebugConfig.LogLayoutConstraints)
            {
                FenBrowser.Core.EngineLogCompat.Info($"[TEXT-POS] '{displayText.Substring(0, Math.Min(20, displayText.Length))}...' TextBoxTop={drawBounds.Top} TextBoxH={drawBounds.Height} TextBoxW={drawBounds.Width} LineH={fbLineHeight} BaselineY={baselineY}", FenBrowser.Core.Logging.LogCategory.Layout);
            }

            if (ShouldTraceWhatIsMyBrowserText(displayText))
            {
                string parentTag = textNode.ParentElement?.TagName ?? "null";
                string grandParentTag = textNode.ParentElement?.ParentElement?.TagName ?? "null";
                string boxSummary = $"Box=({box.ContentBox.Left:F1},{box.ContentBox.Top:F1},{box.ContentBox.Width:F1}x{box.ContentBox.Height:F1})";
                string parentSummary = "ParentBox=null";
                if (textNode.ParentNode != null && _boxes.TryGetValue(textNode.ParentNode, out var traceParentBox) && traceParentBox != null)
                {
                    parentSummary = $"ParentBox=({traceParentBox.ContentBox.Left:F1},{traceParentBox.ContentBox.Top:F1},{traceParentBox.ContentBox.Width:F1}x{traceParentBox.ContentBox.Height:F1})";
                }

                global::FenBrowser.Core.EngineLogCompat.Info(
                    $"[WIMB-TEXT-FALLBACK] Text='{displayText}' Parent=<{parentTag}> GrandParent=<{grandParentTag}> Color=#{color.Red:X2}{color.Green:X2}{color.Blue:X2}{color.Alpha:X2} {boxSummary} {parentSummary} DrawBounds=({drawBounds.Left:F1},{drawBounds.Top:F1},{drawBounds.Width:F1}x{drawBounds.Height:F1}) BaselineY={baselineY:F1}",
                    LogCategory.Rendering);
            }
            
            return new List<TextPaintNode> 
            {
                new TextPaintNode
                {
                    Bounds = drawBounds,
                    SourceNode = textNode,
                    Color = color,
                    FontSize = fontSize,
                    Typeface = typeface,
                    Glyphs = BuildPaintGlyphs(displayText, fontFamily, fontSize, weight, new SKPoint(drawBounds.Left, baselineY)),
                    TextOrigin = new SKPoint(drawBounds.Left, baselineY),
                    FallbackText = displayText,
                    TextDecorations = textDecorations,
                    WritingMode = parentStyle?.WritingMode,
                    IsFocused = isFocused,
                    IsHovered = isHovered
                }
            };

        }
        
        private ImagePaintNode BuildImageOrSvgNode(Element elem, Layout.BoxModel box, CssComputed style, bool isFocused, bool isHovered)
        {
            string url = null;
            string tag = elem.TagName?.ToUpperInvariant();
            
            if (tag == "IMG")
            {
                url = ResponsiveImageSourceSelector.PickCurrentImageSource(elem, _viewportWidth, _viewportHeight);

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
                    catch (Exception ex) { global::FenBrowser.Core.EngineLogCompat.Warn($"[IMG-BUILD] Failed normalizing image URL: {ex.Message}", FenBrowser.Core.Logging.LogCategory.Rendering); }
                }

                global::FenBrowser.Core.EngineLogCompat.Debug($"[IMG-BUILD] Tag={tag} URL={(url?.Length > 80 ? url?.Substring(0, 80) + "..." : url)}");
                var bitmap = ImageLoader.GetImage(url);
                
                return new ImagePaintNode
                {
                    Bounds = box.ContentBox,
                    SourceNode = elem,
                    Bitmap = bitmap,
                    ObjectFit = style?.ObjectFit ?? "fill"
                };
            }
            else if (tag == "OBJECT")
            {
                if (ReplacedElementSizing.ShouldUseObjectFallbackContent(elem))
                {
                    return null;
                }

                string dataUrl = elem.GetAttribute("data");
                if (string.IsNullOrWhiteSpace(dataUrl))
                {
                    return null;
                }

                string resolvedUrl = NormalizeResourceUrl(dataUrl);
                var bitmap = ImageLoader.GetImage(resolvedUrl);
                if (bitmap == null)
                {
                    return null;
                }

                return new ImagePaintNode
                {
                    Bounds = box.ContentBox,
                    SourceNode = elem,
                    Bitmap = bitmap,
                    ObjectFit = style?.ObjectFit ?? "fill"
                };
            }
            else if (tag == "SVG" || tag.EndsWith(":SVG", StringComparison.Ordinal))
            {
                // Internal method to re-render SVG with color resolution
                string svgContent = elem.ToHtml(); // Basic capture
                
                // Resolve CSS variables in SVG content
                if (style != null && style.CustomProperties != null && style.CustomProperties.Count > 0 && svgContent.Contains("var("))
                {
                    foreach (var varKvp in style.CustomProperties)
                    {
                        string varFunc = $"var({varKvp.Key})";
                        if (svgContent.Contains(varFunc))
                        {
                            svgContent = svgContent.Replace(varFunc, varKvp.Value);
                        }
                    }
                }

                // Resolve remaining CSS var() usage so Skia receives concrete paint values.
                if (svgContent.Contains("var("))
                {
                    // Use fallback value when present: var(--x, fallback) -> fallback
                    svgContent = Regex.Replace(
                        svgContent,
                        @"var\(\s*--[^,\)]+\s*,\s*([^)]+)\)",
                        "$1",
                        RegexOptions.IgnoreCase);

                    // Remaining unresolved custom properties fallback to currentColor.
                    svgContent = Regex.Replace(
                        svgContent,
                        @"var\([^)]+\)",
                        "currentColor",
                        RegexOptions.IgnoreCase);
                }
                
                // Final currentColor fallback if any left.
                // Some inline SVG icons (notably Google material symbols) rely on inherited color;
                // if computed style is missing on the SVG node, fall back to parent/black so the icon stays visible.
                if (svgContent.IndexOf("currentColor", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var fg = style?.ForegroundColor ?? SKColors.Empty;
                    // Walk up ancestor chain to find an inherited foreground color.
                    // Google Material symbols set color via CSS vars on parent containers;
                    // checking only one parent is insufficient.
                    if (fg == SKColors.Empty || fg.Alpha == 0)
                    {
                        var ancestor = elem.ParentElement;
                        while (ancestor != null)
                        {
                            try
                            {
                                CssComputed ancStyle = null;
                                if (_styles.TryGetValue(ancestor, out ancStyle) ||
                                    (ancStyle = ancestor.GetComputedStyle()) != null)
                                {
                                    if (ancStyle?.ForegroundColor != null &&
                                        ancStyle.ForegroundColor.Value != SKColors.Empty &&
                                        ancStyle.ForegroundColor.Value.Alpha > 0)
                                    {
                                        fg = ancStyle.ForegroundColor.Value;
                                        break;
                                    }
                                }
                            }
                            catch (Exception ex) { global::FenBrowser.Core.EngineLogCompat.Warn($"[IMG-BUILD] Failed parsing ancestor style while resolving image URL: {ex.Message}", FenBrowser.Core.Logging.LogCategory.Rendering); }
                            ancestor = ancestor.ParentElement;
                        }
                    }
                    if (fg == SKColors.Empty || fg.Alpha == 0)
                        fg = SKColors.Black;

                    string hexColor = $"#{fg.Red:X2}{fg.Green:X2}{fg.Blue:X2}";
                    svgContent = Regex.Replace(svgContent, "currentColor", hexColor, RegexOptions.IgnoreCase);
                }

                if (!svgContent.Contains("xmlns=\"http://www.w3.org/2000/svg\"") && 
                    !svgContent.Contains("xmlns='http://www.w3.org/2000/svg'"))
                {
                    if (svgContent.Contains("<svg "))
                        svgContent = svgContent.Replace("<svg ", "<svg xmlns=\"http://www.w3.org/2000/svg\" ");
                    else if (svgContent.Contains("<svg>"))
                        svgContent = svgContent.Replace("<svg>", "<svg xmlns=\"http://www.w3.org/2000/svg\">");
                }

                // Normalization: SkiaSharp.Svg is case-sensitive for certain attributes
                if (svgContent.Contains("viewbox="))
                {
                    svgContent = svgContent.Replace("viewbox=", "viewBox=");
                }

                // Re-rasterize with resolved colors
                var bitmap = RenderSvgToCachedBitmap(svgContent, (int)box.ContentBox.Width, (int)box.ContentBox.Height);

                return new ImagePaintNode
                {
                    Bounds = box.ContentBox,
                    SourceNode = elem,
                    Bitmap = bitmap,
                    ObjectFit = style?.ObjectFit ?? "fill"
                };
            }

            return null;
        }

        private SKBitmap RenderSvgToCachedBitmap(string svgContent, int width, int height)
        {


                
            if (width <= 0) width = 24;
            if (height <= 0) height = 24;

            string dataUri = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svgContent));
            var tuple = ImageLoader.GetImageTuple(dataUri, false, null, width, height);
            
            if (tuple is (SKBitmap bitmap, bool _))
            {
                return bitmap;
            }
            return null;
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
             
             // Use PaddingBox for alignment so submit/button text centers within padded controls.
             float x = box.PaddingBox.Left;
             var metrics = paint.FontMetrics;
             float textHeight = metrics.Descent - metrics.Ascent;
             float y = box.PaddingBox.Top + (box.PaddingBox.Height - textHeight) / 2f - metrics.Ascent;

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
                 x = box.PaddingBox.Left + (box.PaddingBox.Width - textWidth) / 2;
             }
             else if (align == SKTextAlign.Right)
             {
                 // Right align within ContentBox
                 x = box.PaddingBox.Right - textWidth;
             }
             // else Left: x is already ContentBox.Left

             // Color
            SKColor textColor = style?.ForegroundColor ?? SKColors.Black;
            if (isPlaceholder)
            {
                // Prefer ::placeholder computed color; otherwise dim base color
                var phStyle = style?.Placeholder;
                if (phStyle?.ForegroundColor != null)
                {
                    textColor = phStyle.ForegroundColor.Value;
                }
                else
                {
                    textColor = new SKColor(textColor.Red, textColor.Green, textColor.Blue, (byte)(textColor.Alpha * 0.6));
                }

                if (phStyle?.Opacity is double phOpacity)
                {
                    byte alpha = (byte)Math.Clamp(phOpacity * 255.0, 0, 255);
                    textColor = textColor.WithAlpha(alpha);
                }
            }

             return new TextPaintNode
             {
                 Bounds = box.PaddingBox, // Clip to padding box? Or ContentBox? Text usually allowed to overflow into padding? Clipping usually happens at BorderBox.
                 SourceNode = elem,
                 Color = textColor,
                 FontSize = fontSize,
                 Typeface = typeface,
                 TextOrigin = new SKPoint(x, y),
                 FallbackText = type == "password" ? new string('●', value.Length) : value,
                 IsFocused = isFocused,
                 IsHovered = isHovered
             };
        }
        
        private PaintNodeBase BuildSpecialInputNode(Element elem, Layout.BoxModel box, CssComputed style, bool isFocused, bool isHovered)
        {
            if (box == null) return null;
            
            string inputType = elem.GetAttribute("type")?.ToLowerInvariant() ?? "text";
            bool isChecked = elem.HasAttribute("checked");
            bool isDisabled = elem.HasAttribute("disabled");
            
            SKColor borderColor = isDisabled ? SKColors.LightGray : SKColors.Gray;
            SKColor fillColor = isDisabled ? new SKColor(240, 240, 240) : SKColors.White;
            SKColor checkColor = isDisabled ? SKColors.Gray : new SKColor(0, 120, 215); // Windows accent blue
            
            switch (inputType)
            {
                case "checkbox":
                    return new CustomPaintNode
                    {
                        Bounds = box.ContentBox,
                        PaintAction = (canvas, bounds) =>
                        {
                            // Draw checkbox box
                            using var borderPaint = new SKPaint { Color = borderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
                            using var fillPaint = new SKPaint { Color = fillColor, Style = SKPaintStyle.Fill, IsAntialias = true };
                            
                            canvas.DrawRect(bounds, fillPaint);
                            canvas.DrawRect(bounds, borderPaint);
                            
                            // Draw checkmark if checked
                            if (isChecked)
                            {
                                using var checkPaint = new SKPaint { Color = checkColor, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
                                using var path = new SKPath();
                                path.MoveTo(bounds.Left + bounds.Width * 0.2f, bounds.Top + bounds.Height * 0.5f);
                                path.LineTo(bounds.Left + bounds.Width * 0.4f, bounds.Top + bounds.Height * 0.7f);
                                path.LineTo(bounds.Left + bounds.Width * 0.8f, bounds.Top + bounds.Height * 0.3f);
                                canvas.DrawPath(path, checkPaint);
                            }
                        }
                    };
                    
                case "radio":
                    return new CustomPaintNode
                    {
                        Bounds = box.ContentBox,
                        PaintAction = (canvas, bounds) =>
                        {
                            // Draw radio circle
                            using var borderPaint = new SKPaint { Color = borderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
                            using var fillPaint = new SKPaint { Color = fillColor, Style = SKPaintStyle.Fill, IsAntialias = true };
                            
                            float cx = bounds.MidX;
                            float cy = bounds.MidY;
                            float radius = Math.Min(bounds.Width, bounds.Height) / 2 - 1;
                            
                            canvas.DrawCircle(cx, cy, radius, fillPaint);
                            canvas.DrawCircle(cx, cy, radius, borderPaint);
                            
                            // Draw inner dot if checked
                            if (isChecked)
                            {
                                using var checkPaint = new SKPaint { Color = checkColor, Style = SKPaintStyle.Fill, IsAntialias = true };
                                canvas.DrawCircle(cx, cy, radius * 0.5f, checkPaint);
                            }
                        }
                    };
                    
                case "color":
                    string colorValue = elem.GetAttribute("value") ?? "#000000";
                    SKColor displayColor;
                    if (!SKColor.TryParse(colorValue, out displayColor))
                        displayColor = SKColors.Black;
                    
                    return new CustomPaintNode
                    {
                        Bounds = box.ContentBox,
                        PaintAction = (canvas, bounds) =>
                        {
                            using var borderPaint = new SKPaint { Color = SKColors.Gray, Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
                            using var colorPaint = new SKPaint { Color = displayColor, Style = SKPaintStyle.Fill };
                            
                            // Draw color swatch with border
                            var inset = new SKRect(bounds.Left + 2, bounds.Top + 2, bounds.Right - 2, bounds.Bottom - 2);
                            canvas.DrawRect(inset, colorPaint);
                            canvas.DrawRect(bounds, borderPaint);
                        }
                    };
                    
                case "range":
                    string minStr = elem.GetAttribute("min") ?? "0";
                    string maxStr = elem.GetAttribute("max") ?? "100";
                    string valStr = elem.GetAttribute("value") ?? "50";
                    
                    float.TryParse(minStr, out float min);
                    float.TryParse(maxStr, out float max);
                    float.TryParse(valStr, out float val);
                    
                    float percent = (max > min) ? (val - min) / (max - min) : 0.5f;
                    
                    return new CustomPaintNode
                    {
                        Bounds = box.ContentBox,
                        PaintAction = (canvas, bounds) =>
                        {
                            // Track
                            float trackHeight = 4;
                            float trackY = bounds.MidY - trackHeight / 2;
                            var trackRect = new SKRect(bounds.Left, trackY, bounds.Right, trackY + trackHeight);
                            
                            using var trackPaint = new SKPaint { Color = new SKColor(200, 200, 200), Style = SKPaintStyle.Fill };
                            using var filledPaint = new SKPaint { Color = checkColor, Style = SKPaintStyle.Fill };
                            
                            canvas.DrawRoundRect(trackRect, 2, 2, trackPaint);
                            
                            // Filled portion
                            float thumbX = bounds.Left + (bounds.Width - 10) * percent;
                            var filledRect = new SKRect(bounds.Left, trackY, thumbX + 5, trackY + trackHeight);
                            canvas.DrawRoundRect(filledRect, 2, 2, filledPaint);
                            
                            // Thumb
                            float thumbRadius = 7;
                            using var thumbPaint = new SKPaint { Color = checkColor, Style = SKPaintStyle.Fill, IsAntialias = true };
                            using var thumbBorder = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
                            
                        canvas.DrawCircle(thumbX + 5, bounds.MidY, thumbRadius, thumbPaint);
                            canvas.DrawCircle(thumbX + 5, bounds.MidY, thumbRadius, thumbBorder);
                        }
                    };
                    
                case "date":
                case "datetime-local":
                    return new CustomPaintNode
                    {
                        Bounds = box.ContentBox,
                        PaintAction = (canvas, bounds) =>
                        {
                            // Input background
                            using var bgPaint = new SKPaint { Color = fillColor, Style = SKPaintStyle.Fill };
                            canvas.DrawRoundRect(bounds, 3, 3, bgPaint);
                            
                            // Border
                            using var borderPaint = new SKPaint { Color = borderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                            canvas.DrawRoundRect(bounds, 3, 3, borderPaint);
                            
                            // Calendar icon on right
                            float iconSize = Math.Min(bounds.Height - 6, 16);
                            float iconX = bounds.Right - iconSize - 8;
                            float iconY = bounds.MidY;
                            
                            using var iconPaint = new SKPaint { Color = new SKColor(100, 100, 100), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                            // Calendar box
                            canvas.DrawRect(new SKRect(iconX, iconY - iconSize/2 + 2, iconX + iconSize, iconY + iconSize/2), iconPaint);
                            // Calendar top bar
                            canvas.DrawLine(iconX, iconY - iconSize/2 + 5, iconX + iconSize, iconY - iconSize/2 + 5, iconPaint);
                            
                            // Date text placeholder
                            string val = elem.GetAttribute("value") ?? "yyyy-mm-dd";
                            using var textPaint = new SKPaint { Color = new SKColor(60, 60, 60), TextSize = 12, IsAntialias = true };
                            canvas.DrawText(val, bounds.Left + 8, bounds.MidY + 4, textPaint);
                        }
                    };
                    
                case "time":
                    return new CustomPaintNode
                    {
                        Bounds = box.ContentBox,
                        PaintAction = (canvas, bounds) =>
                        {
                            // Input background
                            using var bgPaint = new SKPaint { Color = fillColor, Style = SKPaintStyle.Fill };
                            canvas.DrawRoundRect(bounds, 3, 3, bgPaint);
                            
                            using var borderPaint = new SKPaint { Color = borderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                            canvas.DrawRoundRect(bounds, 3, 3, borderPaint);
                            
                            // Clock icon on right
                            float iconSize = Math.Min(bounds.Height - 6, 14);
                            float iconX = bounds.Right - iconSize - 8;
                            float iconY = bounds.MidY;
                            
                            using var iconPaint = new SKPaint { Color = new SKColor(100, 100, 100), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
                            // Clock circle
                            canvas.DrawCircle(iconX + iconSize/2, iconY, iconSize/2, iconPaint);
                            // Clock hands
                            canvas.DrawLine(iconX + iconSize/2, iconY, iconX + iconSize/2, iconY - iconSize/3, iconPaint);
                            canvas.DrawLine(iconX + iconSize/2, iconY, iconX + iconSize/2 + iconSize/4, iconY, iconPaint);
                            
                            string val = elem.GetAttribute("value") ?? "--:--";
                            using var textPaint = new SKPaint { Color = new SKColor(60, 60, 60), TextSize = 12, IsAntialias = true };
                            canvas.DrawText(val, bounds.Left + 8, bounds.MidY + 4, textPaint);
                        }
                    };
                    
                case "number":
                    return new CustomPaintNode
                    {
                        Bounds = box.ContentBox,
                        PaintAction = (canvas, bounds) =>
                        {
                            using var bgPaint = new SKPaint { Color = fillColor, Style = SKPaintStyle.Fill };
                            canvas.DrawRoundRect(bounds, 3, 3, bgPaint);
                            
                            using var borderPaint = new SKPaint { Color = borderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                            canvas.DrawRoundRect(bounds, 3, 3, borderPaint);
                            
                            // Spinbox arrows on right
                            float arrowWidth = 20;
                            float arrowX = bounds.Right - arrowWidth;
                            
                            using var arrowPaint = new SKPaint { Color = new SKColor(100, 100, 100), Style = SKPaintStyle.Fill, IsAntialias = true };
                            
                            // Up arrow
                            using var upPath = new SKPath();
                            upPath.MoveTo(arrowX + arrowWidth/2, bounds.Top + 4);
                            upPath.LineTo(arrowX + 4, bounds.MidY - 2);
                            upPath.LineTo(arrowX + arrowWidth - 4, bounds.MidY - 2);
                            upPath.Close();
                            canvas.DrawPath(upPath, arrowPaint);
                            
                            // Down arrow
                            using var downPath = new SKPath();
                            downPath.MoveTo(arrowX + arrowWidth/2, bounds.Bottom - 4);
                            downPath.LineTo(arrowX + 4, bounds.MidY + 2);
                            downPath.LineTo(arrowX + arrowWidth - 4, bounds.MidY + 2);
                            downPath.Close();
                            canvas.DrawPath(downPath, arrowPaint);
                            
                            // Separator line
                            using var sepPaint = new SKPaint { Color = borderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                            canvas.DrawLine(arrowX, bounds.Top, arrowX, bounds.Bottom, sepPaint);
                            
                            string val = elem.GetAttribute("value") ?? "";
                            using var textPaint = new SKPaint { Color = new SKColor(60, 60, 60), TextSize = 12, IsAntialias = true };
                            canvas.DrawText(val, bounds.Left + 8, bounds.MidY + 4, textPaint);
                        }
                    };
                    
                case "file":
                    return new CustomPaintNode
                    {
                        Bounds = box.ContentBox,
                        PaintAction = (canvas, bounds) =>
                        {
                            using var bgPaint = new SKPaint { Color = fillColor, Style = SKPaintStyle.Fill };
                            canvas.DrawRoundRect(bounds, 3, 3, bgPaint);
                            
                            using var borderPaint = new SKPaint { Color = borderColor, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                            canvas.DrawRoundRect(bounds, 3, 3, borderPaint);
                            
                            // "Choose File" button on left
                            float btnWidth = 80;
                            var btnRect = new SKRect(bounds.Left, bounds.Top, bounds.Left + btnWidth, bounds.Bottom);
                            
                            using var btnPaint = new SKPaint { Color = new SKColor(228, 228, 228), Style = SKPaintStyle.Fill };
                            canvas.DrawRoundRect(btnRect, 3, 3, btnPaint);
                            
                            using var btnBorder = new SKPaint { Color = new SKColor(180, 180, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                            canvas.DrawRoundRect(btnRect, 3, 3, btnBorder);
                            
                            using var btnText = new SKPaint { Color = new SKColor(40, 40, 40), TextSize = 11, IsAntialias = true };
                            canvas.DrawText("Choose File", bounds.Left + 8, bounds.MidY + 3, btnText);
                            
                            // Filename area
                            using var textPaint = new SKPaint { Color = new SKColor(100, 100, 100), TextSize = 11, IsAntialias = true };
                            canvas.DrawText("No file chosen", bounds.Left + btnWidth + 10, bounds.MidY + 3, textPaint);
                        }
                    };
                    
                default:
                    return null;
            }
        }
        
        private PaintNodeBase BuildVideoPlaceholder(Element elem, Layout.BoxModel box, CssComputed style)
        {
            if (box == null) return null;
            
            return new CustomPaintNode
            {
                Bounds = box.ContentBox,
                PaintAction = (canvas, bounds) =>
                {
                    // Black background
                    using var bgPaint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
                    canvas.DrawRect(bounds, bgPaint);
                    
                    // Play button triangle (centered)
                    float size = Math.Min(bounds.Width, bounds.Height) * 0.3f;
                    float cx = bounds.MidX;
                    float cy = bounds.MidY;
                    
                    using var playPaint = new SKPaint { Color = new SKColor(255, 255, 255, 180), Style = SKPaintStyle.Fill, IsAntialias = true };
                    using var path = new SKPath();
                    path.MoveTo(cx - size * 0.4f, cy - size * 0.5f);
                    path.LineTo(cx + size * 0.6f, cy);
                    path.LineTo(cx - size * 0.4f, cy + size * 0.5f);
                    path.Close();
                    canvas.DrawPath(path, playPaint);
                    
                    // Border
                    using var borderPaint = new SKPaint { Color = new SKColor(80, 80, 80), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                    canvas.DrawRect(bounds, borderPaint);
                }
            };
        }
        
        private PaintNodeBase BuildAudioPlaceholder(Element elem, Layout.BoxModel box, CssComputed style)
        {
            if (box == null) return null;
            
            // Only show if controls attribute is present
            if (!elem.HasAttribute("controls")) return null;
            
            return new CustomPaintNode
            {
                Bounds = box.ContentBox,
                PaintAction = (canvas, bounds) =>
                {
                    // Control bar background
                    using var bgPaint = new SKPaint { Color = new SKColor(240, 240, 240), Style = SKPaintStyle.Fill };
                    canvas.DrawRoundRect(bounds, 4, 4, bgPaint);
                    
                    // Play button
                    float btnSize = bounds.Height * 0.6f;
                    float btnX = bounds.Left + 8;
                    float btnY = bounds.MidY;
                    
                    using var playPaint = new SKPaint { Color = new SKColor(100, 100, 100), Style = SKPaintStyle.Fill, IsAntialias = true };
                    using var path = new SKPath();
                    path.MoveTo(btnX, btnY - btnSize * 0.4f);
                    path.LineTo(btnX + btnSize * 0.7f, btnY);
                    path.LineTo(btnX, btnY + btnSize * 0.4f);
                    path.Close();
                    canvas.DrawPath(path, playPaint);
                    
                    // Progress bar track
                    float trackLeft = btnX + btnSize + 10;
                    float trackRight = bounds.Right - 60;
                    float trackY = bounds.MidY - 2;
                    
                    using var trackPaint = new SKPaint { Color = new SKColor(200, 200, 200), Style = SKPaintStyle.Fill };
                    canvas.DrawRoundRect(new SKRect(trackLeft, trackY, trackRight, trackY + 4), 2, 2, trackPaint);
                    
                    // Time display placeholder
                    using var timePaint = new SKPaint { Color = new SKColor(100, 100, 100), TextSize = 10, IsAntialias = true };
                    canvas.DrawText("0:00", bounds.Right - 50, bounds.MidY + 4, timePaint);
                    
                    // Border
                    using var borderPaint = new SKPaint { Color = new SKColor(200, 200, 200), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                    canvas.DrawRoundRect(bounds, 4, 4, borderPaint);
                }
            };
        }
        
        private PaintNodeBase BuildProgressBar(Element elem, Layout.BoxModel box, CssComputed style)
        {
            if (box == null) return null;
            
            // Parse value and max attributes
            float value = 0;
            float max = 1;
            
            if (float.TryParse(elem.GetAttribute("value"), out float v)) value = v;
            if (float.TryParse(elem.GetAttribute("max"), out float m)) max = m;
            
            float percent = (max > 0) ? Math.Min(value / max, 1f) : 0;
            bool isIndeterminate = !elem.HasAttribute("value");
            
            return new CustomPaintNode
            {
                Bounds = box.ContentBox,
                PaintAction = (canvas, bounds) =>
                {
                    // Background (light gray)
                    using var bgPaint = new SKPaint { Color = new SKColor(230, 230, 230), Style = SKPaintStyle.Fill };
                    canvas.DrawRoundRect(bounds, 3, 3, bgPaint);
                    
                    if (isIndeterminate)
                    {
                        // Striped pattern for indeterminate
                        using var stripePaint = new SKPaint { Color = new SKColor(66, 133, 244), Style = SKPaintStyle.Fill };
                        float stripeWidth = bounds.Width * 0.3f;
                        canvas.DrawRoundRect(new SKRect(bounds.Left, bounds.Top, bounds.Left + stripeWidth, bounds.Bottom), 3, 3, stripePaint);
                    }
                    else
                    {
                        // Filled portion (blue)
                        float fillWidth = bounds.Width * percent;
                        using var fillPaint = new SKPaint { Color = new SKColor(66, 133, 244), Style = SKPaintStyle.Fill };
                        canvas.DrawRoundRect(new SKRect(bounds.Left, bounds.Top, bounds.Left + fillWidth, bounds.Bottom), 3, 3, fillPaint);
                    }
                    
                    // Border
                    using var borderPaint = new SKPaint { Color = new SKColor(200, 200, 200), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                    canvas.DrawRoundRect(bounds, 3, 3, borderPaint);
                }
            };
        }
        
        private PaintNodeBase BuildMeterBar(Element elem, Layout.BoxModel box, CssComputed style)
        {
            if (box == null) return null;
            
            // Parse value, min, max, low, high, optimum
            float value = 0, min = 0, max = 1;
            float? low = null, high = null, optimum = null;
            
            if (float.TryParse(elem.GetAttribute("value"), out float v)) value = v;
            if (float.TryParse(elem.GetAttribute("min"), out float minVal)) min = minVal;
            if (float.TryParse(elem.GetAttribute("max"), out float maxVal)) max = maxVal;
            if (float.TryParse(elem.GetAttribute("low"), out float lowVal)) low = lowVal;
            if (float.TryParse(elem.GetAttribute("high"), out float highVal)) high = highVal;
            if (float.TryParse(elem.GetAttribute("optimum"), out float optVal)) optimum = optVal;
            
            float range = max - min;
            float percent = (range > 0) ? (value - min) / range : 0;
            
            // Determine color based on value relative to low/high/optimum
            SKColor fillColor;
            if (low.HasValue && value < low.Value)
                fillColor = new SKColor(255, 100, 100); // Red - below low
            else if (high.HasValue && value > high.Value)
                fillColor = new SKColor(100, 200, 100); // Green - above high
            else
                fillColor = new SKColor(255, 200, 100); // Yellow/Orange - normal
            
            return new CustomPaintNode
            {
                Bounds = box.ContentBox,
                PaintAction = (canvas, bounds) =>
                {
                    // Background
                    using var bgPaint = new SKPaint { Color = new SKColor(230, 230, 230), Style = SKPaintStyle.Fill };
                    canvas.DrawRoundRect(bounds, 3, 3, bgPaint);
                    
                    // Filled portion
                    float fillWidth = bounds.Width * percent;
                    using var fillPaint = new SKPaint { Color = fillColor, Style = SKPaintStyle.Fill };
                    canvas.DrawRoundRect(new SKRect(bounds.Left, bounds.Top, bounds.Left + fillWidth, bounds.Bottom), 3, 3, fillPaint);
                    
                    // Border
                    using var borderPaint = new SKPaint { Color = new SKColor(180, 180, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                    canvas.DrawRoundRect(bounds, 3, 3, borderPaint);
                }
            };
        }
        
        private PaintNodeBase BuildIframePlaceholder(Element elem, Layout.BoxModel box, CssComputed style)
        {
            if (box == null) return null;
            
            string src = elem.GetAttribute("src") ?? "";
            
            return new CustomPaintNode
            {
                Bounds = box.ContentBox,
                PaintAction = (canvas, bounds) =>
                {
                    // Light gray background
                    using var bgPaint = new SKPaint { Color = new SKColor(248, 248, 248), Style = SKPaintStyle.Fill };
                    canvas.DrawRect(bounds, bgPaint);
                    
                    // Border
                    using var borderPaint = new SKPaint { Color = new SKColor(200, 200, 200), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                    canvas.DrawRect(bounds, borderPaint);
                    
                    // Icon (document/frame icon)
                    float iconSize = Math.Min(bounds.Width, bounds.Height) * 0.15f;
                    float cx = bounds.MidX;
                    float cy = bounds.MidY - 10;
                    
                    using var iconPaint = new SKPaint { Color = new SKColor(150, 150, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
                    var iconRect = new SKRect(cx - iconSize, cy - iconSize * 0.8f, cx + iconSize, cy + iconSize * 0.8f);
                    canvas.DrawRect(iconRect, iconPaint);
                    
                    // Small inner rect for frame effect
                    var innerRect = iconRect;
                    innerRect.Inflate(-iconSize * 0.3f, -iconSize * 0.3f);
                    canvas.DrawRect(innerRect, iconPaint);
                    
                    // Display src URL (truncated)
                    if (!string.IsNullOrEmpty(src))
                    {
                        string displaySrc = src.Length > 40 ? src.Substring(0, 40) + "..." : src;
                        using var textPaint = new SKPaint { Color = new SKColor(100, 100, 100), TextSize = 11, IsAntialias = true };
                        float textWidth = textPaint.MeasureText(displaySrc);
                        float textX = cx - textWidth / 2;
                        float textY = cy + iconSize + 20;
                        canvas.DrawText(displaySrc, textX, textY, textPaint);
                    }
                    else
                    {
                        using var textPaint = new SKPaint { Color = new SKColor(150, 150, 150), TextSize = 11, IsAntialias = true };
                        string label = "<iframe>";
                        float textWidth = textPaint.MeasureText(label);
                        canvas.DrawText(label, cx - textWidth / 2, cy + iconSize + 20, textPaint);
                    }
                }
            };
        }
        
        private List<TextPaintNode> BuildRubyTextNode(Element elem, Layout.BoxModel box, CssComputed style)
        {
            var nodes = new List<TextPaintNode>();
            if (box == null) return nodes;
            
            // Get ruby text data from layout
            // The ruby element stores its layout info in the box's Lines property
            // Format: "RT:rtText|BASE:baseText|RT_SIZE:size|BASE_SIZE:size|RT_HEIGHT:height"
            
            if (box.Lines == null || box.Lines.Count == 0) return nodes;
            
            foreach (var line in box.Lines)
            {
                if (string.IsNullOrEmpty(line.Text)) continue;
                
                // Parse the ruby metadata
                var parts = line.Text.Split('|');
                string rtText = "", baseText = "";
                float rtFontSize = 12f, baseFontSize = 24f, rtHeight = 14f;
                
                foreach (var part in parts)
                {
                    if (part.StartsWith("RT:")) rtText = part.Substring(3);
                    else if (part.StartsWith("BASE:")) baseText = part.Substring(5);
                    else if (part.StartsWith("RT_SIZE:")) float.TryParse(part.Substring(8), out rtFontSize);
                    else if (part.StartsWith("BASE_SIZE:")) float.TryParse(part.Substring(10), out baseFontSize);
                    else if (part.StartsWith("RT_HEIGHT:")) float.TryParse(part.Substring(10), out rtHeight);
                }
                
                var typeface = Layout.TextLayoutHelper.ResolveTypeface(style?.FontFamilyName ?? "sans-serif", baseText + rtText, style?.FontWeight ?? 400, SkiaSharp.SKFontStyleSlant.Upright);
                SKColor color = style?.ForegroundColor ?? SKColors.Black;
                
                float containerWidth = box.ContentBox.Width;
                
                using var rtPaint = new SKPaint { TextSize = rtFontSize, Typeface = typeface };
                using var basePaint = new SKPaint { TextSize = baseFontSize, Typeface = typeface };
                
                float rtWidth = rtPaint.MeasureText(rtText);
                float baseWidth = basePaint.MeasureText(baseText);
                
                // RT text node (above)
                if (!string.IsNullOrEmpty(rtText))
                {
                    float rtX = box.ContentBox.Left + (containerWidth - rtWidth) / 2;
                    float rtY = box.ContentBox.Top + rtFontSize; // Baseline
                    
                    nodes.Add(new TextPaintNode
                    {
                        Bounds = new SKRect(rtX, box.ContentBox.Top, rtX + rtWidth, box.ContentBox.Top + rtHeight),
                        SourceNode = elem,
                        Color = color,
                        FontSize = rtFontSize,
                        Typeface = typeface,
                        TextOrigin = new SKPoint(rtX, rtY),
                        FallbackText = rtText
                    });
                }
                
                // Base text node (below RT)
                if (!string.IsNullOrEmpty(baseText))
                {
                    float baseX = box.ContentBox.Left + (containerWidth - baseWidth) / 2;
                    float baseY = box.ContentBox.Top + rtHeight + baseFontSize; // Below RT
                    
                    nodes.Add(new TextPaintNode
                    {
                        Bounds = new SKRect(baseX, box.ContentBox.Top + rtHeight, baseX + baseWidth, box.ContentBox.Bottom),
                        SourceNode = elem,
                        Color = color,
                        FontSize = baseFontSize,
                        Typeface = typeface,
                        TextOrigin = new SKPoint(baseX, baseY),
                        FallbackText = baseText
                    });
                }
            }
            
            return nodes;
        }
        
        private static SKPoint[] ExtractBorderRadius(Layout.BoxModel box, CssComputed style)
        {
            if (style == null) return null;
            
            var br = style.BorderRadius;
            if (br.TopLeft.Value == 0 && br.TopRight.Value == 0 && br.BottomRight.Value == 0 && br.BottomLeft.Value == 0)
                return null;
            
            float w = box.BorderBox.Width;
            float h = box.BorderBox.Height;

            SKPoint Resolve(CssLength len)
            {
                if (!len.IsPercent) return new SKPoint(len.Value, len.Value);
                // CSS spec: % is relative to Width for horizontal, Height for vertical
                return new SKPoint(w * len.Value / 100f, h * len.Value / 100f);
            }

            return new SKPoint[]
            {
                Resolve(br.TopLeft),
                Resolve(br.TopRight),
                Resolve(br.BottomRight),
                Resolve(br.BottomLeft)
            };
        }
        
        private static bool IsImageElement(Element elem)
        {
            if (elem == null)
            {
                return false;
            }

            string tag = elem.TagName?.ToUpperInvariant();
            if (tag == "IMG" || tag == "SVG")
            {
                return true;
            }

            if (tag == "OBJECT")
            {
                return !ReplacedElementSizing.ShouldUseObjectFallbackContent(elem);
            }

            return false;
        }

        private static bool IsAtomicInlinePaintElement(Element elem)
        {
            if (elem == null)
            {
                return false;
            }

            if (IsImageElement(elem))
            {
                return true;
            }

            string tag = elem.TagName?.ToUpperInvariant() ?? string.Empty;
            return tag is "INPUT" or "TEXTAREA" or "SELECT" or "BUTTON" or "VIDEO" or "AUDIO" or "IFRAME" or "EMBED" or "CANVAS";
        }

        private static SKColor ResolveBorderSideColor(CssComputed style, string key, SKColor fallback)
        {
            if (style?.Map != null &&
                style.Map.TryGetValue(key, out var raw) &&
                !string.IsNullOrWhiteSpace(raw))
            {
                var parsed = CssLoader.TryColor(raw);
                if (parsed.HasValue)
                {
                    return parsed.Value;
                }
            }

            return fallback;
        }

        private static SKColor[] ResolveBorderColors(CssComputed style, SKColor fallback)
        {
            SKColor top = ResolveBorderSideColor(style, "border-top-color", fallback);
            SKColor right = ResolveBorderSideColor(style, "border-right-color", fallback);
            SKColor bottom = ResolveBorderSideColor(style, "border-bottom-color", fallback);
            SKColor left = ResolveBorderSideColor(style, "border-left-color", fallback);

            if (style?.Map != null &&
                style.Map.TryGetValue("border-color", out var shorthand) &&
                !string.IsNullOrWhiteSpace(shorthand))
            {
                var tokens = shorthand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                SKColor? c1 = tokens.Length >= 1 ? CssLoader.TryColor(tokens[0]) : null;
                SKColor? c2 = tokens.Length >= 2 ? CssLoader.TryColor(tokens[1]) : null;
                SKColor? c3 = tokens.Length >= 3 ? CssLoader.TryColor(tokens[2]) : null;
                SKColor? c4 = tokens.Length >= 4 ? CssLoader.TryColor(tokens[3]) : null;

                if (tokens.Length == 1 && c1.HasValue)
                {
                    top = right = bottom = left = c1.Value;
                }
                else if (tokens.Length == 2 && c1.HasValue && c2.HasValue)
                {
                    top = bottom = c1.Value;
                    right = left = c2.Value;
                }
                else if (tokens.Length == 3 && c1.HasValue && c2.HasValue && c3.HasValue)
                {
                    top = c1.Value;
                    right = left = c2.Value;
                    bottom = c3.Value;
                }
                else if (tokens.Length >= 4 && c1.HasValue && c2.HasValue && c3.HasValue && c4.HasValue)
                {
                    top = c1.Value;
                    right = c2.Value;
                    bottom = c3.Value;
                    left = c4.Value;
                }
            }

            // Explicit side declarations must override shorthand.
            top = ResolveBorderSideColor(style, "border-top-color", top);
            right = ResolveBorderSideColor(style, "border-right-color", right);
            bottom = ResolveBorderSideColor(style, "border-bottom-color", bottom);
            left = ResolveBorderSideColor(style, "border-left-color", left);

            return new[] { top, right, bottom, left };
        }

        private static (SKShaderTileMode x, SKShaderTileMode y) ResolveBackgroundTileModes(string repeat)
        {
            var normalized = repeat?.Trim().ToLowerInvariant();
            return normalized switch
            {
                "no-repeat" => (SKShaderTileMode.Decal, SKShaderTileMode.Decal),
                "repeat-x" => (SKShaderTileMode.Repeat, SKShaderTileMode.Decal),
                "repeat-y" => (SKShaderTileMode.Decal, SKShaderTileMode.Repeat),
                _ => (SKShaderTileMode.Repeat, SKShaderTileMode.Repeat)
            };
        }

        private static SKRect ResolveBackgroundPaintBounds(Layout.BoxModel box, CssComputed style)
        {
            var clip = style?.BackgroundClip?.Trim().ToLowerInvariant();
            return clip switch
            {
                "padding-box" => box.PaddingBox,
                "content-box" => box.ContentBox,
                _ => box.BorderBox
            };
        }

        private static SKPoint ResolveBackgroundOriginPoint(Layout.BoxModel box, CssComputed style)
        {
            var origin = style?.BackgroundOrigin?.Trim().ToLowerInvariant();
            return origin switch
            {
                "border-box" => new SKPoint(box.BorderBox.Left, box.BorderBox.Top),
                "content-box" => new SKPoint(box.ContentBox.Left, box.ContentBox.Top),
                _ => new SKPoint(box.PaddingBox.Left, box.PaddingBox.Top)
            };
        }

        private static SKPoint ResolveBackgroundPosition(string value, SKRect paintBounds, SKBitmap bitmap, SKPoint origin)
        {
            if (bitmap == null)
            {
                return SKPoint.Empty;
            }

            var raw = value?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return SKPoint.Empty;
            }

            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return SKPoint.Empty;
            }

            float x = 0;
            float y = 0;
            bool hasX = TryResolveBackgroundPositionComponent(parts[0], paintBounds.Width, bitmap.Width, true, out x);
            bool hasY = false;

            if (parts.Length > 1)
            {
                hasY = TryResolveBackgroundPositionComponent(parts[1], paintBounds.Height, bitmap.Height, false, out y);
            }

            if (!hasY && parts.Length == 1)
            {
                hasY = TryResolveBackgroundPositionComponent(parts[0], paintBounds.Height, bitmap.Height, false, out y);
            }

            if (!hasX && !hasY)
            {
                return SKPoint.Empty;
            }

            return new SKPoint((origin.X - paintBounds.Left) + x, (origin.Y - paintBounds.Top) + y);
        }

        private static bool TryResolveBackgroundPositionComponent(string token, float containerSize, float imageSize, bool isHorizontal, out float value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            token = token.Trim().ToLowerInvariant();

            if (token.EndsWith("px", StringComparison.Ordinal))
            {
                return float.TryParse(token.AsSpan(0, token.Length - 2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
            }

            if (token.EndsWith("%", StringComparison.Ordinal) &&
                float.TryParse(token.AsSpan(0, token.Length - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                value = (containerSize - imageSize) * (pct / 100f);
                return true;
            }

            switch (token)
            {
                case "left":
                    if (isHorizontal) { value = 0; return true; }
                    break;
                case "right":
                    if (isHorizontal) { value = containerSize - imageSize; return true; }
                    break;
                case "top":
                    if (!isHorizontal) { value = 0; return true; }
                    break;
                case "bottom":
                    if (!isHorizontal) { value = containerSize - imageSize; return true; }
                    break;
                case "center":
                    value = (containerSize - imageSize) / 2f;
                    return true;
            }

            return false;
        }

        private string NormalizeResourceUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_baseUri))
            {
                try
                {
                    if (url.StartsWith("//", StringComparison.Ordinal))
                    {
                        var scheme = new Uri(_baseUri).Scheme;
                        return scheme + ":" + url;
                    }

                    if (url.StartsWith("/", StringComparison.Ordinal))
                    {
                        var uri = new Uri(_baseUri);
                        return $"{uri.Scheme}://{uri.Host}{url}";
                    }

                    var resolved = new Uri(new Uri(_baseUri), url);
                    return resolved.ToString();
                }
                catch
                {
                    return url;
                }
            }

            return url;
        }
        
        private PaintNodeBase BuildListMarkerNode(Node node, Layout.BoxModel box, CssComputed style, bool isFocused, bool isHovered)
        {
            // Only for Elements
            if (!(node is Element elem)) return null;

            string tag = elem.TagName;
            string id = elem.GetAttribute("id") ?? "";
            string type = style?.ListStyleType ?? "disc";

            global::FenBrowser.Core.EngineLogCompat.Info($"[MARKER-BUILD] <{tag}#{id}> Type={type} Display={style?.Display}", global::FenBrowser.Core.Logging.LogCategory.Rendering);

            string listStyleType = ResolveEffectiveListStyleType(elem, style) ?? "disc"; // Default to disc
            string listStylePosition = style?.ListStylePosition ?? "outside";

            if (string.Equals(listStyleType, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            
            // Check for explicit list-style-image (URL)
            string listStyleImage = style?.ListStyleImage;
            if (!string.IsNullOrEmpty(listStyleImage) && listStyleImage != "none")
            {
                string url = listStyleImage.Trim();
                if (url.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
                {
                    url = url.Substring(4, url.Length - 5).Trim('\'', '\"', ' ');
                }

                // Resolve relative URL against baseUri if present
                if (!string.IsNullOrEmpty(url) &&
                    !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(_baseUri))
                {
                    url = new Uri(new Uri(_baseUri), url).ToString();
                }

                var bitmap = ImageLoader.GetImage(url);
                float markerSize = (float)(style?.FontSize ?? 16.0);
                float markerX, markerY;

                float markerBaselineOffset = markerSize * 0.85f;
                if (box.Lines != null && box.Lines.Count > 0)
                    markerBaselineOffset = box.Lines[0].Baseline;

                markerY = box.ContentBox.Top + markerBaselineOffset;

                if (listStylePosition == "inside")
                    markerX = box.ContentBox.Left + 4;
                else
                    markerX = Math.Max(4, box.ContentBox.Left - markerSize - 14);

                if (bitmap != null)
                {
                    return new ImagePaintNode
                    {
                        SourceNode = node,
                        Bounds = new SKRect(markerX, markerY - markerSize, markerX + markerSize, markerY),
                        Bitmap = bitmap,
                        ObjectFit = "contain",
                        IsFocused = isFocused,
                        IsHovered = isHovered
                    };
                }

                return new CustomPaintNode
                {
                    SourceNode = node,
                    Bounds = new SKRect(markerX, markerY - markerSize, markerX + markerSize, markerY),
                    IsFocused = isFocused,
                    IsHovered = isHovered,
                    PaintAction = (canvas, bounds) =>
                    {
                        using var paint = new SKPaint { Color = style?.ForegroundColor ?? SKColors.Black, IsAntialias = true, TextSize = markerSize * 0.6f };
                        canvas.DrawText("•", bounds.Left + 2, bounds.Bottom - 2, paint);
                    }
                };
            }
            
            // Determine marker text/shape
            string markerText = "•"; // Default disc
            
            // Basic types
            if (listStyleType == "disc") markerText = "•";       // U+2022
            else if (listStyleType == "circle") markerText = "◦"; // U+25E6
            else if (listStyleType == "square") markerText = "■"; // U+25A0
            else if (listStyleType == "decimal")
            {
                // Find index in parent
                int index = 1;
                /* [PERF-WARNING] This is O(N) per item, can be slow for large lists.
                   Ideally layout engine should calculate this. */
                if (elem.Parent != null)
                {
                    int count = 0;
                    foreach (var child in elem.Parent.Children)
                    {
                        if (child is Element childEl && childEl.TagName == "LI") 
                        {
                            count++;
                            if (child == elem) 
                            { 
                                index = count; 
                                break; 
                            }
                        }
                    }
                }
                
                // Handle 'start' attribute on OL
                if (elem.Parent is Element parentEl && parentEl.TagName == "OL")
                {
                    int startVal = 1;
                    if (int.TryParse(parentEl.GetAttribute("start"), out int sv))
                    {
                        startVal = sv;
                    }
                    
                    // Handle 'reversed' attribute on OL
                    if (parentEl.HasAttribute("reversed"))
                    {
                        // Count total LI items for reversed calculation
                        int totalItems = 0;
                        foreach (var c in parentEl.Children)
                        {
                            if (c is Element ce && ce.TagName == "LI") totalItems++;
                        }
                        // Reversed: first item = start, last item = start - (count-1)
                        index = startVal - (index - 1);
                    }
                    else
                    {
                        index = startVal + (index - 1);
                    }
                }
                
                // Handle 'value' attribute on LI (overrides everything)
                if (int.TryParse(elem.GetAttribute("value"), out int valueVal))
                {
                    index = valueVal;
                }
                
                markerText = $"{index}.";
            }
            else if (listStyleType == "lower-alpha" || listStyleType == "upper-alpha")
            {
                int index = CalculateListIndex(elem);
                markerText = ToAlpha(index, listStyleType == "upper-alpha") + ".";
            }
            else if (listStyleType == "lower-roman" || listStyleType == "upper-roman")
            {
                int index = CalculateListIndex(elem);
                markerText = ToRoman(index, listStyleType == "upper-roman") + ".";
            }
            else if (listStyleType == "disclosure-closed")
            {
                markerText = "▶"; // Right-pointing triangle (closed)
            }
            else if (listStyleType == "disclosure-open")
            {
                markerText = "▼"; // Down-pointing triangle (open)
            }
            
            // Calculate Position
            float fontSize = (float)(style?.FontSize ?? 16.0);
            
            // Use same typeface as element
            string fontFamily = style?.FontFamilyName;
            int weight = style?.FontWeight ?? 400;
            SKFontStyleSlant slant = (style?.FontStyle == SKFontStyleSlant.Italic) ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
            var typeface = TextLayoutHelper.ResolveTypeface(fontFamily, markerText, weight, slant);
            
            using var paint = new SKPaint { Typeface = typeface, TextSize = fontSize, IsAntialias = true };
            float markerWidth = paint.MeasureText(markerText);
            
            float x, y;
            
            // Vertical alignment: align baseline with first line of text
            // Roughly: Top + (FontSize * 0.9) ? Or use Box Baseline if available?
            // Box.Lines[0].Baseline is best if available.
            float baselineOffset = fontSize;
            if (box.Lines != null && box.Lines.Count > 0)
            {
                baselineOffset = box.Lines[0].Baseline;
            }
            else
            {
                 // Fallback
                 baselineOffset = fontSize * 0.85f;
            }
            
            y = box.ContentBox.Top + baselineOffset;
            
            if (listStylePosition == "inside")
            {
                // Inside: Render as inline text at start of content
                x = box.ContentBox.Left + 4; // Shift inside slightly
            }
            else
            {
                // Outside: Render to the left of the border box
                float anchorRight = box.ContentBox.Left - 6;
                var childNodes = elem.ChildNodes;
                if ((childNodes == null || childNodes.Length == 0) && elem.Children != null)
                {
                    childNodes = elem.Children;
                }

                if (childNodes != null)
                {
                    for (int i = 0; i < childNodes.Length; i++)
                    {
                        if (_boxes != null && _boxes.TryGetValue(childNodes[i], out var childBox))
                        {
                            anchorRight = Math.Min(anchorRight, childBox.ContentBox.Left - 6);
                        }
                    }
                }

                float desiredX = anchorRight - markerWidth;
                x = Math.Max(4, desiredX); // Safety: at least 4px from left edge
            }
            
            return new TextPaintNode
            {
                Bounds = new SKRect(x, y - fontSize, x + markerWidth, y), // Approximate
                SourceNode = node,
                Color = style?.ForegroundColor ?? SKColors.Black,
                FontSize = fontSize,
                Typeface = typeface,
                TextOrigin = new SKPoint(x, y),
                FallbackText = markerText,
                IsFocused = isFocused,
                IsHovered = isHovered
            };
        }

        private string ResolveEffectiveListStyleType(Element element, CssComputed style)
        {
            if (style != null)
            {
                if (!string.IsNullOrWhiteSpace(style.ListStyleType))
                {
                    return style.ListStyleType.Trim().ToLowerInvariant();
                }

                string localType = ExtractListStyleTypeFromMap(style);
                if (!string.IsNullOrWhiteSpace(localType))
                {
                    return localType;
                }
            }

            if (element?.Parent is not Element parentElement)
            {
                return null;
            }

            if (_styles != null && _styles.TryGetValue(parentElement, out var parentStyle))
            {
                if (!string.IsNullOrWhiteSpace(parentStyle?.ListStyleType))
                {
                    return parentStyle.ListStyleType.Trim().ToLowerInvariant();
                }

                string parentType = ExtractListStyleTypeFromMap(parentStyle);
                if (!string.IsNullOrWhiteSpace(parentType))
                {
                    return parentType;
                }
            }

            return null;
        }

        private static string ExtractListStyleTypeFromMap(CssComputed style)
        {
            if (style?.Map == null)
            {
                return null;
            }

            if (style.Map.TryGetValue("list-style-type", out var explicitType) &&
                !string.IsNullOrWhiteSpace(explicitType))
            {
                return explicitType.Trim().ToLowerInvariant();
            }

            if (style.Map.TryGetValue("list-style", out var shorthand) &&
                !string.IsNullOrWhiteSpace(shorthand))
            {
                string[] parts = shorthand.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string rawPart in parts)
                {
                    string part = rawPart.Trim().ToLowerInvariant();
                    if (part == "inside" || part == "outside" || part.StartsWith("url(", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return part;
                }
            }

            return null;
        }

        private static bool ShouldHide(Node node, CssComputed style)
        {
            if (node == null) return true;

            // Use string comparison for Display and Visibility
            if (style != null && string.Equals(style.Display, "none", StringComparison.OrdinalIgnoreCase)) return true;
            // Visibility: hidden check removed (handled in BuildRecursive to allow children)

            // HTML hidden attribute means the element is not relevant and should not be rendered.
            if (node is Element elem)
            {
                if (elem.HasAttribute("hidden")) return true;
                if (ShouldHideCollapsedVectorPanel(elem)) return true;
            }

            string tag = node.NodeName?.ToUpperInvariant();
            return tag == "HEAD" || tag == "SCRIPT" || tag == "STYLE" || tag == "META" || tag == "LINK" || tag == "TITLE" || tag == "NOSCRIPT" || tag == "IFRAME";
        }

        private static bool ShouldHideCollapsedVectorPanel(Element element)
        {
            if (element == null) return false;

            bool isVectorDropdownContent = HasClassToken(element, "vector-dropdown-content");
            bool isVectorMenuContent = HasClassToken(element, "vector-menu-content");
            if (!isVectorDropdownContent && !isVectorMenuContent)
            {
                return false;
            }

            string toggleClass = isVectorDropdownContent ? "vector-dropdown-checkbox" : "vector-menu-checkbox";

            if (element.ParentNode == null || element.ParentNode.ChildNodes == null)
            {
                return false;
            }

            var siblings = element.ParentNode.ChildNodes;
            int index = -1;
            for (int i = 0; i < siblings.Length; i++)
            {
                if (ReferenceEquals(siblings[i], element))
                {
                    index = i;
                    break;
                }
            }

            if (index <= 0)
            {
                return false;
            }

            for (int i = index - 1; i >= 0; i--)
            {
                if (siblings[i] is not Element siblingElement)
                {
                    continue;
                }

                if (!string.Equals(siblingElement.TagName, "input", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!HasClassToken(siblingElement, toggleClass))
                {
                    continue;
                }

                return !siblingElement.HasAttribute("checked");
            }

            return false;
        }

        private static bool HasClassToken(Element element, string token)
        {
            if (element == null || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            string classAttr = element.GetAttribute("class");
            if (string.IsNullOrWhiteSpace(classAttr))
            {
                return false;
            }

            var parts = classAttr.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Any(p => string.Equals(p, token, StringComparison.OrdinalIgnoreCase));
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
                
            // mask-image creates stacking context
            if (!string.IsNullOrEmpty(style.MaskImage) && style.MaskImage != "none")
                return true;
                
            // filter creates stacking context
            // Note: filter is not yet fully implemented for rendering, but it MUST create a stacking context
            // to support overlays like the "AI Mode" overlay or backdrop-filters.
            if (!string.IsNullOrEmpty(style.Filter) && style.Filter != "none")
                return true;

            // Overflow != visible creates stacking context (for our simplified renderer)
            // Overflow != visible creates stacking context (for our simplified renderer)
            // UPDATE: Only scroll/auto needs context for scroll management. 
            // Hidden should NOT create context to allow proper z-index interleaving.
            if (style.OverflowX == "scroll" || style.OverflowX == "auto" || 
                style.OverflowY == "scroll" || style.OverflowY == "auto")
                return true;
            
            // Note: overflow: hidden does NOT create a stacking context by itself.
            // It will be handled via ClipPaintNode in the normal flow.

            return false;
        }

        /// <summary>
        /// Parses CSS transform-origin into x,y coordinates relative to the element's border box.
        /// Default is "50% 50%" (center of border box).
        /// </summary>
        private static void ParseTransformOrigin(string value, SKRect bounds, out float ox, out float oy)
        {
            ox = bounds.MidX;
            oy = bounds.MidY;

            if (string.IsNullOrWhiteSpace(value)) return;

            var parts = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 1)
            {
                ox = ParseOriginValue(parts[0], bounds.Left, bounds.Width);
            }
            if (parts.Length >= 2)
            {
                oy = ParseOriginValue(parts[1], bounds.Top, bounds.Height);
            }
        }

        /// <summary>
        /// Parses a single transform-origin axis value (keyword, percentage, or length).
        /// </summary>
        private static float ParseOriginValue(string val, float start, float size)
        {
            val = val.Trim().ToLowerInvariant();
            if (val == "left" || val == "top") return start;
            if (val == "right" || val == "bottom") return start + size;
            if (val == "center") return start + size / 2;
            if (val.EndsWith("%") && float.TryParse(val.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pct))
                return start + size * pct / 100f;
            if (val.EndsWith("px") && float.TryParse(val.Replace("px", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px))
                return start + px;
            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float raw))
                return start + raw;
            return start + size / 2; // fallback to center
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
            
            // Masking support
            public string MaskImage { get; set; }
            public SKRect MaskBounds { get; set; }
            
            // Overflow support
            public SKRect? ClipBounds { get; set; }
            public SKPoint? ScrollOffset { get; set; }
            public float Opacity { get; set; } = 1.0f;

            // CSS Transform support
            public SKMatrix? TransformMatrix { get; set; }

            // CSS filter / backdrop-filter (stored as raw strings; parsed by renderer)
            public string Filter { get; set; }
            public string BackdropFilter { get; set; }
            
            // Categorized by paint order (CSS spec)
            private readonly List<BuilderStackingContext> _negativeZContexts = new List<BuilderStackingContext>();
            private readonly List<PaintNodeBase> _blockNodes = new List<PaintNodeBase>();
            private readonly List<PaintNodeBase> _floatNodes = new List<PaintNodeBase>();
            private readonly List<PaintNodeBase> _inlineNodes = new List<PaintNodeBase>();

            // Step 6: Stacking contexts with z-index: 0 AND Positioned descendants with z-index: auto
            // These must be painted in TREE ORDER (DOM order).
            private readonly List<object> _step6Items = new List<object>(); 
            
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
                    _step6Items.Add(child); // Step 6
                else
                    _positiveZContexts.Add(child);
            }
            
            public void AddBlockNodes(List<PaintNodeBase> nodes) => _blockNodes.AddRange(nodes);
            public void AddFloatNodes(List<PaintNodeBase> nodes) => _floatNodes.AddRange(nodes);
            public void AddInlineNodes(List<PaintNodeBase> nodes) => _inlineNodes.AddRange(nodes);
            
            public void AddPositionedNodes(List<PaintNodeBase> nodes, int zIndex)
            {
                // Positioned with z-index: auto (which comes here as we verify no-context in caller)
                _step6Items.Add(nodes); // Step 6
            }
            
            public List<PaintNodeBase> Flatten()
            {
                try { System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack(); }
                catch (InsufficientExecutionStackException) { return new List<PaintNodeBase>(); }

                var result = new List<PaintNodeBase>();
                
                // 1. Own paint nodes (background, border)
                if (PaintNodes != null)
                    result.AddRange(PaintNodes);
                
                // Content to be clipped/scrolled
                var contentNodes = new List<PaintNodeBase>();
                
                // 2. Negative z-index stacking contexts (sorted)
                foreach (var ctx in _negativeZContexts.OrderBy(c => c.ZIndex))
                {
                    contentNodes.AddRange(ctx.Flatten());
                }
                
                // 3. In-flow Block Level descendants
                contentNodes.AddRange(_blockNodes);

                // 4. In-flow Float descendants
                contentNodes.AddRange(_floatNodes);

                // 5. In-flow Inline Level descendants
                contentNodes.AddRange(_inlineNodes);
                
                // 6. Step 6: Zero z-index contexts AND positioned z-auto descendants (In Tree Order)
                foreach (var item in _step6Items)
                {
                    if (item is BuilderStackingContext ctx)
                    {
                        contentNodes.AddRange(ctx.Flatten());
                    }
                    else if (item is List<PaintNodeBase> nodes)
                    {
                        contentNodes.AddRange(nodes);
                    }
                }
                
                // 7. Positive z-index stacking contexts (sorted)
                foreach (var ctx in _positiveZContexts.OrderBy(c => c.ZIndex))
                {
                    contentNodes.AddRange(ctx.Flatten());
                }
                
                // Apply Scroll
                if (ScrollOffset.HasValue && (ScrollOffset.Value.X != 0 || ScrollOffset.Value.Y != 0))
                {
                    var scrollBounds = LayoutHelper.NormalizeRect(ClipBounds ?? new SKRect(0, 0, 10000, 10000));
                    var scrollNode = new ScrollPaintNode
                    {
                        Bounds = scrollBounds,
                        ScrollX = ScrollOffset.Value.X,
                        ScrollY = ScrollOffset.Value.Y,
                        Children = contentNodes
                    };
                    contentNodes = new List<PaintNodeBase> { scrollNode };
                }
                
                // Apply Clip
                if (ClipBounds.HasValue)
                {
                    var normalizedClipBounds = LayoutHelper.NormalizeRect(ClipBounds.Value);
                    var clipNode = new ClipPaintNode
                    {
                        Bounds = normalizedClipBounds,
                        ClipRect = normalizedClipBounds,
                        Children = contentNodes
                    };
                    contentNodes = new List<PaintNodeBase> { clipNode };
                }
                
                result.AddRange(contentNodes);
                
                // Apply Mask (if any)
                if (!string.IsNullOrEmpty(MaskImage) && MaskImage != "none")
                {
                    string url = MaskImage;
                    if (url.StartsWith("url("))
                    {
                        int start = url.IndexOf("(") + 1;
                        int end = url.LastIndexOf(")");
                        if (end > start)
                        {
                            url = url.Substring(start, end - start).Trim('"', '\'', ' ');
                        }
                    }
                    
                    var bitmap = ImageLoader.GetImage(url);
                    if (bitmap != null)
                    {
                         var maskNode = new MaskPaintNode
                         {
                             Bounds = MaskBounds,
                             MaskBitmap = bitmap,
                             MaskSize = "cover",
                             Children = result, 
                             SourceNode = SourceNode
                         };
                         result = new List<PaintNodeBase> { maskNode };
                    }
                }
                
                // Apply Opacity (Stacking Context Atomic Paint)
                if (Opacity < 1.0f)
                {
                    var groupBounds = ComputeAggregateBounds(result, MaskBounds);
                    var opacityNode = new OpacityGroupPaintNode
                    {
                        Bounds = groupBounds,
                        Opacity = Opacity,
                        Children = result,
                        SourceNode = SourceNode
                    };
                    result = new List<PaintNodeBase> { opacityNode };
                }

                // Apply CSS Transform (outermost wrapper — transform affects entire stacking context)
                if (TransformMatrix.HasValue || !string.IsNullOrEmpty(Filter) || !string.IsNullOrEmpty(BackdropFilter))
                {
                    var groupBounds = ComputeAggregateBounds(result, MaskBounds);
                    var scNode = new StackingContextPaintNode
                    {
                        Bounds = groupBounds,
                        ZIndex = ZIndex,
                        Transform = TransformMatrix ?? SKMatrix.Identity,
                        Children = result,
                        SourceNode = SourceNode,
                        Filter = Filter,
                        BackdropFilter = BackdropFilter
                    };
                    return new List<PaintNodeBase> { scNode };
                }

                return result;
            }

            private static SKRect ComputeAggregateBounds(IReadOnlyList<PaintNodeBase> nodes, SKRect? fallback)
            {
                SKRect aggregate = SKRect.Empty;
                bool hasBounds = false;

                if (nodes != null)
                {
                    foreach (var node in nodes)
                    {
                        if (node == null)
                        {
                            continue;
                        }

                        var bounds = node.Bounds;
                        bounds = LayoutHelper.NormalizeRect(bounds);
                        if (bounds.Width <= 0 || bounds.Height <= 0)
                        {
                            continue;
                        }

                        if (!hasBounds)
                        {
                            aggregate = bounds;
                            hasBounds = true;
                        }
                        else
                        {
                            aggregate = UnionRects(aggregate, bounds);
                        }
                    }
                }

                if (!hasBounds && fallback.HasValue)
                {
                    aggregate = LayoutHelper.NormalizeRect(fallback.Value);
                    hasBounds = aggregate.Width > 0 && aggregate.Height > 0;
                }

                return hasBounds ? aggregate : SKRect.Empty;
            }

            private static SKRect UnionRects(SKRect left, SKRect right)
            {
                return new SKRect(
                    Math.Min(left.Left, right.Left),
                    Math.Min(left.Top, right.Top),
                    Math.Max(left.Right, right.Right),
                    Math.Max(left.Bottom, right.Bottom));
            }
        }

        private int CalculateListIndex(Element elem)
        {
            int index = 1;
            if (elem.Parent != null)
            {
                int count = 0;
                foreach (var child in elem.Parent.Children)
                {
                    if (child is Element childEl && childEl.TagName == "LI") 
                    {
                        count++;
                        if (child == elem) 
                        { 
                            index = count; 
                            break; 
                        }
                    }
                }
            }
            
            // Handle 'start' attribute on OL
            if (elem.Parent is Element parentEl && parentEl.TagName == "OL")
            {
                if (int.TryParse(parentEl.GetAttribute("start"), out int startVal))
                {
                    index = startVal + (index - 1);
                }
            }
            
            // Handle 'value' attribute on LI
            if (int.TryParse(elem.GetAttribute("value"), out int valueVal))
            {
                index = valueVal;
            }
            return index;
        }

        private static string ToAlpha(int index, bool upper)
        {
            if (index < 1) return index.ToString();
            string s = "";
            index--; // 0-based
            while (index >= 0)
            {
                s = (char)('a' + (index % 26)) + s;
                index /= 26;
                index--;
            }
            return upper ? s.ToUpperInvariant() : s;
        }

        private static string ToRoman(int number, bool upper)
        {
            if (number < 1 || number > 3999) return number.ToString();
            string[] thousands = { "", "m", "mm", "mmm" };
            string[] hundreds = { "", "c", "cc", "ccc", "cd", "d", "dc", "dcc", "dccc", "cm" };
            string[] tens = { "", "x", "xx", "xxx", "xl", "l", "lx", "lxx", "lxxx", "xc" };
            string[] ones = { "", "i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix" };
            
            string s = thousands[number / 1000] + hundreds[(number % 1000) / 100] + tens[(number % 100) / 10] + ones[number % 10];
            return upper ? s.ToUpperInvariant() : s;
        }
    }
}


