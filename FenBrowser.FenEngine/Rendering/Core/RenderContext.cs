using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Core
{
    public class RenderContext
    {
        public Dictionary<LiteElement, CssComputed> Styles { get; set; } = new Dictionary<LiteElement, CssComputed>();
        public Dictionary<LiteElement, RenderBox> Boxes { get; set; } = new Dictionary<LiteElement, RenderBox>();
        public float ViewportHeight { get; set; }
        public float ViewportWidth { get; set; }
        public SKRect Viewport { get; set; }
        public string BaseUrl { get; set; }

        public CssComputed GetStyle(LiteElement node)
        {
            if (node != null && Styles != null && Styles.TryGetValue(node, out var style)) return style;
            return null;
        }
    }
}
