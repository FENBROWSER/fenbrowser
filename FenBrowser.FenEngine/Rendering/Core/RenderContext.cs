using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Rendering.Core
{
    /// <summary>
    /// Centralized render context containing shared state for layout and painting.
    /// This allows extracted modules (FlexLayout, Painter, etc.) to access shared state.
    /// </summary>
    public class RenderContext
    {
        // =====================================
        // Box Model Storage
        // =====================================
        
        /// <summary>
        /// Computed box models for each element.
        /// Use ConcurrentDictionary for thread safety between render and hit test.
        /// </summary>
        public ConcurrentDictionary<LiteElement, BoxModel> Boxes { get; } = new ConcurrentDictionary<LiteElement, BoxModel>();
        
        /// <summary>
        /// Parent-child relationship map for DOM traversal.
        /// </summary>
        public Dictionary<LiteElement, LiteElement> Parents { get; } = new Dictionary<LiteElement, LiteElement>();
        
        /// <summary>
        /// Text lines for wrapped text elements.
        /// </summary>
        public Dictionary<LiteElement, List<TextLine>> TextLines { get; } = new Dictionary<LiteElement, List<TextLine>>();
        
        // =====================================
        // Computed Styles
        // =====================================
        
        /// <summary>
        /// Computed CSS styles for each element.
        /// </summary>
        public Dictionary<LiteElement, CssComputed> Styles { get; set; }
        
        // =====================================
        // Viewport Information
        // =====================================
        
        /// <summary>
        /// Viewport height for resolving vh units and height: 100%.
        /// </summary>
        public float ViewportHeight { get; set; }
        
        /// <summary>
        /// Viewport width for resolving vw units and width: 100%.
        /// </summary>
        public float ViewportWidth { get; set; }
        
        /// <summary>
        /// Full viewport rectangle for position: fixed elements.
        /// </summary>
        public SKRect Viewport { get; set; }
        
        /// <summary>
        /// Base URL for resolving relative URLs.
        /// </summary>
        public string BaseUrl { get; set; }
        
        // =====================================
        // CSS Counters
        // =====================================
        
        /// <summary>
        /// CSS counter values for counter-increment/counter-reset.
        /// </summary>
        public Dictionary<string, int> Counters { get; } = new Dictionary<string, int>();
        
        // =====================================
        // Deferred Layout
        // =====================================
        
        /// <summary>
        /// Absolute/fixed positioned elements deferred for two-pass layout.
        /// </summary>
        public List<LiteElement> DeferredAbsoluteElements { get; } = new List<LiteElement>();
        
        // =====================================
        // Caches
        // =====================================
        
        /// <summary>
        /// Intrinsic size cache for images (reduces re-layout calls).
        /// Key: image URL, Value: (width, height)
        /// </summary>
        public Dictionary<string, (float width, float height)> IntrinsicSizeCache { get; } = new Dictionary<string, (float, float)>();
        
        /// <summary>
        /// Text measurement cache for elements.
        /// </summary>
        public Dictionary<LiteElement, float> TextMeasureCache { get; } = new Dictionary<LiteElement, float>();
        
        // =====================================
        // Scroll State
        // =====================================
        
        /// <summary>
        /// Scroll offset for hash fragment navigation.
        /// </summary>
        public float ScrollOffsetY { get; set; }
        
        /// <summary>
        /// Scrollbar visibility state.
        /// </summary>
        public bool VerticalScrollbarVisible { get; set; }
        
        // =====================================
        // Debug Flags
        // =====================================
        
        /// <summary>
        /// Enable debug layout visualization (box boundaries).
        /// </summary>
        public bool DebugLayout { get; set; } = false;
        
        /// <summary>
        /// Enable file-based debug logging.
        /// </summary>
        public bool DebugFileLogging { get; set; } = true;
        
        // =====================================
        // Helper Methods
        // =====================================
        
        /// <summary>
        /// Get the style for an element, or null if not found.
        /// </summary>
        public CssComputed GetStyle(LiteElement node)
        {
            if (node == null || Styles == null) return null;
            Styles.TryGetValue(node, out var style);
            return style;
        }
        
        /// <summary>
        /// Get the parent of an element, or null if not found.
        /// </summary>
        public LiteElement GetParent(LiteElement node)
        {
            if (node == null) return null;
            Parents.TryGetValue(node, out var parent);
            return parent;
        }
        
        /// <summary>
        /// Get the box for an element, or null if not found.
        /// </summary>
        public BoxModel GetBox(LiteElement node)
        {
            if (node == null) return null;
            Boxes.TryGetValue(node, out var box);
            return box;
        }
        
        /// <summary>
        /// Clear all state for a fresh render pass.
        /// </summary>
        public void Clear()
        {
            Boxes.Clear();
            Parents.Clear();
            TextLines.Clear();
            Counters.Clear();
            DeferredAbsoluteElements.Clear();
            IntrinsicSizeCache.Clear();
            TextMeasureCache.Clear();
        }
    }
    
    /// <summary>
    /// Box model storage for a single element.
    /// </summary>
    public class BoxModel
    {
        public SKRect MarginBox { get; set; }
        public SKRect BorderBox { get; set; }
        public SKRect PaddingBox { get; set; }
        public SKRect ContentBox { get; set; }
        public Avalonia.Thickness Margin { get; set; }
        public Avalonia.Thickness Border { get; set; }
        public Avalonia.Thickness Padding { get; set; }
        public float Ascent { get; set; }  // For text baseline alignment
        public float Descent { get; set; } // For text baseline alignment
    }
    
    /// <summary>
    /// Text line for text wrapping.
    /// </summary>
    public class TextLine
    {
        public string Text { get; set; }
        public float Width { get; set; }
        public float Y { get; set; }
    }
}
