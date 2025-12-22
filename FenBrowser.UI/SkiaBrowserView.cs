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
                // Draw placeholder or clear
                context.DrawRectangle(Brushes.White, null, new Rect(Bounds.Size));
                return;
            }

            var customDraw = new BrowserDrawCheckOperation(new Rect(Bounds.Size), _renderer, _root, _styles, BaseUrl, LayoutViewport);
            context.Custom(customDraw);
        }
        
        // Input handling
        protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            
            var point = e.GetPosition(this);
            // Simple hit test wrapper for link clicking
            // Real interaction should go through BrowserApi/InputManager, but this shortcuts for UI layer
             
             // Convert to document coordinates? Renderer handles this logic usually or we need to bridge it.
             // For now, let's just trigger a generic click if needed or rely on MainWindow logic.
             // MainWindow uses WebDriverIntegration or direct access.
             // But we need to define LinkInternalClicked logic.
             
             // Since _renderer.HitTest is available, we can use it.
             // But _renderer is stateful and updated during Render.
             
             if (_renderer.HitTest((float)point.X, (float)point.Y, out var result))
             {
                 if (!string.IsNullOrEmpty(result.Href))
                 {
                     LinkInternalClicked?.Invoke(this, result.Href);
                 }
                 
                 // Handle other clicks (inputs etc)
                 // This requires updating the renderer state
                 // _renderer.OnClick(result.Element); // Not available in immutable result
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
                
                // _renderer.OnHover(result.Element); // Not available
            }
            else
            {
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
                 _renderer.OnHover(null);
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

                try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", $"[BrowserDrawCheckOperation] Invoking _renderer.Render. Viewport={paintViewport} LayoutSize={layoutSize}\r\n"); } catch {}
                _renderer.Render(_root, canvas, _styles, paintViewport, _baseUrl, null, layoutSize);
            }
        }
    }
}
