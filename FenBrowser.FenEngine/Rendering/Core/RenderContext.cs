using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;



using FenBrowser.FenEngine.Layout; // Required for BoxModel

namespace FenBrowser.FenEngine.Rendering.Core
{
    public class RenderContext
    {
        private Dictionary<Node, CssComputed> _styles = new Dictionary<Node, CssComputed>();
        private Dictionary<Node, BoxModel> _boxes = new Dictionary<Node, BoxModel>();
        private IReadOnlyList<Rendering.PaintNodeBase> _paintTreeRoots = Array.Empty<Rendering.PaintNodeBase>();
        private float _viewportHeight;
        private float _viewportWidth;
        private SKRect _viewport;
        private string _baseUrl = string.Empty;

        public Dictionary<Node, CssComputed> Styles
        {
            get => _styles;
            set => _styles = value ?? new Dictionary<Node, CssComputed>();
        }

        public Dictionary<Node, BoxModel> Boxes
        {
            get => _boxes;
            set => _boxes = value ?? new Dictionary<Node, BoxModel>();
        }
        // Added for Interaction Engine
        public IReadOnlyList<Rendering.PaintNodeBase> PaintTreeRoots
        {
            get => _paintTreeRoots;
            set => _paintTreeRoots = value ?? Array.Empty<Rendering.PaintNodeBase>();
        }

        public float ViewportHeight
        {
            get => _viewportHeight;
            set => _viewportHeight = NormalizeFloat(value);
        }

        public float ViewportWidth
        {
            get => _viewportWidth;
            set => _viewportWidth = NormalizeFloat(value);
        }

        public SKRect Viewport
        {
            get => _viewport;
            set => _viewport = NormalizeRect(value);
        }

        public string BaseUrl
        {
            get => _baseUrl;
            set => _baseUrl = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        public bool HasViewport => ViewportWidth > 0f && ViewportHeight > 0f;

        public CssComputed GetStyle(Node node)
        {
            if (node != null && Styles.TryGetValue(node, out var style)) return style;
            return null;
        }

        public bool TryGetBox(Node node, out BoxModel box)
        {
            if (node != null && Boxes.TryGetValue(node, out box))
            {
                return true;
            }

            box = null;
            return false;
        }

        private static float NormalizeFloat(float value)
        {
            return float.IsFinite(value) && value >= 0f ? value : 0f;
        }

        private static SKRect NormalizeRect(SKRect rect)
        {
            if (!float.IsFinite(rect.Left) || !float.IsFinite(rect.Top) ||
                !float.IsFinite(rect.Right) || !float.IsFinite(rect.Bottom))
            {
                return SKRect.Empty;
            }

            return rect;
        }
    }
}


