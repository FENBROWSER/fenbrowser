using Avalonia;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;
using System;
using System.Collections.Generic;

namespace FenBrowser.UI
{
    public class SkiaBrowserView : Control
    {
        private Element _root;
        private Dictionary<Node, CssComputed> _styles;
        private SkiaDomRenderer _renderer;
        private Element _lastHoveredElement;
        
        // Properties
        public string BaseUrl { get; set; }
        private SKSize _layoutViewport;
        public SKSize LayoutViewport
        {
            get => _layoutViewport;
            set
            {
                if (_layoutViewport != value)
                {
                    _layoutViewport = value;
                    // Sync control size with layout viewport to ensure hit testing works across the entire area
                    // and the ScrollViewer parent knows the content bounds.
                    Width = value.Width;
                    Height = value.Height;
                    Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
                }
            }
        }
        
        // Events
        public event EventHandler<string> LinkInternalClicked;

        public SkiaBrowserView()
        {
            _renderer = new SkiaDomRenderer();
            ClipToBounds = true;
            Focusable = true; // Required to receive pointer events
        }

        public void Render(Element root, Dictionary<Node, CssComputed> styles)
        {
            _root = root;
            _styles = styles;
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            
            if (_root == null || _styles == null)
            {
                // Keep the white background as placeholder if no content
                return;
            }

            var customDraw = new BrowserDrawCheckOperation(new Rect(Bounds.Size), _renderer, _root, _styles, BaseUrl, LayoutViewport);
            context.Custom(customDraw);
            
            FenLogger.Debug($"[SkiaBrowserView] Render complete. Bounds={Bounds}", LogCategory.General);
        }
        
        // Input handling
        protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            
            var point = e.GetPosition(this);
            FenLogger.Debug($"[SkiaBrowserView] OnPointerPressed at ({point.X:F1}, {point.Y:F1})", LogCategory.General);
             
             if (_renderer.HitTest((float)point.X, (float)point.Y, out var result))
             {
                 FenLogger.Debug($"[SkiaBrowserView] HitTest SUCCESS: Element='{result.TagName}', Href='{result.Href ?? "null"}', IsClickable={result.IsClickable}, Box={result.BoundingBox}", LogCategory.General);
                 
                 if (!string.IsNullOrEmpty(result.Href))
                 {
                     FenLogger.Info($"[SkiaBrowserView] CLICK -> Link detected: {result.Href}", LogCategory.General);
                     LinkInternalClicked?.Invoke(this, result.Href);
                 }
             }
             else
             {
                 FenLogger.Debug($"[SkiaBrowserView] HitTest FAILED (no link found) at ({point.X:F1}, {point.Y:F1})", LogCategory.General);
             }
        }
        
        /// <summary>
        /// Public method to handle click events forwarded from parent ScrollViewer.
        /// This bypasses hit testing issues with custom-drawn controls in Avalonia.
        /// </summary>
        public void HandleClickFromParent(Point point)
        {
            FenLogger.Debug($"[SkiaBrowserView] HandleClickFromParent (Forwarded) at ({point.X:F1}, {point.Y:F1})", LogCategory.General);
            
            if (_renderer.HitTest((float)point.X, (float)point.Y, out var result))
            {
                FenLogger.Debug($"[SkiaBrowserView] FORWARD HitTest SUCCESS: Element='{result.TagName}', Href='{result.Href ?? "null"}', IsClickable={result.IsClickable}, Box={result.BoundingBox}", LogCategory.General);
                
                if (!string.IsNullOrEmpty(result.Href))
                {
                    FenLogger.Info($"[SkiaBrowserView] FORWARD CLICK -> Link detected: {result.Href}", LogCategory.General);
                    LinkInternalClicked?.Invoke(this, result.Href);
                }
            }
            else
            {
                FenLogger.Debug($"[SkiaBrowserView] FORWARD HitTest FAILED (no link found) at ({point.X:F1}, {point.Y:F1})", LogCategory.General);
            }
        }
        
        protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var point = e.GetPosition(this);
            
            if (_renderer.HitTest((float)point.X, (float)point.Y, out var result))
            {
                // Handle cursor changes, etc.
                if (!string.IsNullOrEmpty(result.Href))
                {
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                }
                else
                {
                    Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
                }
                
                var element = result.NativeElement as Element;
                if (element != _lastHoveredElement)
                {
                    _renderer.OnHover(element, _lastHoveredElement);
                    _lastHoveredElement = element;
                    Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
                }
            }
            else
            {
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
                if (_lastHoveredElement != null)
                {
                    _renderer.OnHover(null, _lastHoveredElement);
                    _lastHoveredElement = null;
                    Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
                }
            }
        }

        class BrowserDrawCheckOperation : ICustomDrawOperation
        {
            private readonly SkiaDomRenderer _renderer;
            private readonly Node _root;
            private readonly Dictionary<Node, CssComputed> _styles;
            private readonly string _baseUrl;
            private readonly SKSize _layoutViewport;

            public Rect Bounds { get; }

            public BrowserDrawCheckOperation(Rect bounds, SkiaDomRenderer renderer, Node root, Dictionary<Node, CssComputed> styles, string baseUrl, SKSize layoutViewport)
            {
                Bounds = bounds;
                _renderer = renderer;
                _root = root;
                _styles = styles;
                _baseUrl = baseUrl;
                _layoutViewport = layoutViewport;
            }

            public void Dispose() { }

            public bool HitTest(Point p) => Bounds.Contains(p);

            public bool Equals(ICustomDrawOperation other) => false; // Always redraw

            public void Render(ImmediateDrawingContext context)
            {
                var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
                if (lease == null) return;
                
                using var skia = lease.Lease();
                var canvas = skia.SkCanvas;
                
                // SKRect viewport = new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height);
                // Call renderer
                // Use LayoutViewport if provided (preferable for consistent layout), else render bounds
                 SKRect paintViewport = new SKRect(0, 0, (float)Bounds.Width, (float)Bounds.Height);
                 SKSize? layoutSize = _layoutViewport.Width > 0 ? _layoutViewport : (SKSize?)null;

                FenLogger.Debug($"[BrowserDrawCheckOperation] Invoking _renderer.Render. Viewport={paintViewport} LayoutSize={layoutSize}", LogCategory.General);
                _renderer.Render(_root, canvas, _styles, paintViewport, _baseUrl, null, layoutSize);
            }
        }
    }
}
