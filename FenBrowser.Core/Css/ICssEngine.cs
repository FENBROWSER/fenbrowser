using FenBrowser.Core.Dom;
using FenBrowser.Core.Deadlines;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;

namespace FenBrowser.Core.Css
{
    /// <summary>
    /// CSS Engine abstraction layer.
    /// Used by the rendering engine to compute styles.
    /// </summary>
    public interface ICssEngine
    {
        /// <summary>
        /// Engine name for debugging/logging.
        /// </summary>
        string EngineName { get; }

        /// <summary>
        /// Compute styles for all elements in a DOM tree.
        /// </summary>
        /// <param name="root">The root element (usually the HTML element)</param>
        /// <param name="baseUri">Base URI for resolving relative URLs</param>
        /// <param name="cssFetcher">Function to fetch external CSS files</param>
        /// <param name="viewportWidth">Viewport width for media queries</param>
        /// <param name="viewportHeight">Viewport height for media queries</param>
        /// <returns>Dictionary mapping nodes to their computed styles</returns>
        Task<Dictionary<Node, CssComputed>> ComputeStylesAsync(
            Element root,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            double? viewportWidth = null,
            double? viewportHeight = null,
            FrameDeadline deadline = null);

        /// <summary>
        /// Get computed style for a single element (useful for JS getComputedStyle).
        /// </summary>
        CssComputed GetComputedStyle(Element element);

        /// <summary>
        /// Parse inline style attribute value.
        /// </summary>
        Dictionary<string, string> ParseInlineStyle(string styleValue);

    }

    /// <summary>
    /// Configuration for CSS engine selection.
    /// </summary>
    public enum CssEngineType
    {
        /// <summary>Use the custom FenBrowser CSS parser (CssLoader.cs)</summary>
        Custom
    }

    /// <summary>
    /// Global CSS engine configuration.
    /// Change this to switch engines at runtime or for testing.
    /// </summary>
    public static class CssEngineConfig
    {
        /// <summary>
        /// Current CSS engine type. Default is Custom (existing CssLoader).
        /// </summary>
        public static CssEngineType CurrentEngine { get; set; } = CssEngineType.Custom;

        /// <summary>
        /// Enable detailed CSS parsing logs.
        /// </summary>
        public static bool EnableLogging { get; set; } = false;
    }
}
