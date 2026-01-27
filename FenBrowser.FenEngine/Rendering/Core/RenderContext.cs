using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;

using FenBrowser.FenEngine.Rendering;

using FenBrowser.FenEngine.Layout; // Required for BoxModel

namespace FenBrowser.FenEngine.Rendering.Core
{
    public class RenderContext
    {
        public Dictionary<Node, CssComputed> Styles { get; set; } = new Dictionary<Node, CssComputed>();
        public Dictionary<Node, BoxModel> Boxes { get; set; } = new Dictionary<Node, BoxModel>();
        // Added for Interaction Engine
        public IReadOnlyList<Rendering.PaintNodeBase> PaintTreeRoots { get; set; }
        public float ViewportHeight { get; set; }
        public float ViewportWidth { get; set; }
        public SKRect Viewport { get; set; }
        public string BaseUrl { get; set; }

        public CssComputed GetStyle(Node node)
        {
            if (node != null && Styles != null && Styles.TryGetValue(node, out var style)) return style;
            return null;
        }
    }
}


