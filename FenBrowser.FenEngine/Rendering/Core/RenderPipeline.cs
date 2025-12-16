using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Core
{
    /// <summary>
    /// Orchestrates the complete rendering pipeline.
    /// 
    /// Pipeline stages:
    /// 1. Parse      : HTML Text → DOM Tree (HtmlParser)
    /// 2. Style      : DOM + CSS → Computed Styles (StyleResolver)
    /// 3. Layout     : Computed Styles → Box Tree (LayoutEngine)
    /// 4. Paint      : Box Tree → Display List (Painter)
    /// 5. Composite  : Display List → Frame Buffer
    /// </summary>
    public class RenderPipeline
    {
        private readonly StyleResolver _styleResolver;
        private readonly BoxTree _boxTree;
        private SKRect _viewport;
        private Uri _baseUri;

        public RenderPipeline(Uri baseUri, double viewportWidth, double viewportHeight)
        {
            _baseUri = baseUri;
            _viewport = new SKRect(0, 0, (float)viewportWidth, (float)viewportHeight);
            _styleResolver = new StyleResolver(baseUri, viewportWidth, viewportHeight);
            _boxTree = new BoxTree();
        }

        /// <summary>
        /// Run the complete rendering pipeline.
        /// </summary>
        public async Task<BoxTree> RunAsync(
            LiteElement domTree,
            Func<Uri, Task<string>> fetchCssAsync = null)
        {
            FenLogger.Info("[RenderPipeline] Starting pipeline", LogCategory.Rendering);

            // Stage 2: Style Resolution
            var styles = await _styleResolver.ResolveAsync(domTree, fetchCssAsync);
            FenLogger.Debug($"[RenderPipeline] Stage 2: Resolved {styles.Count} styles", LogCategory.Rendering);

            // Stage 3: Box Tree Construction (delegated to LayoutEngine)
            _boxTree.Clear();
            BuildBoxTree(domTree, null, styles);
            FenLogger.Debug($"[RenderPipeline] Stage 3: Built {_boxTree.AllBoxes} boxes", LogCategory.Rendering);

            return _boxTree;
        }

        /// <summary>
        /// Build box tree from DOM tree.
        /// </summary>
        private void BuildBoxTree(LiteElement element, BoxNode parentBox, Dictionary<LiteElement, CssComputed> styles)
        {
            if (element == null) return;

            styles.TryGetValue(element, out var style);

            // Skip display:none elements
            if (style?.Display?.ToLowerInvariant() == "none") return;

            var box = _boxTree.CreateBox(element, style);

            if (parentBox != null)
            {
                parentBox.AddChild(box);
            }
            else
            {
                _boxTree.Root = box;
            }

            // Recursively build for children
            if (element.Children != null)
            {
                foreach (var child in element.Children)
                {
                    if (!child.IsText) // Skip text nodes for now
                    {
                        BuildBoxTree(child, box, styles);
                    }
                }
            }
        }

        /// <summary>
        /// Invalidate layout for an element subtree.
        /// </summary>
        public void InvalidateLayout(LiteElement element)
        {
            _styleResolver.InvalidateStyles(element);
            // Box tree will be rebuilt on next render
        }

        /// <summary>
        /// Update viewport dimensions.
        /// </summary>
        public void UpdateViewport(double width, double height)
        {
            _viewport = new SKRect(0, 0, (float)width, (float)height);
        }

        /// <summary>
        /// Get the box tree (read-only access).
        /// </summary>
        public BoxTree BoxTree => _boxTree;

        /// <summary>
        /// Get the style resolver (for computed style queries).
        /// </summary>
        public StyleResolver StyleResolver => _styleResolver;
    }
}
