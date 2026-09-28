using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.Core.Dom.V2;
using SkiaSharp;

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// Backing state for a JS-facing 2D canvas rendering context
    /// (CanvasRenderingContext2D). Drawing happens into an SKBitmap owned by
    /// this instance; the JS property/method dispatch lives in
    /// BrowserScriptEngineRuntime following the existing host-object pattern.
    /// </summary>
    public sealed class FenJsCanvasRenderingContext2DHost
    {
        private const int MaxCanvasDimension = 8192;

        private SKBitmap _bitmap;
        private SKCanvas _canvas;
        private readonly Element _canvasElement;
        private readonly Action _requestRepaint;

        private SKPath _path;
        private readonly Stack<CanvasDrawState> _stateStack;
        private CanvasDrawState _state;
        private (float X, float Y) _currentPoint;

        public FenJsCanvasRenderingContext2DHost(Element canvasElement, Action requestRepaint)
        {
            _canvasElement = canvasElement;
            _requestRepaint = requestRepaint;
            _state = new CanvasDrawState();
            _stateStack = new Stack<CanvasDrawState>();
            _path = new SKPath();
            var width = ReadCanvasAttributeWidth();
            var height = ReadCanvasAttributeHeight();
            AllocateSurface(width, height);
        }

        public Element CanvasElement => _canvasElement;

        public int Width => _bitmap?.Width ?? 0;

        public int Height => _bitmap?.Height ?? 0;

        public SKBitmap Bitmap => _bitmap;

        internal Action RequestRepaint => _requestRepaint;

        private long _version;

        /// <summary>
        /// Bumped by every change to the bitmap, so a capture (canvas.captureStream) can tell
        /// whether the canvas was painted since its last frame without comparing pixels.
        /// </summary>
        public long Version => System.Threading.Interlocked.Read(ref _version);

        private void Changed()
        {
            System.Threading.Interlocked.Increment(ref _version);
            _requestRepaint?.Invoke();
        }

        public void Resize(int width, int height)
        {
            width = Math.Clamp(width, 1, MaxCanvasDimension);
            height = Math.Clamp(height, 1, MaxCanvasDimension);
            if (_bitmap != null && _bitmap.Width == width && _bitmap.Height == height)
            {
                return;
            }

            AllocateSurface(width, height);
            Changed();
        }

        private void AllocateSurface(int width, int height)
        {
            _canvas?.Dispose();
            _bitmap?.Dispose();
            _bitmap = new SKBitmap(width, height);
            _canvas = new SKCanvas(_bitmap);
            _canvas.Clear(SKColors.Transparent);
            _path?.Dispose();
            _path = new SKPath();
        }

        private int ReadCanvasAttributeWidth()
        {
            var raw = _canvasElement?.GetAttribute("width");
            return TryParseDimension(raw, 300);
        }

        private int ReadCanvasAttributeHeight()
        {
            var raw = _canvasElement?.GetAttribute("height");
            return TryParseDimension(raw, 150);
        }

        private static int TryParseDimension(string raw, int fallback)
        {
            if (!string.IsNullOrWhiteSpace(raw) &&
                int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0)
            {
                return Math.Min(parsed, MaxCanvasDimension);
            }

            return fallback;
        }

        // â”€â”€ State handling â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private sealed class CanvasDrawState
        {
            public SKColor FillStyle = SKColors.Black;
            public SKColor StrokeStyle = SKColors.Black;
            public float LineWidth = 1f;
            public float GlobalAlpha = 1f;
            public SKStrokeCap LineCap = SKStrokeCap.Butt;
            public SKStrokeJoin LineJoin = SKStrokeJoin.Miter;
            public float MiterLimit = 10f;
            public string Font = "10px sans-serif";
            public string TextAlign = "start";
            public string TextBaseline = "alphabetic";
            public float[] LineDash = Array.Empty<float>();
            public float LineDashOffset;
            public SKMatrix Transform = SKMatrix.Identity;
            public string FillStyleRaw = "#000000";
            public string StrokeStyleRaw = "#000000";
        }

        // â”€â”€ Property accessors used by the JS dispatch layer â”€â”€â”€â”€â”€â”€â”€â”€

        public string GetFillStyle() => _state.FillStyleRaw;

        public void SetFillStyle(string value)
        {
            if (CanvasColorParser.TryParse(value, out var color))
            {
                _state.FillStyle = color;
                _state.FillStyleRaw = value;
            }
        }

        public string GetStrokeStyle() => _state.StrokeStyleRaw;

        public void SetStrokeStyle(string value)
        {
            if (CanvasColorParser.TryParse(value, out var color))
            {
                _state.StrokeStyle = color;
                _state.StrokeStyleRaw = value;
            }
        }

        public float GetLineWidth() => _state.LineWidth;

        public void SetLineWidth(float value)
        {
            if (value > 0f && float.IsFinite(value))
            {
                _state.LineWidth = value;
            }
        }

        public float GetGlobalAlpha() => _state.GlobalAlpha;

        public void SetGlobalAlpha(float value)
        {
            if (value >= 0f && value <= 1f && float.IsFinite(value))
            {
                _state.GlobalAlpha = value;
            }
        }

        public string GetLineCap() => _state.LineCap switch
        {
            SKStrokeCap.Round => "round",
            SKStrokeCap.Square => "square",
            _ => "butt"
        };

        public void SetLineCap(string value)
        {
            _state.LineCap = value switch
            {
                "round" => SKStrokeCap.Round,
                "square" => SKStrokeCap.Square,
                _ => SKStrokeCap.Butt
            };
        }

        public string GetLineJoin() => _state.LineJoin switch
        {
            SKStrokeJoin.Round => "round",
            SKStrokeJoin.Bevel => "bevel",
            _ => "miter"
        };

        public void SetLineJoin(string value)
        {
            _state.LineJoin = value switch
            {
                "round" => SKStrokeJoin.Round,
                "bevel" => SKStrokeJoin.Bevel,
                _ => SKStrokeJoin.Miter
            };
        }

        public float GetMiterLimit() => _state.MiterLimit;

        public void SetMiterLimit(float value)
        {
            if (value > 0f && float.IsFinite(value))
            {
                _state.MiterLimit = value;
            }
        }

        public string GetFont() => _state.Font;

        public void SetFont(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _state.Font = value;
            }
        }

        public string GetTextAlign() => _state.TextAlign;

        public void SetTextAlign(string value)
        {
            if (value is "start" or "end" or "left" or "right" or "center")
            {
                _state.TextAlign = value;
            }
        }

        public string GetTextBaseline() => _state.TextBaseline;

        public void SetTextBaseline(string value)
        {
            if (value is "top" or "hanging" or "middle" or "alphabetic" or "ideographic" or "bottom")
            {
                _state.TextBaseline = value;
            }
        }

        public float GetLineDashOffset() => _state.LineDashOffset;

        public void SetLineDashOffset(float value)
        {
            if (float.IsFinite(value))
            {
                _state.LineDashOffset = value;
            }
        }

        public float[] GetLineDash() => (float[])_state.LineDash.Clone();

        public void SetLineDash(float[] segments)
        {
            if (segments == null)
            {
                _state.LineDash = Array.Empty<float>();
                return;
            }

            // HTML setLineDash(): a negative or non-finite segment makes the whole call
            // a no-op, rather than being dropped from the list.
            if (Array.Exists(segments, s => s < 0f || !float.IsFinite(s)))
            {
                return;
            }

            var filtered = segments;
            if ((filtered.Length & 1) == 1)
            {
                var doubled = new float[filtered.Length * 2];
                Array.Copy(filtered, 0, doubled, 0, filtered.Length);
                Array.Copy(filtered, 0, doubled, filtered.Length, filtered.Length);
                filtered = doubled;
            }

            _state.LineDash = filtered;
        }

        public bool ImageSmoothingEnabled { get; set; } = true;

        public string GlobalCompositeOperation { get; set; } = "source-over";

        // â”€â”€ Transform â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public void Save()
        {
            _stateStack.Push(CloneState(_state));
        }

        public void Restore()
        {
            if (_stateStack.Count > 0)
            {
                _state = _stateStack.Pop();
            }
        }

        private static CanvasDrawState CloneState(CanvasDrawState state)
        {
            return new CanvasDrawState
            {
                FillStyle = state.FillStyle,
                StrokeStyle = state.StrokeStyle,
                LineWidth = state.LineWidth,
                GlobalAlpha = state.GlobalAlpha,
                LineCap = state.LineCap,
                LineJoin = state.LineJoin,
                MiterLimit = state.MiterLimit,
                Font = state.Font,
                TextAlign = state.TextAlign,
                TextBaseline = state.TextBaseline,
                LineDash = (float[])state.LineDash.Clone(),
                LineDashOffset = state.LineDashOffset,
                Transform = state.Transform,
                FillStyleRaw = state.FillStyleRaw,
                StrokeStyleRaw = state.StrokeStyleRaw
            };
        }

        public void Scale(float x, float y) =>
            _state.Transform = SKMatrix.Concat(_state.Transform, SKMatrix.CreateScale(x, y));

        public void Rotate(float radians) =>
            _state.Transform = SKMatrix.Concat(_state.Transform, SKMatrix.CreateRotation(radians));

        public void Translate(float x, float y) =>
            _state.Transform = SKMatrix.Concat(_state.Transform, SKMatrix.CreateTranslation(x, y));

        public void Transform(float a, float b, float c, float d, float e, float f) =>
            _state.Transform = SKMatrix.Concat(
                _state.Transform,
                new SKMatrix(a, c, e, b, d, f, 0, 0, 1));

        public void SetTransform(float a, float b, float c, float d, float e, float f) =>
            _state.Transform = new SKMatrix(a, c, e, b, d, f, 0, 0, 1);

        public void ResetTransform() => _state.Transform = SKMatrix.Identity;

        // â”€â”€ Path construction â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public void BeginPath()
        {
            _path.Reset();
            _currentPoint = (0f, 0f);
        }

        public void ClosePath()
        {
            _path.Close();
            Changed();
        }

        public void MoveTo(float x, float y)
        {
            ApplyTransformRef(ref x, ref y);
            if (_path.IsEmpty)
            {
                _path.MoveTo(x, y);
            }
            else
            {
                _path.LineTo(x, y);
            }

            _currentPoint = (x, y);
        }

        public void LineTo(float x, float y)
        {
            ApplyTransformRef(ref x, ref y);
            if (_path.IsEmpty)
            {
                _path.MoveTo(x, y);
            }
            else
            {
                _path.LineTo(x, y);
            }

            _currentPoint = (x, y);
        }

        public void Rect(float x, float y, float width, float height)
        {
            ApplyTransformRef(ref x, ref y);
            _path.AddRect(new SKRect(x, y, x + width, y + height));
            Changed();
        }

        public void Arc(float x, float y, float radius, float startAngle, float endAngle, bool counterclockwise = false)
        {
            if (radius <= 0f || !float.IsFinite(radius))
            {
                return;
            }

            ApplyTransformRef(ref x, ref y);
            var sweep = endAngle - startAngle;
            if (!counterclockwise && sweep < 0f)
            {
                sweep += MathF.PI * 2f;
            }
            else if (counterclockwise && sweep > 0f)
            {
                sweep -= MathF.PI * 2f;
            }

            var oval = new SKRect(x - radius, y - radius, x + radius, y + radius);
            _path.ArcTo(oval, startAngle * 180f / MathF.PI, sweep * 180f / MathF.PI, false);
            _currentPoint = (x + radius * MathF.Cos(endAngle), y + radius * MathF.Sin(endAngle));
        }

        public void Ellipse(
            float x,
            float y,
            float radiusX,
            float radiusY,
            float rotation,
            float startAngle,
            float endAngle,
            bool counterclockwise = false)
        {
            if (radiusX <= 0f || radiusY <= 0f)
            {
                return;
            }

            ApplyTransformRef(ref x, ref y);
            var sweep = endAngle - startAngle;
            if (!counterclockwise && sweep < 0f)
            {
                sweep += MathF.PI * 2f;
            }
            else if (counterclockwise && sweep > 0f)
            {
                sweep -= MathF.PI * 2f;
            }

            using var save = new SKAutoCanvasRestore(_canvas);
            _canvas.Save();
            _canvas.Concat(SKMatrix.CreateRotation(rotation, x, y));
            var oval = new SKRect(x - radiusX, y - radiusY, x + radiusX, y + radiusY);
            var temp = new SKPath();
            temp.ArcTo(oval, startAngle * 180f / MathF.PI, sweep * 180f / MathF.PI, false);
            _path.AddPath(temp);
            temp.Dispose();
        }

        public void QuadraticCurveTo(float controlX, float controlY, float x, float y)
        {
            ApplyTransformRef(ref controlX, ref controlY);
            ApplyTransformRef(ref x, ref y);
            _path.QuadTo(controlX, controlY, x, y);
            _currentPoint = (x, y);
        }

        public void BezierCurveTo(float cp1X, float cp1Y, float cp2X, float cp2Y, float x, float y)
        {
            ApplyTransformRef(ref cp1X, ref cp1Y);
            ApplyTransformRef(ref cp2X, ref cp2Y);
            ApplyTransformRef(ref x, ref y);
            _path.CubicTo(cp1X, cp1Y, cp2X, cp2Y, x, y);
            _currentPoint = (x, y);
        }

        public bool IsPointInPath(float x, float y)
        {
            ApplyTransformRef(ref x, ref y);
            return _path.Contains(x, y);
        }

        // â”€â”€ Drawing â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public void Fill(string fillRule = "nonzero")
        {
            if (_path.IsEmpty)
            {
                return;
            }

            using var paint = CreateFillPaint();
            _canvas.DrawPath(_path, paint);
            Changed();
        }

        public void Stroke()
        {
            if (_path.IsEmpty)
            {
                return;
            }

            using var paint = CreateStrokePaint();
            _canvas.DrawPath(_path, paint);
            Changed();
        }

        public void Clip(string fillRule = "nonzero")
        {
            _canvas.ClipPath(_path, SKClipOperation.Intersect, true);
        }

        public void FillRect(float x, float y, float width, float height)
        {
            if (width <= 0f || height <= 0f)
            {
                return;
            }

            ApplyTransformRef(ref x, ref y);
            using var paint = CreateFillPaint();
            _canvas.DrawRect(new SKRect(x, y, x + width, y + height), paint);
            Changed();
        }

        public void StrokeRect(float x, float y, float width, float height)
        {
            ApplyTransformRef(ref x, ref y);
            using var paint = CreateStrokePaint();
            _canvas.DrawRect(new SKRect(x, y, x + width, y + height), paint);
            Changed();
        }

        public void ClearRect(float x, float y, float width, float height)
        {
            ApplyTransformRef(ref x, ref y);
            _canvas.Save();
            _canvas.ResetMatrix();
            _canvas.ClipRect(new SKRect(x, y, x + width, y + height));
            _canvas.Clear(SKColors.Transparent);
            _canvas.Restore();
            Changed();
        }

        public void DrawImageFromBitmap(SKBitmap source, float dx, float dy, float? dWidth, float? dHeight)
        {
            if (source == null || source.Width == 0 || source.Height == 0)
            {
                return;
            }

            var destWidth = dWidth ?? source.Width;
            var destHeight = dHeight ?? source.Height;
            ApplyTransformRef(ref dx, ref dy);
            using var paint = CreateFillPaint();
            _canvas.DrawBitmap(
                source,
                new SKRect(0, 0, source.Width, source.Height),
                new SKRect(dx, dy, dx + destWidth, dy + destHeight),
                paint);
            Changed();
        }

        public (int Width, int Height, byte[] Pixels) GetImageData(int sx, int sy, int sw, int sh)
        {
            sw = Math.Clamp(sw, 1, Math.Max(1, _bitmap.Width));
            sh = Math.Clamp(sh, 1, Math.Max(1, _bitmap.Height));
            sx = Math.Clamp(sx, 0, _bitmap.Width - 1);
            sy = Math.Clamp(sy, 0, _bitmap.Height - 1);
            var pixels = new byte[sw * sh * 4];
            var dstIndex = 0;
            for (var row = sy; row < sy + sh; row++)
            {
                for (var col = sx; col < sx + sw; col++)
                {
                    var pixel = _bitmap.GetPixel(col, row);
                    // Canvas ImageData is RGBA byte order.
                    pixels[dstIndex++] = pixel.Red;
                    pixels[dstIndex++] = pixel.Green;
                    pixels[dstIndex++] = pixel.Blue;
                    pixels[dstIndex++] = pixel.Alpha;
                }
            }

            return (sw, sh, pixels);
        }

        public void PutImageData(byte[] pixels, int width, int height, int dx, int dy)
        {
            if (pixels == null || width <= 0 || height <= 0 || pixels.Length < width * height * 4)
            {
                return;
            }

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var image = SKImage.FromPixelCopy(info, pixels);
            _canvas.Save();
            _canvas.ResetMatrix();
            _canvas.DrawImage(image, dx, dy);
            _canvas.Restore();
            Changed();
        }

        public double MeasureTextWidth(string text)
        {
            using var font = ResolveFont(_state.Font);
            if (font == null)
            {
                return text?.Length * 6d ?? 0d;
            }

            return font.MeasureText(text ?? string.Empty);
        }

        public void FillText(string text, float x, float y, float? maxWidth)
        {
            DrawText(text, x, y, maxWidth, fill: true);
        }

        public void StrokeText(string text, float x, float y, float? maxWidth)
        {
            DrawText(text, x, y, maxWidth, fill: false);
        }

        private void DrawText(string text, float x, float y, float? maxWidth, bool fill)
        {
            using var font = ResolveFont(_state.Font);
            if (font == null)
            {
                return;
            }

            ApplyTransformRef(ref x, ref y);
            using var paint = fill ? CreateFillPaint() : CreateStrokePaint();
            paint.IsAntialias = true;
            var measured = font.MeasureText(text ?? string.Empty);
            var offsetX = _state.TextAlign switch
            {
                "center" => measured / 2f,
                "right" or "end" => measured,
                _ => 0f
            };
            var offsetY = _state.TextBaseline switch
            {
                "middle" => font.Size / 2.6f,
                "top" or "hanging" => font.Size * 0.8f,
                "bottom" or "ideographic" => 0f,
                _ => font.Size * 0.8f
            };
            _canvas.DrawText(text ?? string.Empty, x - offsetX, y - offsetY, SKTextAlign.Left, font, paint);
            Changed();
        }

        private static SKFont ResolveFont(string fontSpec)
        {
            var size = 10f;
            var family = "sans-serif";
            if (!string.IsNullOrWhiteSpace(fontSpec))
            {
                var parts = fontSpec.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (part.EndsWith("px", StringComparison.OrdinalIgnoreCase) &&
                        float.TryParse(part.TrimEnd("px".ToCharArray()), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        size = parsed;
                    }
                    else if (!part.Equals("normal", StringComparison.OrdinalIgnoreCase) &&
                             !part.Equals("bold", StringComparison.OrdinalIgnoreCase) &&
                             !part.Equals("italic", StringComparison.OrdinalIgnoreCase) &&
                             !part.Equals("sans-serif", StringComparison.OrdinalIgnoreCase))
                    {
                        family = part.Trim('\'', '"');
                    }
                }
            }

            var typeface = family.Equals("sans-serif", StringComparison.OrdinalIgnoreCase)
                ? SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
                : SKTypeface.FromFamilyName(family, SKFontStyle.Normal);
            typeface ??= SKTypeface.FromFamilyName(null);
            return new SKFont(typeface, size);
        }

        // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private void ApplyTransformRef(ref float x, ref float y)
        {
            var matrix = _state.Transform;
            if (matrix == SKMatrix.Identity)
            {
                return;
            }

            var point = new SKPoint(x, y);
            var result = matrix.MapPoint(point);
            x = result.X;
            y = result.Y;
        }

        private SKPaint CreateFillPaint()
        {
            return new SKPaint
            {
                Color = WithAlpha(_state.FillStyle, _state.GlobalAlpha),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
        }

        private SKPaint CreateStrokePaint()
        {
            return new SKPaint
            {
                Color = WithAlpha(_state.StrokeStyle, _state.GlobalAlpha),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = _state.LineWidth,
                StrokeCap = _state.LineCap,
                StrokeJoin = _state.LineJoin,
                StrokeMiter = _state.MiterLimit,
                IsAntialias = true,
                PathEffect = _state.LineDash.Length > 0
                    ? SKPathEffect.CreateDash(_state.LineDash, _state.LineDashOffset)
                    : null
            };
        }

        private static SKColor WithAlpha(SKColor color, float alpha)
        {
            if (alpha >= 1f)
            {
                return color;
            }

            return new SKColor(color.Red, color.Green, color.Blue, (byte)Math.Round(color.Alpha * Math.Clamp(alpha, 0f, 1f)));
        }
    }

    /// <summary>
    /// Minimal canvas gradient (addColorStop + linear/radial application).
    /// </summary>
    public sealed class FenJsCanvasGradientHost
    {
        private readonly List<(float Position, SKColor Color)> _stops = new();
        private readonly bool _radial;
        private readonly float _x0, _y0, _r0, _x1, _y1, _r1;

        public FenJsCanvasGradientHost(float x0, float y0, float x1, float y1)
        {
            _radial = false;
            _x0 = x0;
            _y0 = y0;
            _x1 = x1;
            _y1 = y1;
        }

        public FenJsCanvasGradientHost(float x0, float y0, float r0, float x1, float y1, float r1)
        {
            _radial = true;
            _x0 = x0;
            _y0 = y0;
            _r0 = r0;
            _x1 = x1;
            _y1 = y1;
            _r1 = r1;
        }

        public void AddColorStop(float offset, string color)
        {
            if (offset < 0f || offset > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            var parsed = CanvasColorParser.TryParse(color, out var skColor)
                ? skColor
                : SKColors.Black;
            _stops.Add((offset, parsed));
        }

        public SKShader CreateShader()
        {
            if (_stops.Count == 0)
            {
                return SKShader.CreateColor(SKColors.Transparent);
            }

            _stops.Sort((a, b) => a.Position.CompareTo(b.Position));
            var positions = new float[_stops.Count];
            var colors = new SKColor[_stops.Count];
            for (var i = 0; i < _stops.Count; i++)
            {
                positions[i] = _stops[i].Position;
                colors[i] = _stops[i].Color;
            }

            return _radial
                ? SKShader.CreateTwoPointConicalGradient(
                    new SKPoint(_x0, _y0), _r0,
                    new SKPoint(_x1, _y1), _r1,
                    colors, positions, SKShaderTileMode.Clamp)
                : SKShader.CreateLinearGradient(
                    new SKPoint(_x0, _y0),
                    new SKPoint(_x1, _y1),
                    colors,
                    positions,
                    SKShaderTileMode.Clamp);
        }
    }
}

/// <summary>
/// CSS color parsing for canvas fill/stroke styles: hex, rgb()/rgba(),
/// hsl()/hsla(), and the common named colors.
/// </summary>
public static class CanvasColorParser
{
    public static bool TryParse(string value, out SKColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            color = SKColors.Transparent;
            return true;
        }

        if (SKColor.TryParse(text, out color))
        {
            return true;
        }

        var lower = text.ToLowerInvariant();
        var named = lower switch
        {
            "black" => SKColors.Black,
            "white" => SKColors.White,
            "red" => SKColors.Red,
            "green" => SKColors.Green,
            "blue" => SKColors.Blue,
            "yellow" => SKColors.Yellow,
            "orange" => SKColors.Orange,
            "purple" => SKColors.Purple,
            "gray" or "grey" => SKColors.Gray,
            "silver" => SKColors.Silver,
            "maroon" => SKColors.Maroon,
            "navy" => SKColors.Navy,
            "teal" => SKColors.Teal,
            "aqua" or "cyan" => SKColors.Cyan,
            "fuchsia" or "magenta" => SKColors.Magenta,
            "lime" => SKColors.Lime,
            "olive" => SKColors.Olive,
            _ => (SKColor?)null
        };
        if (named.HasValue)
        {
            color = named.Value;
            return true;
        }

        if (lower.StartsWith("rgb(") || lower.StartsWith("rgba("))
        {
            var inner = lower.Substring(lower.IndexOf('(') + 1).TrimEnd(')');
            var parts = inner.Split(',');
            if (parts.Length is 3 or 4)
            {
                if (TryParseChannel(parts[0], out var r, parts[0].Contains('%')) &&
                    TryParseChannel(parts[1], out var g, parts[1].Contains('%')) &&
                    TryParseChannel(parts[2], out var b, parts[2].Contains('%')))
                {
                    var alpha = 1f;
                    if (parts.Length == 4 && !TryParseAlpha(parts[3], out alpha))
                    {
                        return false;
                    }

                    color = new SKColor(r, g, b, (byte)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255f));
                    return true;
                }
            }

            return false;
        }

        if (lower.StartsWith("hsl(") || lower.StartsWith("hsla("))
        {
            var inner = lower.Substring(lower.IndexOf('(') + 1).TrimEnd(')');
            var parts = inner.Split(',');
            if (parts.Length is 3 or 4 &&
                float.TryParse(parts[0].Trim().TrimEnd("deg".ToCharArray()), NumberStyles.Float, CultureInfo.InvariantCulture, out var hue))
            {
                var satText = parts[1].Trim().TrimEnd('%');
                var lightText = parts[2].Trim().TrimEnd('%');
                if (float.TryParse(satText, NumberStyles.Float, CultureInfo.InvariantCulture, out var sat) &&
                    float.TryParse(lightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var light))
                {
                    var alpha = 1f;
                    if (parts.Length == 4 && !TryParseAlpha(parts[3], out alpha))
                    {
                        return false;
                    }

                    color = FromHsl(hue, Math.Clamp(sat, 0f, 100f) / 100f, Math.Clamp(light, 0f, 100f) / 100f, Math.Clamp(alpha, 0f, 1f));
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParseChannel(string text, out byte channel, bool isPercentage)
    {
        var trimmed = text.Trim();
        if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            var scaled = isPercentage ? parsed * 255f / 100f : parsed;
            channel = (byte)Math.Round(Math.Clamp(scaled, 0f, 255f));
            return true;
        }

        channel = 0;
        return false;
    }

    private static bool TryParseAlpha(string text, out float alpha)
    {
        if (float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out alpha) && float.IsFinite(alpha))
        {
            return true;
        }

        alpha = 1f;
        return false;
    }

    private static SKColor FromHsl(float hue, float saturation, float lightness, float alpha)
    {
        hue = ((hue % 360f) + 360f) % 360f;
        var chroma = (1f - Math.Abs(2f * lightness - 1f)) * saturation;
        var huePrime = hue / 60f;
        var secondary = chroma * (1f - Math.Abs(huePrime % 2f - 1f));
        float r, g, b;
        (r, g, b) = huePrime switch
        {
            < 1f => (chroma, secondary, 0f),
            < 2f => (secondary, chroma, 0f),
            < 3f => (0f, chroma, secondary),
            < 4f => (0f, secondary, chroma),
            < 5f => (secondary, 0f, chroma),
            _ => (chroma, 0f, secondary)
        };
        var match = lightness - chroma / 2f;
        return new SKColor(
            (byte)Math.Round((r + match) * 255f),
            (byte)Math.Round((g + match) * 255f),
            (byte)Math.Round((b + match) * 255f),
            (byte)Math.Round(alpha * 255f));
    }
}
