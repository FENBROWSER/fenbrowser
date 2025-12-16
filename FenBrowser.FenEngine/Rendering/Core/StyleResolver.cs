using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.FenEngine.Rendering.Core
{
    /// <summary>
    /// Separates style resolution from CSS parsing.
    /// Pipeline: DOM Tree + CSSOM Rules → Computed Styles per Node
    /// 
    /// Architecture:
    /// ┌──────────────┐     ┌──────────────┐
    /// │  DOM Tree    │     │ CSSOM Rules  │
    /// └──────┬───────┘     └──────┬───────┘
    ///        │                    │
    ///        └────────┬───────────┘
    ///                 ▼
    ///        ┌────────────────────┐
    ///        │   StyleResolver    │
    ///        │  (This class)      │
    ///        └────────┬───────────┘
    ///                 ▼
    ///        ┌────────────────────┐
    ///        │  Computed Styles   │
    ///        │  per DOM Node      │
    ///        └────────────────────┘
    /// </summary>
    public class StyleResolver
    {
        private readonly Dictionary<LiteElement, CssComputed> _computedStyles;
        private readonly Uri _baseUri;
        private readonly double? _viewportWidth;
        private readonly double? _viewportHeight;

        /// <summary>
        /// Create a new StyleResolver for a document.
        /// </summary>
        public StyleResolver(Uri baseUri, double? viewportWidth = null, double? viewportHeight = null)
        {
            _baseUri = baseUri;
            _viewportWidth = viewportWidth;
            _viewportHeight = viewportHeight;
            _computedStyles = new Dictionary<LiteElement, CssComputed>();
        }

        /// <summary>
        /// Resolve styles for the entire DOM tree.
        /// </summary>
        public async Task<Dictionary<LiteElement, CssComputed>> ResolveAsync(
            LiteElement root,
            Func<Uri, Task<string>> fetchExternalCssAsync = null)
        {
            _computedStyles.Clear();

            // Delegate to CssLoader for now (will be refactored later)
            var styles = await CssLoader.ComputeAsync(
                root,
                _baseUri,
                fetchExternalCssAsync,
                _viewportWidth);

            foreach (var kvp in styles)
            {
                _computedStyles[kvp.Key] = kvp.Value;
            }

            FenLogger.Debug($"[StyleResolver] Resolved styles for {_computedStyles.Count} elements", LogCategory.CSS);
            return _computedStyles;
        }

        /// <summary>
        /// Get computed style for a specific element.
        /// </summary>
        public CssComputed GetComputedStyle(LiteElement element)
        {
            return _computedStyles.TryGetValue(element, out var style) ? style : null;
        }

        /// <summary>
        /// Invalidate styles for an element and its descendants (for dynamic updates).
        /// </summary>
        public void InvalidateStyles(LiteElement element)
        {
            if (element == null) return;

            _computedStyles.Remove(element);
            if (element.Children != null)
            {
                foreach (var child in element.Children)
                {
                    InvalidateStyles(child);
                }
            }
        }

        /// <summary>
        /// Check if an element has computed styles.
        /// </summary>
        public bool HasStyles(LiteElement element)
        {
            return _computedStyles.ContainsKey(element);
        }

        /// <summary>
        /// Get all computed styles (read-only).
        /// </summary>
        public IReadOnlyDictionary<LiteElement, CssComputed> ComputedStyles => _computedStyles;
    }
}
