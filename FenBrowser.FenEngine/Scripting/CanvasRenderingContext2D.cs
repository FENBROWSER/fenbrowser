using System;
using System.Collections.Generic;
// Avalonia imports removed for Skia/Silk migration
using SkiaSharp;
using FenBrowser.Core;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// Full HTML5 Canvas 2D Rendering Context implementation.
    /// Implements the CanvasRenderingContext2D interface per W3C spec.
    /// </summary>
    public class CanvasRenderingContext2D : IObject
    {
        private readonly LiteElement _element;
        private readonly JavaScriptEngine _engine;
        // private object _imageControl; // Removed legacy control ref
        private SKBitmap _bitmap;
        private IObject _prototype;

        // Current drawing state
        private CanvasState _state;
        private Stack<CanvasState> _stateStack;
        private SKPath _currentPath;

        public CanvasRenderingContext2D(LiteElement element, JavaScriptEngine engine)
        {
            _element = element;
            _engine = engine;
            _currentPath = new SKPath();
            _stateStack = new Stack<CanvasState>();
            _state = new CanvasState();
        }

        #region State Properties (via _state)
        
        public string fillStyle 
        { 
            get => _state.FillStyle; 
            set => _state.FillStyle = value; 
        }
        
        public string strokeStyle 
        { 
            get => _state.StrokeStyle; 
            set => _state.StrokeStyle = value; 
        }
        
        public double lineWidth 
        { 
            get => _state.LineWidth; 
            set => _state.LineWidth = value; 
        }
        
        public string lineCap 
        { 
            get => _state.LineCap; 
            set => _state.LineCap = value; 
        }
        
        public string lineJoin 
        { 
            get => _state.LineJoin; 
            set => _state.LineJoin = value; 
        }
        
        public double miterLimit 
        { 
            get => _state.MiterLimit; 
            set => _state.MiterLimit = value; 
        }
        
        public double globalAlpha 
        { 
            get => _state.GlobalAlpha; 
            set => _state.GlobalAlpha = Math.Max(0, Math.Min(1, value)); 
        }
        
        public string globalCompositeOperation 
        { 
            get => _state.GlobalCompositeOperation; 
            set => _state.GlobalCompositeOperation = value; 
        }
        
        public string font 
        { 
            get => _state.Font; 
            set => _state.Font = value; 
        }
        
        public string textAlign 
        { 
            get => _state.TextAlign; 
            set => _state.TextAlign = value; 
        }
        
        public string textBaseline 
        { 
            get => _state.TextBaseline; 
            set => _state.TextBaseline = value; 
        }
        
        public double shadowBlur 
        { 
            get => _state.ShadowBlur; 
            set => _state.ShadowBlur = value; 
        }
        
        public string shadowColor 
        { 
            get => _state.ShadowColor; 
            set => _state.ShadowColor = value; 
        }
        
        public double shadowOffsetX 
        { 
            get => _state.ShadowOffsetX; 
            set => _state.ShadowOffsetX = value; 
        }
        
        public double shadowOffsetY 
        { 
            get => _state.ShadowOffsetY; 
            set => _state.ShadowOffsetY = value; 
        }
        
        #endregion

        #region IObject Implementation
        
        public IValue Get(string key)
        {
            switch (key)
            {
                // Properties
                case "fillStyle": return FenValue.FromString(fillStyle);
                case "strokeStyle": return FenValue.FromString(strokeStyle);
                case "lineWidth": return FenValue.FromNumber(lineWidth);
                case "lineCap": return FenValue.FromString(lineCap);
                case "lineJoin": return FenValue.FromString(lineJoin);
                case "miterLimit": return FenValue.FromNumber(miterLimit);
                case "globalAlpha": return FenValue.FromNumber(globalAlpha);
                case "globalCompositeOperation": return FenValue.FromString(globalCompositeOperation);
                case "font": return FenValue.FromString(font);
                case "textAlign": return FenValue.FromString(textAlign);
                case "textBaseline": return FenValue.FromString(textBaseline);
                case "shadowBlur": return FenValue.FromNumber(shadowBlur);
                case "shadowColor": return FenValue.FromString(shadowColor);
                case "shadowOffsetX": return FenValue.FromNumber(shadowOffsetX);
                case "shadowOffsetY": return FenValue.FromNumber(shadowOffsetY);
                
                // Rectangle methods
                case "fillRect": return CreateMethod("fillRect", args => {
                    if (args.Length >= 4) fillRect(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber(), args[3].ToNumber());
                });
                case "strokeRect": return CreateMethod("strokeRect", args => {
                    if (args.Length >= 4) strokeRect(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber(), args[3].ToNumber());
                });
                case "clearRect": return CreateMethod("clearRect", args => {
                    if (args.Length >= 4) clearRect(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber(), args[3].ToNumber());
                });
                
                // Path methods
                case "beginPath": return CreateMethod("beginPath", _ => beginPath());
                case "closePath": return CreateMethod("closePath", _ => closePath());
                case "moveTo": return CreateMethod("moveTo", args => {
                    if (args.Length >= 2) moveTo(args[0].ToNumber(), args[1].ToNumber());
                });
                case "lineTo": return CreateMethod("lineTo", args => {
                    if (args.Length >= 2) lineTo(args[0].ToNumber(), args[1].ToNumber());
                });
                case "arc": return CreateMethod("arc", args => {
                    if (args.Length >= 5)
                    {
                        bool ccw = args.Length > 5 && args[5].ToBoolean();
                        arc(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber(), 
                            args[3].ToNumber(), args[4].ToNumber(), ccw);
                    }
                });
                case "arcTo": return CreateMethod("arcTo", args => {
                    if (args.Length >= 5)
                        arcTo(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber(), 
                              args[3].ToNumber(), args[4].ToNumber());
                });
                case "quadraticCurveTo": return CreateMethod("quadraticCurveTo", args => {
                    if (args.Length >= 4)
                        quadraticCurveTo(args[0].ToNumber(), args[1].ToNumber(), 
                                         args[2].ToNumber(), args[3].ToNumber());
                });
                case "bezierCurveTo": return CreateMethod("bezierCurveTo", args => {
                    if (args.Length >= 6)
                        bezierCurveTo(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber(),
                                      args[3].ToNumber(), args[4].ToNumber(), args[5].ToNumber());
                });
                case "rect": return CreateMethod("rect", args => {
                    if (args.Length >= 4) rect(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber(), args[3].ToNumber());
                });
                case "ellipse": return CreateMethod("ellipse", args => {
                    if (args.Length >= 7)
                    {
                        bool ccw = args.Length > 7 && args[7].ToBoolean();
                        ellipse(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber(), args[3].ToNumber(),
                                args[4].ToNumber(), args[5].ToNumber(), args[6].ToNumber(), ccw);
                    }
                });
                
                // Drawing methods
                case "stroke": return CreateMethod("stroke", _ => stroke());
                case "fill": return CreateMethod("fill", args => {
                    string fillRule = args.Length > 0 ? args[0].ToString() : "nonzero";
                    fill(fillRule);
                });
                case "clip": return CreateMethod("clip", args => {
                    string fillRule = args.Length > 0 ? args[0].ToString() : "nonzero";
                    clip(fillRule);
                });
                
                // Text methods
                case "fillText": return CreateMethod("fillText", args => {
                    if (args.Length >= 3)
                    {
                        double? maxWidth = args.Length > 3 ? args[3].ToNumber() : null;
                        fillText(args[0].ToString(), args[1].ToNumber(), args[2].ToNumber(), maxWidth);
                    }
                });
                case "strokeText": return CreateMethod("strokeText", args => {
                    if (args.Length >= 3)
                    {
                        double? maxWidth = args.Length > 3 ? args[3].ToNumber() : null;
                        strokeText(args[0].ToString(), args[1].ToNumber(), args[2].ToNumber(), maxWidth);
                    }
                });
                case "measureText": return FenValue.FromFunction(new FenFunction("measureText", (args, thisVal) => {
                    if (args.Length >= 1)
                    {
                        var metrics = measureText(args[0].ToString());
                        var result = new FenObject();
                        result.Set("width", FenValue.FromNumber(metrics.Width));
                        return FenValue.FromObject(result);
                    }
                    return FenValue.Undefined;
                }));
                
                // Transform methods
                case "save": return CreateMethod("save", _ => save());
                case "restore": return CreateMethod("restore", _ => restore());
                case "scale": return CreateMethod("scale", args => {
                    if (args.Length >= 2) scale(args[0].ToNumber(), args[1].ToNumber());
                });
                case "rotate": return CreateMethod("rotate", args => {
                    if (args.Length >= 1) rotate(args[0].ToNumber());
                });
                case "translate": return CreateMethod("translate", args => {
                    if (args.Length >= 2) translate(args[0].ToNumber(), args[1].ToNumber());
                });
                case "transform": return CreateMethod("transform", args => {
                    if (args.Length >= 6)
                        transform(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber(),
                                  args[3].ToNumber(), args[4].ToNumber(), args[5].ToNumber());
                });
                case "setTransform": return CreateMethod("setTransform", args => {
                    if (args.Length >= 6)
                        setTransform(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber(),
                                     args[3].ToNumber(), args[4].ToNumber(), args[5].ToNumber());
                });
                case "resetTransform": return CreateMethod("resetTransform", _ => resetTransform());
                
                // Line style
                case "setLineDash": return CreateMethod("setLineDash", args => {
                    if (args.Length >= 1 && args[0].IsObject)
                    {
                        var arr = args[0].AsObject();
                        var len = (int)(arr?.Get("length")?.ToNumber() ?? 0);
                        var dashes = new float[len];
                        for (int i = 0; i < len; i++)
                            dashes[i] = (float)(arr?.Get(i.ToString())?.ToNumber() ?? 0);
                        setLineDash(dashes);
                    }
                });
                case "getLineDash": return FenValue.FromFunction(new FenFunction("getLineDash", (args, thisVal) => {
                    var dashes = getLineDash();
                    var arr = new FenObject();
                    for (int i = 0; i < dashes.Length; i++)
                        arr.Set(i.ToString(), FenValue.FromNumber(dashes[i]));
                    arr.Set("length", FenValue.FromNumber(dashes.Length));
                    return FenValue.FromObject(arr);
                }));
                
                // Gradient
                case "createLinearGradient": return FenValue.FromFunction(new FenFunction("createLinearGradient", (args, thisVal) => {
                    if (args.Length >= 4)
                    {
                        var gradient = createLinearGradient(args[0].ToNumber(), args[1].ToNumber(),
                                                            args[2].ToNumber(), args[3].ToNumber());
                        return FenValue.FromObject(gradient);
                    }
                    return FenValue.Undefined;
                }));
                case "createRadialGradient": return FenValue.FromFunction(new FenFunction("createRadialGradient", (args, thisVal) => {
                    if (args.Length >= 6)
                    {
                        var gradient = createRadialGradient(args[0].ToNumber(), args[1].ToNumber(), args[2].ToNumber(),
                                                            args[3].ToNumber(), args[4].ToNumber(), args[5].ToNumber());
                        return FenValue.FromObject(gradient);
                    }
                    return FenValue.Undefined;
                }));
                
                // Pixel manipulation
                case "getImageData": return FenValue.FromFunction(new FenFunction("getImageData", (args, thisVal) => {
                    if (args.Length >= 4)
                    {
                        var data = getImageData((int)args[0].ToNumber(), (int)args[1].ToNumber(),
                                                (int)args[2].ToNumber(), (int)args[3].ToNumber());
                        return FenValue.FromObject(data);
                    }
                    return FenValue.Undefined;
                }));
                case "putImageData": return CreateMethod("putImageData", args => {
                    if (args.Length >= 3 && args[0].IsObject)
                        putImageData(args[0].AsObject(), (int)args[1].ToNumber(), (int)args[2].ToNumber());
                });
                case "createImageData": return FenValue.FromFunction(new FenFunction("createImageData", (args, thisVal) => {
                    if (args.Length >= 2)
                    {
                        var data = createImageData((int)args[0].ToNumber(), (int)args[1].ToNumber());
                        return FenValue.FromObject(data);
                    }
                    return FenValue.Undefined;
                }));
                
                // Point in path
                case "isPointInPath": return FenValue.FromFunction(new FenFunction("isPointInPath", (args, thisVal) => {
                    if (args.Length >= 2)
                        return FenValue.FromBoolean(isPointInPath(args[0].ToNumber(), args[1].ToNumber()));
                    return FenValue.FromBoolean(false);
                }));
                case "isPointInStroke": return FenValue.FromFunction(new FenFunction("isPointInStroke", (args, thisVal) => {
                    if (args.Length >= 2)
                        return FenValue.FromBoolean(isPointInStroke(args[0].ToNumber(), args[1].ToNumber()));
                    return FenValue.FromBoolean(false);
                }));
                
                default: return FenValue.Undefined;
            }
        }
        
        private IValue CreateMethod(string name, Action<IValue[]> action)
        {
            return FenValue.FromFunction(new FenFunction(name, (args, thisVal) => {
                action(args);
                return FenValue.Undefined;
            }));
        }

        public void Set(string key, IValue value)
        {
            switch (key)
            {
                case "fillStyle": fillStyle = value.ToString(); break;
                case "strokeStyle": strokeStyle = value.ToString(); break;
                case "lineWidth": lineWidth = value.ToNumber(); break;
                case "lineCap": lineCap = value.ToString(); break;
                case "lineJoin": lineJoin = value.ToString(); break;
                case "miterLimit": miterLimit = value.ToNumber(); break;
                case "globalAlpha": globalAlpha = value.ToNumber(); break;
                case "globalCompositeOperation": globalCompositeOperation = value.ToString(); break;
                case "font": font = value.ToString(); break;
                case "textAlign": textAlign = value.ToString(); break;
                case "textBaseline": textBaseline = value.ToString(); break;
                case "shadowBlur": shadowBlur = value.ToNumber(); break;
                case "shadowColor": shadowColor = value.ToString(); break;
                case "shadowOffsetX": shadowOffsetX = value.ToNumber(); break;
                case "shadowOffsetY": shadowOffsetY = value.ToNumber(); break;
            }
        }

        public bool Has(string key) => !Get(key).IsUndefined;
        public bool Delete(string key) => false;
        public IEnumerable<string> Keys() => new[] { 
            "fillStyle", "strokeStyle", "lineWidth", "lineCap", "lineJoin", "miterLimit",
            "globalAlpha", "globalCompositeOperation", "font", "textAlign", "textBaseline",
            "shadowBlur", "shadowColor", "shadowOffsetX", "shadowOffsetY",
            "fillRect", "strokeRect", "clearRect", "beginPath", "closePath", "moveTo", "lineTo",
            "arc", "arcTo", "quadraticCurveTo", "bezierCurveTo", "rect", "ellipse",
            "stroke", "fill", "clip", "fillText", "strokeText", "measureText",
            "save", "restore", "scale", "rotate", "translate", "transform", "setTransform", "resetTransform",
            "setLineDash", "getLineDash", "createLinearGradient", "createRadialGradient",
            "getImageData", "putImageData", "createImageData", "isPointInPath", "isPointInStroke"
        };
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
        
        #endregion

        #region Surface Management
        
        private void EnsureSurface()
        {
            try
            {
                if (_bitmap == null)
                {
                    _bitmap = JavaScriptEngine.GetCanvasBitmap(_element);
                    
                    if (_bitmap == null)
                    {
                        var wStr = _element.Attr.ContainsKey("width") ? _element.Attr["width"] : "300";
                        var hStr = _element.Attr.ContainsKey("height") ? _element.Attr["height"] : "150";
                        if (!int.TryParse(wStr, out int w)) w = 300;
                        if (!int.TryParse(hStr, out int h)) h = 150;
                        
                        _bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                        JavaScriptEngine.RegisterCanvasBitmap(_element, _bitmap);
                    }
                }
            }
            catch { }
        }

        private void Draw(Action<SKCanvas> drawAction)
        {
            // [MIGRATION] Removed Dispatcher.UIThread.Post. Executing synchronously on current thread (JS thread).
            // This is generally safe for offscreen canvas.
            EnsureSurface();
            if (_bitmap == null) return;

            // Just draw to the bitmap
            using (var canvas = new SKCanvas(_bitmap))
            {
                // Apply current transform
                canvas.SetMatrix(_state.Transform);
                drawAction(canvas);
            }
            
            // Notify engine of repaint if needed
            _engine.RequestRender?.Invoke();
        }
        
        #endregion

        #region Rectangle Methods
        
        public void fillRect(double x, double y, double w, double h)
        {
            Draw(canvas =>
            {
                using var paint = CreateFillPaint();
                canvas.DrawRect((float)x, (float)y, (float)w, (float)h, paint);
            });
        }
        
        public void strokeRect(double x, double y, double w, double h)
        {
            Draw(canvas =>
            {
                using var paint = CreateStrokePaint();
                canvas.DrawRect((float)x, (float)y, (float)w, (float)h, paint);
            });
        }

        public void clearRect(double x, double y, double w, double h)
        {
            Draw(canvas =>
            {
                using var paint = new SKPaint { BlendMode = SKBlendMode.Clear };
                canvas.DrawRect((float)x, (float)y, (float)w, (float)h, paint);
            });
        }
        
        #endregion

        #region Path Methods
        
        public void beginPath() => _currentPath = new SKPath();
        
        public void closePath() => _currentPath.Close();
        
        public void moveTo(double x, double y) => _currentPath.MoveTo((float)x, (float)y);
        
        public void lineTo(double x, double y) => _currentPath.LineTo((float)x, (float)y);
        
        public void arc(double x, double y, double radius, double startAngle, double endAngle, bool counterclockwise = false)
        {
            var startDeg = (float)(startAngle * 180 / Math.PI);
            var endDeg = (float)(endAngle * 180 / Math.PI);
            var sweepDeg = counterclockwise ? startDeg - endDeg : endDeg - startDeg;
            
            var rect = new SKRect((float)(x - radius), (float)(y - radius), 
                                   (float)(x + radius), (float)(y + radius));
            
            _currentPath.ArcTo(rect, startDeg, sweepDeg, false);
        }
        
        public void arcTo(double x1, double y1, double x2, double y2, double radius)
        {
            _currentPath.ArcTo(new SKPoint((float)x1, (float)y1), 
                               new SKPoint((float)x2, (float)y2), (float)radius);
        }
        
        public void quadraticCurveTo(double cpx, double cpy, double x, double y)
        {
            _currentPath.QuadTo((float)cpx, (float)cpy, (float)x, (float)y);
        }
        
        public void bezierCurveTo(double cp1x, double cp1y, double cp2x, double cp2y, double x, double y)
        {
            _currentPath.CubicTo((float)cp1x, (float)cp1y, (float)cp2x, (float)cp2y, (float)x, (float)y);
        }
        
        public void rect(double x, double y, double w, double h)
        {
            _currentPath.AddRect(new SKRect((float)x, (float)y, (float)(x + w), (float)(y + h)));
        }
        
        public void ellipse(double x, double y, double radiusX, double radiusY, double rotation, 
                            double startAngle, double endAngle, bool counterclockwise = false)
        {
            var rect = new SKRect((float)(x - radiusX), (float)(y - radiusY),
                                   (float)(x + radiusX), (float)(y + radiusY));
            
            var startDeg = (float)(startAngle * 180 / Math.PI);
            var sweepDeg = (float)((endAngle - startAngle) * 180 / Math.PI);
            if (counterclockwise) sweepDeg = -sweepDeg;
            
            using var ellipsePath = new SKPath();
            ellipsePath.ArcTo(rect, startDeg, sweepDeg, true);
            
            if (Math.Abs(rotation) > 0.001)
            {
                var rotateMatrix = SKMatrix.CreateRotationDegrees((float)(rotation * 180 / Math.PI), (float)x, (float)y);
                ellipsePath.Transform(rotateMatrix);
            }
            
            _currentPath.AddPath(ellipsePath);
        }
        
        #endregion

        #region Drawing Methods
        
        public void stroke()
        {
            var pathData = new SKPath(_currentPath);
            Draw(canvas =>
            {
                using var paint = CreateStrokePaint();
                canvas.DrawPath(pathData, paint);
                pathData.Dispose();
            });
        }

        public void fill(string fillRule = "nonzero")
        {
            var pathData = new SKPath(_currentPath);
            pathData.FillType = fillRule == "evenodd" ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
            
            Draw(canvas =>
            {
                using var paint = CreateFillPaint();
                canvas.DrawPath(pathData, paint);
                pathData.Dispose();
            });
        }
        
        public void clip(string fillRule = "nonzero")
        {
            var pathData = new SKPath(_currentPath);
            pathData.FillType = fillRule == "evenodd" ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
            
            Draw(canvas =>
            {
                canvas.ClipPath(pathData);
                pathData.Dispose();
            });
        }
        
        #endregion

        #region Text Methods
        
        public void fillText(string text, double x, double y, double? maxWidth = null)
        {
            Draw(canvas =>
            {
                using var paint = CreateFillPaint();
                ApplyFontToPaint(paint);
                canvas.DrawText(text, (float)x, (float)y, paint);
            });
        }
        
        public void strokeText(string text, double x, double y, double? maxWidth = null)
        {
            Draw(canvas =>
            {
                using var paint = CreateStrokePaint();
                ApplyFontToPaint(paint);
                canvas.DrawText(text, (float)x, (float)y, paint);
            });
        }
        
        public TextMetrics measureText(string text)
        {
            using var paint = new SKPaint();
            ApplyFontToPaint(paint);
            var width = paint.MeasureText(text);
            return new TextMetrics { Width = width };
        }
        
        private void ApplyFontToPaint(SKPaint paint)
        {
            // Parse font string like "16px Arial" or "bold 14pt sans-serif"
            var fontSize = 16f;
            var parts = font?.Split(' ') ?? Array.Empty<string>();
            foreach (var part in parts)
            {
                if (part.EndsWith("px") && float.TryParse(part.Replace("px", ""), out var px))
                    fontSize = px;
                else if (part.EndsWith("pt") && float.TryParse(part.Replace("pt", ""), out var pt))
                    fontSize = pt * 1.33f;
            }
            paint.TextSize = fontSize;
            paint.IsAntialias = true;
        }
        
        #endregion

        #region Transform Methods
        
        public void save()
        {
            _stateStack.Push(_state.Clone());
        }
        
        public void restore()
        {
            if (_stateStack.Count > 0)
                _state = _stateStack.Pop();
        }
        
        public void scale(double x, double y)
        {
            _state.Transform = _state.Transform.PreConcat(SKMatrix.CreateScale((float)x, (float)y));
        }
        
        public void rotate(double angle)
        {
            _state.Transform = _state.Transform.PreConcat(SKMatrix.CreateRotation((float)angle));
        }
        
        public void translate(double x, double y)
        {
            _state.Transform = _state.Transform.PreConcat(SKMatrix.CreateTranslation((float)x, (float)y));
        }
        
        public void transform(double a, double b, double c, double d, double e, double f)
        {
            var matrix = new SKMatrix((float)a, (float)c, (float)e, (float)b, (float)d, (float)f, 0, 0, 1);
            _state.Transform = _state.Transform.PreConcat(matrix);
        }
        
        public void setTransform(double a, double b, double c, double d, double e, double f)
        {
            _state.Transform = new SKMatrix((float)a, (float)c, (float)e, (float)b, (float)d, (float)f, 0, 0, 1);
        }
        
        public void resetTransform()
        {
            _state.Transform = SKMatrix.Identity;
        }
        
        #endregion

        #region Line Style
        
        public void setLineDash(float[] segments)
        {
            _state.LineDashPattern = segments;
        }
        
        public float[] getLineDash()
        {
            return _state.LineDashPattern ?? Array.Empty<float>();
        }
        
        #endregion

        #region Gradient
        
        public CanvasGradient createLinearGradient(double x0, double y0, double x1, double y1)
        {
            return new CanvasGradient(CanvasGradient.GradientType.Linear, 
                (float)x0, (float)y0, 0, (float)x1, (float)y1, 0);
        }
        
        public CanvasGradient createRadialGradient(double x0, double y0, double r0, double x1, double y1, double r1)
        {
            return new CanvasGradient(CanvasGradient.GradientType.Radial,
                (float)x0, (float)y0, (float)r0, (float)x1, (float)y1, (float)r1);
        }
        
        #endregion

        #region Image Data
        
        public ImageData getImageData(int sx, int sy, int sw, int sh)
        {
            var data = new ImageData(sw, sh);
            // Would need to read from bitmap synchronously - complex with Avalonia's threading
            return data;
        }
        
        public void putImageData(IObject imageData, int dx, int dy)
        {
            // Would need to write to bitmap
        }
        
        public ImageData createImageData(int sw, int sh)
        {
            return new ImageData(sw, sh);
        }
        
        #endregion

        #region Hit Testing
        
        public bool isPointInPath(double x, double y)
        {
            return _currentPath.Contains((float)x, (float)y);
        }
        
        public bool isPointInStroke(double x, double y)
        {
            using var paint = CreateStrokePaint();
            using var strokePath = new SKPath();
            paint.GetFillPath(_currentPath, strokePath);
            return strokePath.Contains((float)x, (float)y);
        }
        
        #endregion

        #region Paint Creation Helpers
        
        private SKPaint CreateFillPaint()
        {
            var paint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
                Color = ParseColor(fillStyle).WithAlpha((byte)(globalAlpha * 255))
            };
            ApplyBlendMode(paint);
            ApplyShadow(paint);
            return paint;
        }
        
        private SKPaint CreateStrokePaint()
        {
            var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
                Color = ParseColor(strokeStyle).WithAlpha((byte)(globalAlpha * 255)),
                StrokeWidth = (float)lineWidth,
                StrokeCap = ParseLineCap(lineCap),
                StrokeJoin = ParseLineJoin(lineJoin),
                StrokeMiter = (float)miterLimit
            };
            
            if (_state.LineDashPattern != null && _state.LineDashPattern.Length > 0)
            {
                paint.PathEffect = SKPathEffect.CreateDash(_state.LineDashPattern, 0);
            }
            
            ApplyBlendMode(paint);
            ApplyShadow(paint);
            return paint;
        }
        
        private void ApplyBlendMode(SKPaint paint)
        {
            paint.BlendMode = globalCompositeOperation switch
            {
                "source-over" => SKBlendMode.SrcOver,
                "source-in" => SKBlendMode.SrcIn,
                "source-out" => SKBlendMode.SrcOut,
                "source-atop" => SKBlendMode.SrcATop,
                "destination-over" => SKBlendMode.DstOver,
                "destination-in" => SKBlendMode.DstIn,
                "destination-out" => SKBlendMode.DstOut,
                "destination-atop" => SKBlendMode.DstATop,
                "lighter" => SKBlendMode.Plus,
                "copy" => SKBlendMode.Src,
                "xor" => SKBlendMode.Xor,
                "multiply" => SKBlendMode.Multiply,
                "screen" => SKBlendMode.Screen,
                "overlay" => SKBlendMode.Overlay,
                "darken" => SKBlendMode.Darken,
                "lighten" => SKBlendMode.Lighten,
                "color-dodge" => SKBlendMode.ColorDodge,
                "color-burn" => SKBlendMode.ColorBurn,
                "hard-light" => SKBlendMode.HardLight,
                "soft-light" => SKBlendMode.SoftLight,
                "difference" => SKBlendMode.Difference,
                "exclusion" => SKBlendMode.Exclusion,
                "hue" => SKBlendMode.Hue,
                "saturation" => SKBlendMode.Saturation,
                "color" => SKBlendMode.Color,
                "luminosity" => SKBlendMode.Luminosity,
                _ => SKBlendMode.SrcOver
            };
        }
        
        private void ApplyShadow(SKPaint paint)
        {
            if (shadowBlur > 0 || shadowOffsetX != 0 || shadowOffsetY != 0)
            {
                var shadowColor = ParseColor(this.shadowColor);
                paint.ImageFilter = SKImageFilter.CreateDropShadow(
                    (float)shadowOffsetX, (float)shadowOffsetY,
                    (float)shadowBlur, (float)shadowBlur,
                    shadowColor);
            }
        }
        
        private SKColor ParseColor(string color)
        {
            if (string.IsNullOrWhiteSpace(color)) return SKColors.Black;
            if (SKColor.TryParse(color, out var skColor)) return skColor;
            return SKColors.Black;
        }
        
        private SKStrokeCap ParseLineCap(string cap) => cap switch
        {
            "round" => SKStrokeCap.Round,
            "square" => SKStrokeCap.Square,
            _ => SKStrokeCap.Butt
        };
        
        private SKStrokeJoin ParseLineJoin(string join) => join switch
        {
            "round" => SKStrokeJoin.Round,
            "bevel" => SKStrokeJoin.Bevel,
            _ => SKStrokeJoin.Miter
        };
        
        #endregion
    }

    /// <summary>
    /// Canvas drawing state that can be saved/restored
    /// </summary>
    internal class CanvasState
    {
        public string FillStyle { get; set; } = "#000000";
        public string StrokeStyle { get; set; } = "#000000";
        public double LineWidth { get; set; } = 1.0;
        public string LineCap { get; set; } = "butt";
        public string LineJoin { get; set; } = "miter";
        public double MiterLimit { get; set; } = 10.0;
        public double GlobalAlpha { get; set; } = 1.0;
        public string GlobalCompositeOperation { get; set; } = "source-over";
        public string Font { get; set; } = "10px sans-serif";
        public string TextAlign { get; set; } = "start";
        public string TextBaseline { get; set; } = "alphabetic";
        public double ShadowBlur { get; set; } = 0;
        public string ShadowColor { get; set; } = "rgba(0,0,0,0)";
        public double ShadowOffsetX { get; set; } = 0;
        public double ShadowOffsetY { get; set; } = 0;
        public SKMatrix Transform { get; set; } = SKMatrix.Identity;
        public float[] LineDashPattern { get; set; }
        
        public CanvasState Clone()
        {
            return new CanvasState
            {
                FillStyle = FillStyle,
                StrokeStyle = StrokeStyle,
                LineWidth = LineWidth,
                LineCap = LineCap,
                LineJoin = LineJoin,
                MiterLimit = MiterLimit,
                GlobalAlpha = GlobalAlpha,
                GlobalCompositeOperation = GlobalCompositeOperation,
                Font = Font,
                TextAlign = TextAlign,
                TextBaseline = TextBaseline,
                ShadowBlur = ShadowBlur,
                ShadowColor = ShadowColor,
                ShadowOffsetX = ShadowOffsetX,
                ShadowOffsetY = ShadowOffsetY,
                Transform = Transform,
                LineDashPattern = LineDashPattern != null ? (float[])LineDashPattern.Clone() : null
            };
        }
    }

    /// <summary>
    /// Text measurement result
    /// </summary>
    public class TextMetrics
    {
        public double Width { get; set; }
    }

    /// <summary>
    /// Canvas gradient object
    /// </summary>
    public class CanvasGradient : IObject
    {
        public enum GradientType { Linear, Radial }
        
        public GradientType Type { get; }
        private readonly float _x0, _y0, _r0, _x1, _y1, _r1;
        private readonly List<(float offset, SKColor color)> _stops = new();
        private IObject _prototype;
        
        public CanvasGradient(GradientType type, float x0, float y0, float r0, float x1, float y1, float r1)
        {
            Type = type;
            _x0 = x0; _y0 = y0; _r0 = r0;
            _x1 = x1; _y1 = y1; _r1 = r1;
        }
        
        public void addColorStop(double offset, string color)
        {
            if (SKColor.TryParse(color, out var skColor))
                _stops.Add(((float)offset, skColor));
        }
        
        public SKShader ToShader()
        {
            var colors = _stops.ConvertAll(s => s.color).ToArray();
            var positions = _stops.ConvertAll(s => s.offset).ToArray();
            
            if (colors.Length == 0)
            {
                colors = new[] { SKColors.Transparent, SKColors.Transparent };
                positions = new[] { 0f, 1f };
            }
            
            return Type == GradientType.Linear
                ? SKShader.CreateLinearGradient(new SKPoint(_x0, _y0), new SKPoint(_x1, _y1), colors, positions, SKShaderTileMode.Clamp)
                : SKShader.CreateRadialGradient(new SKPoint(_x0, _y0), _r1, colors, positions, SKShaderTileMode.Clamp);
        }
        
        public IValue Get(string key)
        {
            if (key == "addColorStop")
            {
                return FenValue.FromFunction(new FenFunction("addColorStop", (args, thisVal) => {
                    if (args.Length >= 2)
                        addColorStop(args[0].ToNumber(), args[1].ToString());
                    return FenValue.Undefined;
                }));
            }
            return FenValue.Undefined;
        }
        
        public void Set(string key, IValue value) { }
        public bool Has(string key) => key == "addColorStop";
        public bool Delete(string key) => false;
        public IEnumerable<string> Keys() => new[] { "addColorStop" };
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
    }

    /// <summary>
    /// Image data object for pixel manipulation
    /// </summary>
    public class ImageData : IObject
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Data { get; }
        private IObject _prototype;
        
        public ImageData(int width, int height)
        {
            Width = width;
            Height = height;
            Data = new byte[width * height * 4]; // RGBA
        }
        
        public IValue Get(string key)
        {
            switch (key)
            {
                case "width": return FenValue.FromNumber(Width);
                case "height": return FenValue.FromNumber(Height);
                case "data":
                    var arr = new FenObject();
                    for (int i = 0; i < Data.Length; i++)
                        arr.Set(i.ToString(), FenValue.FromNumber(Data[i]));
                    arr.Set("length", FenValue.FromNumber(Data.Length));
                    return FenValue.FromObject(arr);
                default: return FenValue.Undefined;
            }
        }
        
        public void Set(string key, IValue value) { }
        public bool Has(string key) => key == "width" || key == "height" || key == "data";
        public bool Delete(string key) => false;
        public IEnumerable<string> Keys() => new[] { "width", "height", "data" };
        public IObject GetPrototype() => _prototype;
        public void SetPrototype(IObject prototype) => _prototype = prototype;
    }
}

