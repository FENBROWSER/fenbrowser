using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using FenBrowser.Core;
using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering
{
    public class SkiaBrowserView : Control
    {
        private SkiaDomRenderer _renderer;
        private LiteElement _root;
        private Dictionary<LiteElement, CssComputed> _styles;

        public SkiaBrowserView()
        {
            _renderer = new SkiaDomRenderer();
        }

        public void Render(LiteElement root, Dictionary<LiteElement, CssComputed> styles)
        {
            _root = root;
            _styles = styles;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (_root == null)
            {
                // Debug draw to prove control exists
                context.DrawRectangle(Brushes.LightYellow, new Pen(Brushes.Red, 2), new Rect(0,0, Bounds.Width, Bounds.Height));
                var ft = new FormattedText("SkiaView Ready (Waiting for Content)", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 24, Brushes.Black);
                context.DrawText(ft, new Point(20, 20));
                return;
            }
            context.Custom(new SkiaDrawOperation(new Rect(0, 0, Bounds.Width, Bounds.Height), _root, _renderer, _styles));
        }

        private class SkiaDrawOperation : ICustomDrawOperation
        {
            private readonly LiteElement _root;
            private readonly SkiaDomRenderer _renderer;
            private readonly Dictionary<LiteElement, CssComputed> _styles;

            public Rect Bounds { get; }

            public SkiaDrawOperation(Rect bounds, LiteElement root, SkiaDomRenderer renderer, Dictionary<LiteElement, CssComputed> styles)
            {
                Bounds = bounds;
                _root = root;
                _renderer = renderer;
                _styles = styles;
            }

            public void Dispose() { }

            public bool HitTest(Point p) => false;

            public bool Equals(ICustomDrawOperation other) => false;

            public void Render(ImmediateDrawingContext context)
            {
                if (!context.TryGetFeature<ISkiaSharpApiLeaseFeature>(out var leaseFeature))
                {
                    return;
                }

                using var lease = leaseFeature.Lease();
                var canvas = lease.SkCanvas;
                
                // Ensure high quality
                canvas.Save();
                try
                {
                    // Convert Avalonia Rect to Skia Rect
                    var viewport = new SKRect((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Right, (float)Bounds.Bottom);
                    _renderer.Render(_root, canvas, _styles, viewport);
                }
                catch (Exception)
                {
                    // Ignore
                }
                finally
                {
                    canvas.Restore();
                }
            }
        }
    }
}
