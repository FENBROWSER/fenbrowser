using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Scripting
{
    public class CanvasRenderingContext2D : IObject
    {
        private readonly LiteElement _element;
        private readonly JavaScriptEngine _engine;
        private Image _imageControl;
        private WriteableBitmap _bitmap;
        private IObject _prototype;

        // State
        public string fillStyle { get; set; } = "#000000";
        public string strokeStyle { get; set; } = "#000000";
        public double lineWidth { get; set; } = 1.0;

        // Path state
        private SKPath _currentPath;

        public CanvasRenderingContext2D(LiteElement element, JavaScriptEngine engine)
        {

            _element = element;
            _engine = engine;
            _currentPath = new SKPath();
        }

        // IObject Implementation
        public IValue Get(string key)
        {

            switch (key)
            {
                case "fillStyle": return FenValue.FromString(fillStyle);
                case "strokeStyle": return FenValue.FromString(strokeStyle);
                case "lineWidth": return FenValue.FromNumber(lineWidth);
                case "fillRect": return FenValue.FromFunction(new FenFunction("fillRect", (args, thisVal) => {

                    if (args.Length >= 4) fillRect(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber(), args[3].ToNumber());
                    return FenValue.Undefined;
                }));
                case "clearRect": return FenValue.FromFunction(new FenFunction("clearRect", (args, thisVal) => {

                    if (args.Length >= 4) clearRect(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber(), args[3].ToNumber());
                    return FenValue.Undefined;
                }));
                case "beginPath": return FenValue.FromFunction(new FenFunction("beginPath", (args, thisVal) => {
                    beginPath();
                    return FenValue.Undefined;
                }));
                case "moveTo": return FenValue.FromFunction(new FenFunction("moveTo", (args, thisVal) => {
                    if (args.Length >= 2) moveTo(args[0].ToNumber(), args[1].ToNumber());
                    return FenValue.Undefined;
                }));
                case "lineTo": return FenValue.FromFunction(new FenFunction("lineTo", (args, thisVal) => {
                    if (args.Length >= 2) lineTo(args[0].ToNumber(), args[1].ToNumber());
                    return FenValue.Undefined;
                }));
                case "stroke": return FenValue.FromFunction(new FenFunction("stroke", (args, thisVal) => {
                    stroke();
                    return FenValue.Undefined;
                }));
                case "fill": return FenValue.FromFunction(new FenFunction("fill", (args, thisVal) => {
                    fill();
                    return FenValue.Undefined;
                }));
                default: return FenValue.Undefined;
            }
        }

        public void Set(string key, IValue value)
        {
            switch (key)
            {
                case "fillStyle": fillStyle = value.ToString(); break;
                case "strokeStyle": strokeStyle = value.ToString(); break;
                case "lineWidth": lineWidth = value.ToNumber(); break;
            }
        }

        public bool Has(string key) => !Get(key).IsUndefined;
        public bool Delete(string key) => false;
        public IEnumerable<string> Keys() => new[] { "fillStyle", "strokeStyle", "lineWidth", "fillRect", "clearRect", "beginPath", "moveTo", "lineTo", "stroke", "fill" };
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;

        private void EnsureSurface()
        {
            try
            {
                // Always check current visual from engine as renderer might have recreated it
                var currentVisual = JavaScriptEngine.GetVisual(_element) as Image;
                
                if (currentVisual != null && currentVisual != _imageControl)
                {

                    _imageControl = currentVisual;
                    
                    // If we have an existing bitmap, attach it to the new visual
                    if (_bitmap != null)
                    {
                        _imageControl.Source = _bitmap;
                        _imageControl.InvalidateVisual();
                    }
                }

                if (_imageControl == null)
                {

                    return;
                }

                if (_bitmap == null)
                {
                    // Check if we have a persisted bitmap for this element
                    _bitmap = JavaScriptEngine.GetCanvasBitmap(_element);
                    
                    if (_bitmap == null)
                    {
                        // Get dimensions from the control or element attributes
                        var w = (int)_imageControl.Width;
                        var h = (int)_imageControl.Height;
                        if (w <= 0) w = 300;
                        if (h <= 0) h = 150;


                        
                        // Create WriteableBitmap
                        _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                        
                        // Persist it
                        JavaScriptEngine.RegisterCanvasBitmap(_element, _bitmap);
                    }
                    else
                    {

                    }
                    
                    _imageControl.Source = _bitmap;
                }
            }
            catch (Exception ex)
            {

            }
        }

        private void Draw(Action<SKCanvas> drawAction)
        {
            Dispatcher.UIThread.Post(() =>
            {
                EnsureSurface();
                if (_bitmap == null) return;

                using (var buf = _bitmap.Lock())
                {
                    var info = new SKImageInfo(buf.Size.Width, buf.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using (var surface = SKSurface.Create(info, buf.Address, buf.RowBytes))
                    {
                        if (surface != null)
                        {
                            drawAction(surface.Canvas);
                        }
                    }
                }
                // Invalidate visual to trigger repaint
                _imageControl.InvalidateVisual();
            });
        }

        public void fillRect(double x, double y, double w, double h)
        {
            var color = ParseColor(fillStyle);
            Draw(canvas =>
            {
                using (var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill })
                {
                    canvas.DrawRect((float)x, (float)y, (float)w, (float)h, paint);
                }
            });
        }

        public void clearRect(double x, double y, double w, double h)
        {
            Draw(canvas =>
            {
                // If clearing full canvas, use Clear
                // Otherwise draw transparent rect with Src mode? Skia is simpler:
                using (var paint = new SKPaint { BlendMode = SKBlendMode.Clear })
                {
                    canvas.DrawRect((float)x, (float)y, (float)w, (float)h, paint);
                }
            });
        }

        public void beginPath()
        {
            _currentPath = new SKPath();
        }

        public void moveTo(double x, double y)
        {
            _currentPath.MoveTo((float)x, (float)y);
        }

        public void lineTo(double x, double y)
        {
            _currentPath.LineTo((float)x, (float)y);
        }

        public void stroke()
        {
            var color = ParseColor(strokeStyle);
            var width = (float)lineWidth;
            // Clone path to avoid threading issues if modified during post? 
            // Actually we are posting the whole draw action, but _currentPath is shared.
            // Better to snapshot the path data.
            var pathData = new SKPath(_currentPath); 

            Draw(canvas =>
            {
                using (var paint = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width, IsAntialias = true })
                {
                    canvas.DrawPath(pathData, paint);
                }
                pathData.Dispose();
            });
        }

        public void fill()
        {
            var color = ParseColor(fillStyle);
            var pathData = new SKPath(_currentPath);

            Draw(canvas =>
            {
                using (var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true })
                {
                    canvas.DrawPath(pathData, paint);
                }
                pathData.Dispose();
            });
        }

        private SKColor ParseColor(string color)
        {
            if (string.IsNullOrWhiteSpace(color)) return SKColors.Black;
            if (SKColor.TryParse(color, out var skColor)) return skColor;
            return SKColors.Black;
        }
    }
}
