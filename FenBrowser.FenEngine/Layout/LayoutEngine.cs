using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core;
using FenBrowser.Core.Memory;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Pure layout computation engine used by the render-frame pipeline.
    /// Goal: no painting, no backend rasterization, no JS execution.
    /// </summary>
    public sealed class LayoutEngine
    {
        /// <summary>
        /// Per-call layout diagnostic logging is OFF by default. Opt in with
        /// FEN_LAYOUT_DEBUG_LOG=1 when investigating box-tree or positioning issues.
        /// </summary>
        internal static bool LayoutDebugLogEnabled =
            string.Equals(System.Environment.GetEnvironmentVariable("FEN_LAYOUT_DEBUG_LOG"), "1",
                System.StringComparison.Ordinal);

        private readonly LayoutContext _context;
        private readonly FenBrowser.FenEngine.Layout.Tree.LayoutBoxStore _boxStore = new FenBrowser.FenEngine.Layout.Tree.LayoutBoxStore();

        /// <summary>
        /// Creates a new layout engine from style dictionary and viewport.
        /// Uses the box-tree pipeline (BoxTreeBuilder → FormattingContext).
        /// </summary>
        public LayoutEngine(
            IReadOnlyDictionary<Node, CssComputed> styles,
            float viewportWidth,
            float viewportHeight,
            string baseUri = null)
        {
            _context = new LayoutContext(styles, viewportWidth, viewportHeight);
        }

        /// <summary>
        /// Creates a default layout engine (for simple use cases).
        /// </summary>
        public LayoutEngine()
        {
            _context = new LayoutContext(new Dictionary<Node, CssComputed>(), 1920, 1080);
        }
        
        /// <summary>
        /// The layout context containing computed boxes and state.
        /// </summary>
        public LayoutContext Context => _context;

        /// <summary>
        /// Points the next pass at a newer style snapshot of the same document. The last
        /// result is kept: whatever invalidated geometry marked the tree dirty, and a clean
        /// tree reuses it instead of a full pass.
        /// </summary>
        internal void UseStyles(IReadOnlyDictionary<Node, CssComputed> styles)
        {
            _context.Styles = styles ?? throw new ArgumentNullException(nameof(styles));
        }
        

        
        /// <summary>
        /// Compute layout for the entire tree using the 2-pass Measure/Arrange protocol.
        /// Returns an immutable LayoutResult.
        /// </summary>
        public LayoutResult ComputeLayout(
            Node node, 
            float x, 
            float y, 
            float availableWidth, 
            bool shrinkToContent = false, 
            float availableHeight = 0, 
            bool hasTargetAncestor = false,
            FenBrowser.Core.Deadlines.FrameDeadline deadline = null)
        {
            // New Pipeline Entry Point (Active)
            if (LayoutDebugLogEnabled)
                DiagnosticPaths.AppendRootText("layout_engine_debug.txt", $"[LayoutEngine] ComputeLayout Called for {node?.GetType().Name}\n");

            Node layoutRoot = node;
            if (layoutRoot is Document doc)
            {
                layoutRoot = doc.DocumentElement ?? doc.FirstChild;
            }

            if (layoutRoot == null)
            {
                _cachedLayoutRoot = null;
                _cachedResult = null;
                return null;
            }

            // Incremental layout fast-path: skip the box-tree build + formatting-
            // context layout pass when the DOM root and viewport are unchanged.
            // After a full layout pass ClearSubtreeDirtyFlags clears dirty flags,
            // so paint-only frames return the cached result.
            // The cache is keyed on the size asked for: the pass below clamps it (a 0
            // height becomes 1 for a subtree layout), and comparing the next request with
            // the clamped value made an identical request look like a viewport change.
            float requestedWidth = availableWidth;
            float requestedHeight = availableHeight;
            bool viewportChanged = Math.Abs(availableWidth - _cachedViewportWidth) > 0.5f ||
                                   Math.Abs(availableHeight - _cachedViewportHeight) > 0.5f;

            bool hasUnmaterializedNestedBrowsingContext =
                HasUnmaterializedNestedBrowsingContext(layoutRoot, _cachedResult, _context.Styles);

            if (!viewportChanged &&
                ReferenceEquals(layoutRoot, _cachedLayoutRoot) &&
                _cachedResult != null &&
                !layoutRoot.LayoutDirty &&
                !layoutRoot.ChildLayoutDirty &&
                !hasUnmaterializedNestedBrowsingContext)
            {
                if (LayoutDebugLogEnabled)
                    DiagnosticPaths.AppendRootText("layout_engine_debug.txt", "[LayoutEngine] Incremental: reusing cached LayoutResult\n");
                return _cachedResult;
            }

            _boxStore.Reset();
            // 1. Build Box Tree. Tracked separately from the layout pass so we
            // can tell whether perf cost lives in DOM→Box construction or in
            // the formatting-context Layout() pass — historically the
            // unscoped RenderFrame.Layout span hid this.
            FenBrowser.FenEngine.Layout.Tree.LayoutBox rootBox;
            using (TimelineTracer.Instance.Begin("LayoutEngine.BoxTreeBuild", "layout"))
            {
                var builder = new FenBrowser.FenEngine.Layout.Tree.BoxTreeBuilder(_context.Styles, _boxStore);
                rootBox = builder.Build(layoutRoot);
            }

            if (rootBox == null)
            {
                if (LayoutDebugLogEnabled)
                    DiagnosticPaths.AppendRootText("layout_engine_debug.txt", "[LayoutEngine] WARN: RootBox is null. Layout aborted.\n");
                return null;
            }
            if (LayoutDebugLogEnabled)
                DiagnosticPaths.AppendRootText("layout_engine_debug.txt", $"[LayoutEngine] RootBox built: {rootBox}\n");

            // Check deadline before starting heavy layout pass
            deadline?.Check();

            // 2. Prepare Root Layout State
            var isDocumentLayout = node is Document || layoutRoot.ParentNode is Document;
            if (isDocumentLayout)
            {
                availableWidth = Math.Max(availableWidth, _context.ViewportWidth);
                availableHeight = Math.Max(availableHeight, _context.ViewportHeight);
            }
            else
            {
                availableWidth = Math.Max(1f, availableWidth);
                availableHeight = Math.Max(1f, availableHeight);
            }

            var initialState = new FenBrowser.FenEngine.Layout.Contexts.LayoutState(
                new SKSize(availableWidth, availableHeight),
                availableWidth,
                availableHeight,
                _context.ViewportWidth,
                _context.ViewportHeight,
                deadline
            );

            // 3. Layout pass — the formatting-context recursion. This is the
            // arm where Block/Flex/Inline/Grid contexts run, and where any
            // O(N²) or unbounded recursive cost would show up.
            using (TimelineTracer.Instance.Begin("LayoutEngine.FormattingContextLayout", "layout"))
            {
                var context = FenBrowser.FenEngine.Layout.Contexts.FormattingContext.Resolve(rootBox);
                if (LayoutDebugLogEnabled)
                    DiagnosticPaths.AppendRootText("layout_engine_debug.txt", $"[LayoutEngine] Resolved Context: {context?.GetType().Name}\n");
                FenBrowser.FenEngine.Layout.Contexts.FormattingContext.ResetPassCounters();
                TextLayoutComputer.ResetCacheCounters();
                FenBrowser.FenEngine.Layout.Contexts.LayoutBoxOps.ResetShiftCounters();
                FenBrowser.FenEngine.Layout.Tree.LayoutBoxStore.ResetSnapshotCounters();
                try
                {
                    context.Layout(rootBox, initialState);
                }
                finally
                {
                    // Reported on the deadline path too — a pass that blows its budget is
                    // exactly the one whose call count needs explaining.
                    var (layoutCalls, layoutCacheHits) =
                        FenBrowser.FenEngine.Layout.Contexts.FormattingContext.PassCounters;
                    var (textHits, textMisses, textEvictions, textEntries) =
                        TextLayoutComputer.CacheCounters;
                    var (shiftRoots, shiftNodes) =
                        FenBrowser.FenEngine.Layout.Contexts.LayoutBoxOps.ShiftCounters;
                    var (snapshots, snapshotBoxes, restores) =
                        FenBrowser.FenEngine.Layout.Tree.LayoutBoxStore.SnapshotCounters;
                    FenBrowser.Core.EngineLogCompat.Debug(
                        $"[PERF-LAYOUT] Formatting-context pass: calls={layoutCalls} cacheHits={layoutCacheHits} " +
                        $"textHits={textHits} textMisses={textMisses} textEvictions={textEvictions} textEntries={textEntries} " +
                        $"shiftRoots={shiftRoots} shiftNodes={shiftNodes} snapshots={snapshots} snapshotBoxes={snapshotBoxes} restores={restores}",
                        FenBrowser.Core.Logging.LogCategory.Layout);
                }
                if (LayoutDebugLogEnabled)
                    DiagnosticPaths.AppendRootText("layout_engine_debug.txt", "[LayoutEngine] Layout Pass Complete\n");
            }

            // 3.5 Final out-of-flow positioning — CSS 2.1 §10.1 containing-block
            // resolution. Formatting contexts provisionally resolve abs/fixed
            // boxes against the immediate parent in flow-local coordinates; this
            // pass re-resolves them in final page coordinates against the nearest
            // valid containing block (nearest positioned/transformed ancestor, or
            // the initial containing block).
            PositionOutOfFlowBoxes(rootBox, initialState);

            // 4. Materialize renderer-facing layout artifacts from the box tree.
            // Tracked separately so a slow flatten/collect step doesn't get
            // misattributed to the formatting-context Layout() pass above.
            var elementRects = new Dictionary<Element, ElementGeometry>();
            var accumulatedBoxes = new Dictionary<Node, BoxModel>();
            using (TimelineTracer.Instance.Begin("LayoutEngine.Materialize", "layout"))
            {
                // PASS 1: Flatten for Legacy API (Absolute Coordinates)
                FlattenBoxTreeAbsolute(rootBox, elementRects, _context, 0, 0);

                // PASS 2: Collect All Boxes for Renderer (Absolute Coordinates)
                CollectBoxesAbsolute(rootBox, accumulatedBoxes, 0, 0);

                RelayoutNestedBrowsingContexts(layoutRoot, elementRects, accumulatedBoxes, deadline);

                if (x != 0f || y != 0f)
                {
                    foreach (var box in accumulatedBoxes.Values)
                    {
                        Contexts.LayoutBoxOps.ShiftBoxModel(box, x, y);
                    }

                    var keys = elementRects.Keys.ToArray();
                    foreach (var element in keys)
                    {
                        var geometry = elementRects[element];
                        elementRects[element] = new ElementGeometry(
                            geometry.X + x,
                            geometry.Y + y,
                            geometry.Width,
                            geometry.Height);
                    }
                }
            }

            _generatedBoxes = accumulatedBoxes;

            
            // DUMP TREE FOR DEBUGGING — per-box file write, gated for perf.
            EngineLogCompat.Debug("--- NEW PIPELINE LAYOUT DUMP ---", LogCategory.Rendering);
            if (LayoutDebugLogEnabled)
            {
                DumpBoxTree(rootBox, 0);
            }
            EngineLogCompat.Debug("--- END NEW PIPELINE DUMP ---", LogCategory.Rendering);

            float contentHeight = ComputeDocumentContentHeight(rootBox, availableHeight);

            var result = new LayoutResult(
                elementRects,
                availableWidth,
                availableHeight,
                0,
                contentHeight
            );

            // Cache for incremental layout fast-path on next frame.
            // Must populate the cache BEFORE clearing dirty flags so a
            // throw in ClearSubtreeDirtyFlags does not leave the cache empty.
            _cachedLayoutRoot = layoutRoot;
            _cachedViewportWidth = requestedWidth;
            _cachedViewportHeight = requestedHeight;
            _cachedResult = result;

            // Layout owns layout invalidation. Style invalidation is consumed only
            // for nodes represented by the current computed-style snapshot; leaving
            // a newly inserted, unstyled node dirty lets the cascade worker discover
            // it instead of silently freezing a default-style box into the cache.
            try { ClearSubtreeDirtyFlags(layoutRoot); }
            catch { /* non-critical */ }

            return result;
        }

        private void RelayoutNestedBrowsingContexts(
            Node layoutRoot,
            Dictionary<Element, ElementGeometry> elementRects,
            Dictionary<Node, BoxModel> accumulatedBoxes,
            FenBrowser.Core.Deadlines.FrameDeadline deadline)
        {
            if (layoutRoot == null)
            {
                return;
            }

            var frameElements = EnumerateFrameElements(layoutRoot).ToArray();

            foreach (var frameElement in frameElements)
            {
                deadline?.Check();

                var frameDocument = frameElement.ChildNodes?.OfType<Document>().FirstOrDefault();
                if (frameDocument?.DocumentElement == null ||
                    !accumulatedBoxes.TryGetValue(frameElement, out var frameBox) ||
                    frameBox == null)
                {
                    continue;
                }

                var frameViewport = frameBox.ContentBox;
                if (!float.IsFinite(frameViewport.Width) ||
                    !float.IsFinite(frameViewport.Height) ||
                    frameViewport.Width <= 0f ||
                    frameViewport.Height <= 0f)
                {
                    continue;
                }

                var frameLayoutEngine = new LayoutEngine(
                    _context.Styles,
                    frameViewport.Width,
                    frameViewport.Height);
                var frameLayout = frameLayoutEngine.ComputeLayout(
                    frameDocument,
                    0,
                    0,
                    frameViewport.Width,
                    availableHeight: frameViewport.Height,
                    deadline: deadline);

                if (frameLayout == null)
                {
                    continue;
                }

                foreach (var frameEntry in frameLayoutEngine.AllBoxes)
                {
                    Contexts.LayoutBoxOps.ShiftBoxModel(
                        frameEntry.Value,
                        frameViewport.Left,
                        frameViewport.Top);
                    accumulatedBoxes[frameEntry.Key] = frameEntry.Value;
                }

                foreach (var frameRect in frameLayout.ElementRects)
                {
                    elementRects[frameRect.Key] = new ElementGeometry(
                        frameRect.Value.X + frameViewport.Left,
                        frameRect.Value.Y + frameViewport.Top,
                        frameRect.Value.Width,
                        frameRect.Value.Height);
                }
            }
        }

        private static bool HasUnmaterializedNestedBrowsingContext(
            Node layoutRoot,
            LayoutResult cachedResult,
            IReadOnlyDictionary<Node, CssComputed> styles)
        {
            if (layoutRoot == null || cachedResult == null)
            {
                return false;
            }

            foreach (var frameElement in EnumerateFrameElements(layoutRoot))
            {
                // A frame that is not rendered never gets a box, so a missing rect says
                // nothing about the cached result. YouTube keeps a display:none
                // about:blank iframe on every page; counting it as unmaterialized made
                // every layout request a full pass, the cached result never reused.
                if (IsInDisplayNoneSubtree(frameElement, styles))
                {
                    continue;
                }

                // A frame may be inserted between style/layout snapshots before its
                // child Document is attached. Do not reuse a result that predates the
                // atomic iframe host box: paint and input both need that geometry.
                if (!cachedResult.TryGetElementRect(frameElement, out _))
                {
                    return true;
                }

                var frameDocument = frameElement.ChildNodes?.OfType<Document>().FirstOrDefault();
                var frameRoot = frameDocument?.DocumentElement;
                if (frameRoot != null && !cachedResult.TryGetElementRect(frameRoot, out _))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when the element or an ancestor (across shadow boundaries) computes to
        /// display:none, so it generates no box (CSS Display 3, 2.5).
        /// </summary>
        internal static bool IsInDisplayNoneSubtree(Element element, IReadOnlyDictionary<Node, CssComputed> styles)
        {
            if (styles == null)
            {
                return false;
            }

            for (Node node = element; node != null; node = node is ShadowRoot shadow ? shadow.Host : node.ParentNode)
            {
                if (node is Element current &&
                    styles.TryGetValue(current, out var style) &&
                    string.Equals(style?.Display, "none", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // Frames inside shadow trees are part of the rendered tree: Cloudflare Turnstile's
        // challenge iframe sits in a closed shadow root. Walking the light tree only gave that
        // frame a host box but never laid out its document, so it painted empty.
        private static IEnumerable<Element> EnumerateFrameElements(Node root)
        {
            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node is Element element)
                {
                    if (string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return element;
                    }

                    if (element.GetAttachedShadowRoot() is { } shadowRoot)
                    {
                        stack.Push(shadowRoot);
                    }
                }

                for (var child = node.LastChild; child != null; child = child.PreviousSibling)
                {
                    stack.Push(child);
                }
            }
        }

        /// <summary>
        /// Clears the layout flags of a laid-out subtree. Style flags are left
        /// alone: they belong to the style pass, which clears each subtree as it
        /// cascades it. Clearing them here cleared an iframe host element's
        /// child-dirty bit while the frame document below it (which this walk
        /// does not enter) stayed marked, so the page root read as clean and the
        /// frame's pending style work was never flushed.
        /// </summary>
        private void ClearSubtreeDirtyFlags(Node node)
        {
            if (node == null)
            {
                return;
            }

            bool isFrameHost = node is Element element &&
                               string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase);
            if (!isFrameHost && node.ChildNodes != null)
            {
                foreach (var child in node.ChildNodes)
                {
                    ClearSubtreeDirtyFlags(child);
                }
            }

            if (node is Element host && host.GetAttachedShadowRoot() is { } attachedShadowRoot)
            {
                ClearSubtreeDirtyFlags(attachedShadowRoot);
            }

            node.ClearDirty(InvalidationKind.Layout);
        }
        
        private Dictionary<Node, FenBrowser.FenEngine.Layout.BoxModel> _generatedBoxes;

        // Incremental layout cache: when the DOM and styles are unchanged between
        // frames, skip the entire box-tree build + layout pass and return the
        // previous result. Cache keyed by root node identity and viewport size.
        private Node _cachedLayoutRoot;
        private float _cachedViewportWidth;
        private float _cachedViewportHeight;
        private LayoutResult _cachedResult;

        private float ComputeDocumentContentHeight(FenBrowser.FenEngine.Layout.Tree.LayoutBox rootBox, float viewportHeight)
        {
            if (rootBox == null)
            {
                return Math.Max(0, viewportHeight);
            }

            float maxBottom = 0;
            AccumulateDocumentExtents(rootBox, 0, 0, ref maxBottom);

            if (float.IsNaN(maxBottom) || float.IsInfinity(maxBottom))
            {
                maxBottom = 0;
            }

            return Math.Max(viewportHeight, maxBottom);
        }

        private void AccumulateDocumentExtents(
            FenBrowser.FenEngine.Layout.Tree.LayoutBox box,
            float parentContentAbsX,
            float parentContentAbsY,
            ref float maxBottom)
        {
            if (box == null) return;

            string position = LayoutStyleResolver.GetEffectivePosition(box.ComputedStyle);
            bool isFixed = position == "fixed";
            if (!isFixed)
            {
                float absoluteBottom = box.Geometry.MarginBox.Bottom;
                if (!float.IsNaN(absoluteBottom) && !float.IsInfinity(absoluteBottom))
                {
                    maxBottom = Math.Max(maxBottom, absoluteBottom);
                }
            }

            if (ClipsDescendantsFromDocumentExtent(box.SourceNode, box.ComputedStyle))
            {
                return;
            }

            float currentContentAbsX = box.Geometry.ContentBox.Left;
            float currentContentAbsY = box.Geometry.ContentBox.Top;

            foreach (var child in box.Children)
            {
                AccumulateDocumentExtents(child, currentContentAbsX, currentContentAbsY, ref maxBottom);
            }
        }

        /// <summary>
        /// CSS Overflow 3 §2.2 scrollable overflow: content a box clips (any overflow other
        /// than visible) or a nested browsing context holds is not part of the document's
        /// scrollable overflow. The root element and body are exempt: their overflow is the
        /// viewport's (§3.3 propagation). Counting clipped content made pages scrollable that
        /// are not: CodeMirror's scroller is 50px taller than the overflow:hidden editor
        /// around it, so w3schools' tryit page scrolled its navbar out of view.
        /// </summary>
        public static bool ClipsDescendantsFromDocumentExtent(Node node, CssComputed style)
        {
            if (node is not Element element)
            {
                return false;
            }

            var tag = element.TagName;
            if (string.Equals(tag, "iframe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "frame", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "object", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, "embed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (element.ParentNode is Document ||
                (string.Equals(tag, "body", StringComparison.OrdinalIgnoreCase) && element.ParentElement?.ParentNode is Document))
            {
                return false;
            }

            return style != null &&
                   (IsClippingOverflow(style.OverflowX) || IsClippingOverflow(style.OverflowY) ||
                    IsClippingOverflow(style.Overflow));

            static bool IsClippingOverflow(string value) =>
                !string.IsNullOrWhiteSpace(value) &&
                value.Trim().ToLowerInvariant() is "hidden" or "clip" or "scroll" or "auto";
        }

        /// <summary>
        /// Whether some ancestor of <paramref name="element"/> keeps it out of the document's
        /// scrollable overflow (see <see cref="ClipsDescendantsFromDocumentExtent"/>). A frame
        /// document is reached through its parent, the frame element. Answers are cached per
        /// ancestor so a pass over every element stays linear.
        /// </summary>
        public static bool IsExcludedFromDocumentExtent(
            Element element,
            IReadOnlyDictionary<Node, CssComputed> styles,
            Dictionary<Node, bool> cache)
        {
            var visited = new List<Node>();
            var excluded = false;
            for (var node = element?.ParentNode; node != null; node = node.ParentNode)
            {
                if (cache.TryGetValue(node, out var known))
                {
                    excluded = known;
                    break;
                }

                visited.Add(node);
                CssComputed style = null;
                styles?.TryGetValue(node, out style);
                if (ClipsDescendantsFromDocumentExtent(node, style))
                {
                    excluded = true;
                    break;
                }
            }

            foreach (var node in visited)
            {
                cache[node] = excluded;
            }

            return excluded;
        }
        
        private void CollectBoxesAbsolute(FenBrowser.FenEngine.Layout.Tree.LayoutBox box, Dictionary<Node, FenBrowser.FenEngine.Layout.BoxModel> dict, float parentContentAbsX, float parentContentAbsY)
        {
            if (box == null) return;

            if (box.SourceNode != null)
            {
                var absModel = new BoxModel
                {
                    ContentBox = box.Geometry.ContentBox,
                    PaddingBox = box.Geometry.PaddingBox,
                    BorderBox = box.Geometry.BorderBox,
                    MarginBox = box.Geometry.MarginBox,
                    Lines = box.Geometry.Lines,  // Copy text lines for proper rendering
                    Baseline = box.Geometry.Baseline,
                    LineHeight = box.Geometry.LineHeight,
                    Ascent = box.Geometry.Ascent,
                    Descent = box.Geometry.Descent
                };

                // Formatting contexts position the full subtree into document coordinates
                // before LayoutEngine materializes renderer-facing boxes. Re-applying the
                // parent content origin here double-shifts nested descendants and produces
                // ghost borders/backgrounds detached from their content.
                if (!dict.TryGetValue(box.SourceNode, out var existingModel) ||
                    ShouldPreferMaterializedBox(existingModel, absModel))
                {
                    dict[box.SourceNode] = absModel;
                }
            }

            float currentContentAbsX = box.Geometry.ContentBox.Left;
            float currentContentAbsY = box.Geometry.ContentBox.Top;

            foreach(var c in box.Children)
                CollectBoxesAbsolute(c, dict, currentContentAbsX, currentContentAbsY);
        }

        private static bool ShouldPreferMaterializedBox(BoxModel existingModel, BoxModel candidateModel)
        {
            if (existingModel == null)
            {
                return true;
            }

            static float Area(SKRect rect)
            {
                return Math.Max(0f, rect.Width) * Math.Max(0f, rect.Height);
            }

            float existingArea = Area(existingModel.BorderBox);
            float candidateArea = Area(candidateModel.BorderBox);
            if (candidateArea > existingArea + 0.5f)
            {
                return true;
            }

            bool existingHasLines = existingModel.Lines != null && existingModel.Lines.Count > 0;
            bool candidateHasLines = candidateModel.Lines != null && candidateModel.Lines.Count > 0;
            if (!existingHasLines && candidateHasLines)
            {
                return true;
            }

            bool existingEmpty = existingArea <= 0.5f &&
                                 Math.Max(0f, existingModel.ContentBox.Width) * Math.Max(0f, existingModel.ContentBox.Height) <= 0.5f;
            bool candidateVisible = candidateArea > 0.5f ||
                                    (Math.Max(0f, candidateModel.ContentBox.Width) * Math.Max(0f, candidateModel.ContentBox.Height) > 0.5f);
            return existingEmpty && candidateVisible;
        }

        /// <summary>
        /// Re-resolves out-of-flow boxes in final page coordinates against their
        /// CSS 2.1 §10.1 containing blocks. position:absolute resolves against the
        /// nearest positioned/transformed ancestor (or the initial containing
        /// block); position:fixed keeps the viewport resolution unless a
        /// transform/filter/perspective ancestor establishes its containing block.
        /// Runs top-down so nested out-of-flow boxes see updated ancestor geometry.
        /// </summary>
        private void PositionOutOfFlowBoxes(FenBrowser.FenEngine.Layout.Tree.LayoutBox box, FenBrowser.FenEngine.Layout.Contexts.LayoutState state)
        {
            if (box == null)
            {
                return;
            }

            // An outside ::marker is laid out as position:absolute only to take it out of
            // flow; CSS Lists places it beside its list item, which
            // FormattingContext.ArrangeOutsideListMarker has done. Re-solving it against
            // a containing block would throw that away.
            if (box.Geometry != null && box.ComputedStyle != null &&
                box is not FenBrowser.FenEngine.Layout.Tree.ListMarkerBox)
            {
                var position = LayoutStyleResolver.GetEffectivePosition(box.ComputedStyle);
                if (string.Equals(position, "absolute", StringComparison.OrdinalIgnoreCase))
                {
                    ResolveFinalOutOfFlowGeometry(box, state, forFixed: false);
                }
                else if (string.Equals(position, "fixed", StringComparison.OrdinalIgnoreCase))
                {
                    ResolveFinalOutOfFlowGeometry(box, state, forFixed: true);
                }
            }

            var children = box.Children;
            for (int i = 0; i < children.Count; i++)
            {
                PositionOutOfFlowBoxes(children[i], state);
            }
        }

        private void ResolveFinalOutOfFlowGeometry(FenBrowser.FenEngine.Layout.Tree.LayoutBox box, FenBrowser.FenEngine.Layout.Contexts.LayoutState state, bool forFixed)
        {
            var cbBox = forFixed
                ? LayoutPositioningLogic.FindTransformContainingBlockForFixed(box)
                : LayoutPositioningLogic.FindContainingBlockForPositioned(box);

            // position:fixed with no transform ancestor keeps its viewport
            // resolution from the flow pass.
            if (forFixed && cbBox == null)
            {
                return;
            }

            var cbRect = LayoutPositioningLogic.GetContainingBlockRect(
                cbBox,
                state.ViewportWidth,
                state.ViewportHeight);

            // CSS 2.1 §10.3.7 / §10.6.4: auto insets fall back to the static position
            // for fixed boxes too; a fixed box only gets here with a transformed
            // ancestor as its containing block, which lives in document space.
            SKPoint? staticPosition = null;
            if (box.OutOfFlowStaticPosition.HasValue &&
                box.Parent?.Geometry != null)
            {
                var contentOrigin = box.Parent.Geometry.ContentBox;
                var relativeStatic = box.OutOfFlowStaticPosition.Value;
                staticPosition = new SKPoint(
                    contentOrigin.Left + relativeStatic.X,
                    contentOrigin.Top + relativeStatic.Y);
            }

            var laidOutSize = box.Geometry.ContentBox.Size;
            ResolveFinal();

            // The flow pass solved this box against its parent, which is not its containing
            // block unless the parent is positioned. When the real containing block changes
            // the box's size, its contents were laid out at the wrong one: #container on
            // w3schools' tryit page (absolute under a static body, top:44px; bottom:0) kept
            // its panes at the height the flow pass derived from body's.
            var solvedSize = box.Geometry.ContentBox.Size;
            if (Math.Abs(solvedSize.Width - laidOutSize.Width) > 0.5f ||
                Math.Abs(solvedSize.Height - laidOutSize.Height) > 0.5f)
            {
                LayoutPositioningLogic.LayoutAtSolvedSize(box, cbRect, state);
                ResolveFinal();
            }

            void ResolveFinal() => LayoutPositioningLogic.ResolvePositionedBox(
                box,
                cbBox ?? box.Parent,
                cbBox?.Geometry ?? box.Parent?.Geometry,
                state,
                collapsePositioningMarginsInFinalGeometry: true,
                staticPosition: staticPosition,
                finalContainingBlockRect: cbRect);
        }

        private void FlattenBoxTreeAbsolute(FenBrowser.FenEngine.Layout.Tree.LayoutBox box, Dictionary<Element, ElementGeometry> rects, LayoutContext context, float parentContentAbsX, float parentContentAbsY)
        {
             if (box == null) return;

             // The box tree already carries document coordinates for visual geometry.
             float absBorderX = (float)box.Geometry.BorderBox.Left;
             float absBorderY = (float)box.Geometry.BorderBox.Top;

             if (box.SourceNode is Element el)
             {
                 var b = box.Geometry.BorderBox;
                 rects[el] = new ElementGeometry(absBorderX, absBorderY, b.Width, b.Height);
             }

             float currentContentAbsX = box.Geometry.ContentBox.Left;
             float currentContentAbsY = box.Geometry.ContentBox.Top;

             foreach(var c in box.Children) FlattenBoxTreeAbsolute(c, rects, context, currentContentAbsX, currentContentAbsY);
        }
        
        /// <summary>
        /// Simplified entry point for root layout.
        /// </summary>
        public LayoutResult ComputeLayout(Node root, float availableWidth, float availableHeight)
        {
            return ComputeLayout(root, 0, 0, availableWidth, false, availableHeight, false);
        }

        
        /// <summary>
        /// Creates a LayoutResult from the current context state.
        /// Used after layout is computed to build an immutable result.
        /// </summary>
        public LayoutResult BuildResult(float contentWidth, float contentHeight)
        {
            var elementRects = new Dictionary<Element, ElementGeometry>();

            foreach (var kvp in _context.Boxes)
            {
                if (kvp.Key is Element elem && kvp.Value != null)
                {
                    var box = kvp.Value.BorderBox;
                    // Last write wins for same element
                    elementRects[elem] = new ElementGeometry(box.Left, box.Top, box.Width, box.Height);
                }
            }
            
            return new LayoutResult(
                elementRects,
                _context.ViewportWidth,
                _context.ViewportHeight,
                0, // scrollY - will be injected from ScrollModel
                contentHeight
            );
        }
        
        public CssComputed GetStyle(Node node) => _context.GetStyle(node);
        public BoxModel GetBox(Node node) => _context.GetBox(node);
        
        /// <summary>
        /// Expose all calculated boxes for rendering and debugging.
        /// </summary>
        public IReadOnlyDictionary<Node, BoxModel> AllBoxes
        {
            get
            {
                 if (_generatedBoxes != null) return _generatedBoxes;

                 var dict = new Dictionary<Node, BoxModel>();
                 foreach(var kvp in _context.Boxes) dict[kvp.Key] = kvp.Value;
                 return dict;
            }
        }
        
        // --- Phase 3: Reverse Pipeline (HitTest) ---
        
        /// <summary>
        /// Performs hit testing to find the deepest DOM node at the given physical coordinates.
        /// Implements the reverse pipeline: Click (x,y) → Layout → DOM Node.
        /// </summary>
        /// <param name="x">Physical X coordinate.</param>
        /// <param name="y">Physical Y coordinate.</param>
        /// <param name="root">Root node to start search from.</param>
        /// <returns>The deepest DOM node containing the point, or null if none found.</returns>
        public Node HitTest(float x, float y, Node root)
        {
            if (root == null) return null;
            
            Node result = null;
            HitTestRecursive(x, y, root, ref result);
            return result;
        }
        
        // Hit-testing runs on every mouse move on the UI thread — the thread
        // with the *smallest* stack budget (main message-pump thread, default
        // 1 MB on x64 Windows). A recursive walk through a real-app DOM
        // (React/Vue can produce 100+-deep layout trees) was a stack-overflow
        // waiting to happen, and StackOverflow on the UI thread vanishes the
        // process with no chance of a managed handler. Iterative walk with an
        // explicit stack; semantics preserved (deepest matching descendant
        // wins, children of boxless wrapper nodes are still searched).
        private void HitTestRecursive(float x, float y, Node node, ref Node deepestHit)
        {
            if (node == null) return;

            var stack = new System.Collections.Generic.Stack<Node>();
            stack.Push(node);

            while (stack.Count > 0)
            {
                var current = stack.Pop();

                BoxModel box = _context.GetBox(current);

                bool descendIntoChildren;
                if (box != null)
                {
                    var rect = box.BorderBox;
                    bool inside = x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;
                    if (inside)
                    {
                        deepestHit = current;
                        descendIntoChildren = true;
                    }
                    else
                    {
                        // Boxed but point is outside its border-box — no descendant
                        // can match either (children paint inside parent), so we
                        // can short-circuit. This matches the recursive version,
                        // which only recursed when 'inside' was true for a boxed
                        // node.
                        descendIntoChildren = false;
                    }
                }
                else
                {
                    // No box: this is a wrapper / non-laid-out node. Descend so
                    // the recursive version's "still check children" branch is
                    // preserved.
                    descendIntoChildren = true;
                }

                if (descendIntoChildren && current.ChildNodes != null)
                {
                    // Push in reverse so the original iteration order is
                    // preserved (last sibling wins on equal-depth hits, matching
                    // the previous foreach behaviour).
                    var children = current.ChildNodes;
                    for (int i = children.Length - 1; i >= 0; i--)
                    {
                        var child = children[i];
                        if (child != null) stack.Push(child);
                    }
                }
            }
        }



        private void DumpBoxTree(FenBrowser.FenEngine.Layout.Tree.LayoutBox box, int depth)
        {
            if (box == null) return;
            
            string indent = new string(' ', depth * 2);
            string tagName = (box.SourceNode as Element)?.TagName ?? box.GetType().Name;
            var r = box.Geometry.BorderBox;
            string rectStr = $"[{r.Left:F1}, {r.Top:F1} {r.Width:F1}x{r.Height:F1}]";
            string extra = "";
            if (box.ComputedStyle?.Display == "flex") extra += " (FLEX)";
            var position = LayoutStyleResolver.GetEffectivePosition(box.ComputedStyle);
            if (!string.IsNullOrEmpty(position)) extra += $" pos={position}";
            if (box.IsOutOfFlow) extra += " oof";
            
            DiagnosticPaths.AppendRootText("layout_engine_debug.txt", $"{indent}{tagName} {rectStr}{extra}\n");
            
            foreach(var c in box.Children)
            {
                DumpBoxTree(c, depth + 1);
            }
        }
    }
}
