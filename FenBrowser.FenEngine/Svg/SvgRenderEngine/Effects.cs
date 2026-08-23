using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        private const int MaxFilterPrimitives = 32;
        private HashSet<string> _activeFilterIds;
        private HashSet<string> _activeMaskIds;

        private void DrawWithEffects(
            SvgElement element,
            SKCanvas canvas,
            ViewportContext viewport,
            Action drawSource)
        {
            string filterRaw = element.GetPresentationProperty("filter");
            string maskRaw = element.GetPresentationProperty("mask");
            bool wantsFilter = HasEffectValue(filterRaw);
            bool wantsMask = HasEffectValue(maskRaw);
            if (!wantsFilter && !wantsMask) { drawSource(); return; }

            string filterId = null;
            string maskId = null;
            var owned = new List<SKImageFilter>();
            SKImageFilter imageFilter = null;
            SvgElement maskElement = null;
            try
            {
                if (wantsFilter &&
                    TryEnterReference(filterRaw, "filter", ref _activeFilterIds, out filterId, out var filterElement))
                {
                    if (!TryBuildFilter(filterElement, owned, out imageFilter)) imageFilter = null;
                }
                if (wantsMask)
                {
                    TryEnterReference(maskRaw, "mask", ref _activeMaskIds, out maskId, out maskElement);
                }

                bool maskLayer = maskElement != null;
                int requiredLayers = (maskLayer ? 2 : 0) + (imageFilter != null ? 1 : 0);
                if (_activeLayers + requiredLayers > _maxActiveLayers)
                {
                    _report.RequireFallback("SVG filter/mask layer budget exceeded");
                    drawSource();
                    return;
                }
                if (maskLayer)
                {
                    canvas.SaveLayer();
                    _activeLayers++;
                }

                try
                {
                    if (imageFilter != null)
                    {
                        using var paint = new SKPaint { ImageFilter = imageFilter };
                        canvas.SaveLayer(paint);
                        _activeLayers++;
                        try { drawSource(); }
                        finally
                        {
                            canvas.Restore();
                            _activeLayers--;
                        }
                    }
                    else
                    {
                        drawSource();
                    }

                    if (maskLayer) ApplyMask(element, maskElement, canvas, viewport);
                }
                finally
                {
                    if (maskLayer)
                    {
                        canvas.Restore();
                        _activeLayers--;
                    }
                }
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i]?.Dispose();
                if (filterId != null) _activeFilterIds.Remove(filterId);
                if (maskId != null) _activeMaskIds.Remove(maskId);
            }
        }

        private bool TryEnterReference(
            string raw,
            string expectedElement,
            ref HashSet<string> active,
            out string id,
            out SvgElement referenced)
        {
            id = null;
            referenced = null;
            if (!TryResolveLocalReference(raw, out string candidate) ||
                !_doc.ElementsById.TryGetValue(candidate, out referenced) ||
                referenced.Name != expectedElement)
            {
                _report.RequireFallback($"SVG {expectedElement} reference is invalid or unresolved");
                referenced = null;
                return false;
            }
            active ??= new HashSet<string>(StringComparer.Ordinal);
            if (active.Contains(candidate) || active.Count >= _maxReferenceDepth)
            {
                _report.RequireFallback($"SVG {expectedElement} cycle or reference-depth budget exceeded");
                referenced = null;
                return false;
            }
            active.Add(candidate);
            id = candidate;
            return true;
        }

        private void ApplyMask(
            SvgElement target,
            SvgElement mask,
            SKCanvas canvas,
            ViewportContext viewport)
        {
            bool objectRegion = !string.Equals(
                mask.GetAttribute("maskUnits"), "userSpaceOnUse", StringComparison.Ordinal);
            bool objectContent = string.Equals(
                mask.GetAttribute("maskContentUnits"), "objectBoundingBox", StringComparison.Ordinal);

            if (!TryResolveObjectBounds(target, viewport, out var bounds))
            {
                _report.RequireFallback($"SVG mask on '{target.Name}' requires resolvable object bounds");
                return;
            }

            SKRect region;
            if (objectRegion)
            {
                float x = ReadMaskFraction(mask, "x", -0.1f);
                float y = ReadMaskFraction(mask, "y", -0.1f);
                float width = ReadMaskFraction(mask, "width", 1.2f);
                float height = ReadMaskFraction(mask, "height", 1.2f);
                region = new SKRect(
                    bounds.Left + x * bounds.Width,
                    bounds.Top + y * bounds.Height,
                    bounds.Left + (x + width) * bounds.Width,
                    bounds.Top + (y + height) * bounds.Height);
            }
            else
            {
                float x = ResolveMaskUserLength(mask.GetAttribute("x"), bounds.Left - bounds.Width * .1f, viewport.Width);
                float y = ResolveMaskUserLength(mask.GetAttribute("y"), bounds.Top - bounds.Height * .1f, viewport.Height);
                float width = ResolveMaskUserLength(mask.GetAttribute("width"), bounds.Width * 1.2f, viewport.Width);
                float height = ResolveMaskUserLength(mask.GetAttribute("height"), bounds.Height * 1.2f, viewport.Height);
                region = new SKRect(x, y, x + width, y + height);
            }

            if (!float.IsFinite(region.Left) || !float.IsFinite(region.Top) ||
                !float.IsFinite(region.Right) || !float.IsFinite(region.Bottom) ||
                region.Width <= 0f || region.Height <= 0f)
            {
                canvas.ClipRect(SKRect.Empty);
                return;
            }

            bool alphaMask = string.Equals(
                mask.GetPresentationProperty("mask-type"), "alpha", StringComparison.OrdinalIgnoreCase);
            using var luma = alphaMask ? null : SKColorFilter.CreateLumaColor();
            using var maskPaint = new SKPaint
            {
                BlendMode = SKBlendMode.DstIn,
                ColorFilter = luma
            };
            canvas.SaveLayer(maskPaint);
            _activeLayers++;
            try
            {
                canvas.ClipRect(region);
                if (objectContent)
                {
                    canvas.Translate(bounds.Left, bounds.Top);
                    canvas.Scale(bounds.Width, bounds.Height);
                }
                var inherited = new InheritedStyle();
                DrawChildren(mask, canvas, objectContent ? new ViewportContext(1f, 1f) : viewport,
                    inherited.ResolveOverrides(mask, _report));
            }
            finally
            {
                canvas.Restore();
                _activeLayers--;
            }
        }

        private static bool HasEffectValue(string raw) =>
            !string.IsNullOrWhiteSpace(raw) &&
            !raw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);

        private static float ReadMaskFraction(SvgElement element, string name, float fallback)
        {
            string raw = element.GetAttribute(name);
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (!SvgValues.TryParseLength(raw.AsSpan(), out float value, out var unit)) return fallback;
            float resolved = unit == SvgValues.SvgUnit.Percent ? value / 100f : value;
            return float.IsFinite(resolved) ? Math.Clamp(resolved, -32767f, 32767f) : fallback;
        }

        private static float ResolveMaskUserLength(string raw, float fallback, float reference)
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                !SvgValues.TryParseLength(raw.AsSpan(), out float value, out var unit)) return fallback;
            float resolved = SvgValues.ResolveUnits(value, unit, DefaultFontSize, reference);
            return float.IsFinite(resolved) ? SvgValues.ClampCoord(resolved) : fallback;
        }

        private bool TryBuildFilter(
            SvgElement filterElement,
            List<SKImageFilter> owned,
            out SKImageFilter current)
        {
            current = null;
            int primitiveCount = 0;
            foreach (var primitive in filterElement.Children)
            {
                CheckDeadline();
                if (++primitiveCount > MaxFilterPrimitives)
                {
                    _report.RequireFallback("SVG filter primitive budget exceeded");
                    return false;
                }

                string inputName = primitive.GetAttribute("in");
                SKImageFilter input;
                if (string.IsNullOrWhiteSpace(inputName)) input = current;
                else if (inputName.Equals("SourceGraphic", StringComparison.Ordinal)) input = null;
                else
                {
                    _report.RequireFallback($"SVG {primitive.Name} named filter input requires compatibility fallback");
                    return false;
                }

                SKImageFilter next = primitive.Name switch
                {
                    "feGaussianBlur" => BuildGaussianBlur(primitive, input),
                    "feOffset" => SKImageFilter.CreateOffset(
                        ReadFiniteNumber(primitive, "dx", 0f, 32767f),
                        ReadFiniteNumber(primitive, "dy", 0f, 32767f),
                        input),
                    "feDropShadow" => BuildDropShadow(primitive, input),
                    "feColorMatrix" => BuildColorMatrix(primitive, input),
                    "feMorphology" => BuildMorphology(primitive, input),
                    _ => null
                };

                if (next == null)
                {
                    _report.RequireFallback($"SVG filter primitive '{primitive.Name}' requires compatibility fallback");
                    return false;
                }
                owned.Add(next);
                current = next;

                if (!string.IsNullOrWhiteSpace(primitive.GetAttribute("result")))
                {
                    _report.RequireFallback("named SVG filter results require compatibility fallback");
                    return false;
                }
            }

            if (primitiveCount == 0)
            {
                _report.RequireFallback("empty SVG filter requires compatibility fallback");
                return false;
            }
            return current != null;
        }

        private SKImageFilter BuildGaussianBlur(SvgElement element, SKImageFilter input)
        {
            if (!TryReadNumberPair(element.GetAttribute("stdDeviation"), 0f, 256f, out float x, out float y))
            {
                return null;
            }
            return SKImageFilter.CreateBlur(x, y, input);
        }

        private SKImageFilter BuildDropShadow(SvgElement element, SKImageFilter input)
        {
            if (!TryReadNumberPair(element.GetAttribute("stdDeviation"), 0f, 256f, out float sx, out float sy))
            {
                return null;
            }
            string colorRaw = element.GetPresentationProperty("flood-color") ?? element.GetAttribute("flood-color");
            if (!SvgValues.TryParseColor((colorRaw ?? "black").AsSpan(), out var color)) color = SKColors.Black;
            float opacity = ReadClampedOpacity(element, "flood-opacity", 1f);
            color = color.WithAlpha((byte)Math.Clamp((int)MathF.Round(color.Alpha * opacity), 0, 255));
            return SKImageFilter.CreateDropShadow(
                ReadFiniteNumber(element, "dx", 0f, 32767f),
                ReadFiniteNumber(element, "dy", 0f, 32767f),
                sx,
                sy,
                color,
                input);
        }

        private SKImageFilter BuildMorphology(SvgElement element, SKImageFilter input)
        {
            if (!TryReadNumberPair(element.GetAttribute("radius"), 0f, 256f, out float x, out float y))
            {
                return null;
            }
            return string.Equals(element.GetAttribute("operator"), "dilate", StringComparison.OrdinalIgnoreCase)
                ? SKImageFilter.CreateDilate(x, y, input)
                : SKImageFilter.CreateErode(x, y, input);
        }

        private SKImageFilter BuildColorMatrix(SvgElement element, SKImageFilter input)
        {
            string type = element.GetAttribute("type") ?? "matrix";
            float[] matrix;
            if (type.Equals("luminanceToAlpha", StringComparison.OrdinalIgnoreCase))
            {
                matrix = new float[]
                {
                    0,0,0,0,0, 0,0,0,0,0, 0,0,0,0,0,
                    .2126f,.7152f,.0722f,0,0
                };
            }
            else if (type.Equals("matrix", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadMatrix(element.GetAttribute("values"), out matrix)) return null;
            }
            else
            {
                return null;
            }

            // SVG matrix offsets are normalized; Skia color-matrix offsets use 0..255 channels.
            matrix[4] *= 255f;
            matrix[9] *= 255f;
            matrix[14] *= 255f;
            matrix[19] *= 255f;
            using var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
            return SKImageFilter.CreateColorFilter(colorFilter, input);
        }

        private static bool TryReadMatrix(string raw, out float[] matrix)
        {
            matrix = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var values = new float[20];
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan());
            int count = 0;
            while (tokenizer.Next(out var token))
            {
                if (count >= values.Length || !SvgValues.TryParseNumber(token, out values[count]) ||
                    !float.IsFinite(values[count])) return false;
                count++;
            }
            if (count != values.Length) return false;
            matrix = values;
            return true;
        }

        private static bool TryReadNumberPair(
            string raw,
            float minimum,
            float maximum,
            out float x,
            out float y)
        {
            x = y = 0f;
            if (string.IsNullOrWhiteSpace(raw)) return true;
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan());
            if (!tokenizer.Next(out var first) || !SvgValues.TryParseNumber(first, out x) ||
                !float.IsFinite(x) || x < minimum || x > maximum) return false;
            y = x;
            if (tokenizer.Next(out var second))
            {
                if (!SvgValues.TryParseNumber(second, out y) || !float.IsFinite(y) || y < minimum || y > maximum ||
                    tokenizer.Next(out _)) return false;
            }
            return true;
        }

        private static float ReadFiniteNumber(SvgElement element, string name, float fallback, float limit)
        {
            return SvgValues.TryParseNumber((element.GetAttribute(name) ?? string.Empty).AsSpan(), out float value) &&
                   float.IsFinite(value)
                ? Math.Clamp(value, -limit, limit)
                : fallback;
        }

        private static bool TryResolveLocalReference(string raw, out string id)
        {
            id = null;
            if (!SvgValues.TryParsePaint(raw.AsSpan(), out var kind, out _, out var fragment, out _) ||
                kind != SvgValues.PaintKind.ServerRef || string.IsNullOrEmpty(fragment))
            {
                return false;
            }
            id = fragment;
            return true;
        }
    }
}
