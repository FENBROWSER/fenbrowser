using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// Custom CSS engine wrapping the existing CssLoader.
    /// This is the default implementation and will always be available.
    /// </summary>
    public class CustomCssEngine : ICssEngine
    {
        public string EngineName => "FenBrowser.Custom";

        private Dictionary<Node, CssComputed> _lastComputed;
        
        // Expose CSS sources for DevTools
        public List<CssLoader.CssSource> LastSources { get; private set; }

        public async Task<Dictionary<Node, CssComputed>> ComputeStylesAsync(
            Element root,
            Uri baseUri,
            Func<Uri, Task<string>> fetchExternalCssAsync,
            double? viewportWidth = null,
            double? viewportHeight = null)
        {
            try
            {
                // Use existing CssLoader
                var result = await CssLoader.ComputeWithResultAsync(
                    root, baseUri, fetchExternalCssAsync, 
                    viewportWidth, viewportHeight, null);
                
                _lastComputed = result.Computed;
                // Don't store sources - causes memory issues on complex sites like GitHub
                // LastSources = result.Sources;
                LastSources = null;
                return result.Computed;
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[CustomCssEngine] ComputeStylesAsync error: {ex.Message}", LogCategory.Rendering);
                return new Dictionary<Node, CssComputed>();
            }
        }

        public CssComputed GetComputedStyle(Element element)
        {
            if (_lastComputed != null && _lastComputed.TryGetValue(element, out var style))
            {
                return style;
            }
            return new CssComputed();
        }

        public Dictionary<string, string> ParseInlineStyle(string styleValue)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(styleValue)) return result;

            // Simple inline style parsing (already handled by CssLoader internally)
            // This is a lightweight version for direct use
            try
            {
                var parts = styleValue.Split(';');
                foreach (var part in parts)
                {
                    var colonIdx = part.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        var name = part.Substring(0, colonIdx).Trim().ToLowerInvariant();
                        var value = part.Substring(colonIdx + 1).Trim();
                        if (!string.IsNullOrEmpty(name))
                        {
                            result[name] = value;
                        }
                    }
                }
            }
            catch { }
            return result;
        }
    }

    /// <summary>
    /// Static factory for getting the configured CSS engine.
    /// </summary>
    public static class CssEngineFactory
    {
        private static CustomCssEngine _customEngine;

        /// <summary>
        /// Get the currently configured CSS engine.
        /// Always returns Custom engine.
        /// </summary>
        public static ICssEngine GetEngine()
        {
            return GetCustomEngine();
        }

        private static CustomCssEngine GetCustomEngine()
        {
            return _customEngine ??= new CustomCssEngine();
        }

        /// <summary>
        /// Clear cached engines (useful for testing).
        /// </summary>
        public static void ClearCache()
        {
            _customEngine = null;
        }
    }
}
