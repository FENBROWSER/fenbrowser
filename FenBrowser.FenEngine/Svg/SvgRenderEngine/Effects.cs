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
            bool blendLayer = TryResolveBlendLayer(element, out SKBlendMode blendMode);
            if (!wantsFilter && !wantsMask && !blendLayer) { drawSource(); return; }

            string filterId = null;
            string maskId = null;
            var owned = new List<SKImageFilter>();
            SKImageFilter imageFilter = null;
            SvgElement maskElement = null;
            SKPaint blendPaint = null;
            try
            {
                if (wantsFilter &&
                    TryEnterReference(filterRaw, "filter", ref _activeFilterIds, out filterId, out var filterElement))
                {
                    if (!TryBuildFilter(filterElement, element, viewport, owned, out imageFilter))
                        imageFilter = null;
                }
                if (wantsMask)
                {
                    TryEnterReference(maskRaw, "mask", ref _activeMaskIds, out maskId, out maskElement);
                }

                bool maskLayer = maskElement != null;
                int requiredLayers = (maskLayer ? 2 : 0) + (imageFilter != null ? 1 : 0) + (blendLayer ? 1 : 0);
                if (_activeLayers + requiredLayers > _maxActiveLayers)
                {
                    _report.RequireFallback("SVG filter/mask/blend layer budget exceeded");
                    drawSource();
                    return;
                }
                if (blendLayer)
                {
                    blendPaint = new SKPaint { BlendMode = blendMode };
                    canvas.SaveLayer(blendPaint);
                    _activeLayers++;
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
                    if (blendLayer)
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
                blendPaint?.Dispose();
            }
        }

        private bool TryResolveBlendLayer(SvgElement element, out SKBlendMode mode)
        {
            mode = SKBlendMode.SrcOver;
            string isolation = element.GetPresentationProperty("isolation");
            bool isolated = string.Equals(isolation?.Trim(), "isolate", StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(isolation) && !isolated &&
                !isolation.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
                _report.RequireFallback("unsupported SVG isolation value requires compatibility fallback");

            string raw = element.GetPresentationProperty("mix-blend-mode");
            if (string.IsNullOrWhiteSpace(raw) || raw.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase))
                return isolated;
            mode = raw.Trim().ToLowerInvariant() switch
            {
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
            if (mode == SKBlendMode.SrcOver)
            {
                _report.RequireFallback($"SVG mix-blend-mode '{raw.Trim()}' requires compatibility fallback");
                return isolated;
            }
            return true;
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

            var bounds = new SKRect(0f, 0f, viewport.Width, viewport.Height);
            if ((objectRegion || objectContent) &&
                !TryResolveObjectBounds(target, viewport, out bounds))
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
            SvgElement target,
            ViewportContext viewport,
            List<SKImageFilter> owned,
            out SKImageFilter current)
        {
            current = null;
            int primitiveCount = 0;
            float primitiveScaleX = 1f;
            float primitiveScaleY = 1f;
            string primitiveUnits = filterElement.GetAttribute("primitiveUnits");
            if (string.Equals(primitiveUnits, "objectBoundingBox", StringComparison.Ordinal))
            {
                if (!TryResolveObjectBounds(target, viewport, out var bounds))
                {
                    _report.RequireFallback(
                        $"SVG filter on '{target.Name}' requires resolvable object bounds");
                    return false;
                }
                primitiveScaleX = bounds.Width;
                primitiveScaleY = bounds.Height;
            }
            else if (!string.IsNullOrWhiteSpace(primitiveUnits) &&
                     !string.Equals(primitiveUnits, "userSpaceOnUse", StringComparison.Ordinal))
            {
                _report.RequireFallback(
                    $"SVG primitiveUnits '{primitiveUnits}' requires compatibility fallback");
                return false;
            }

            var results = new Dictionary<string, SKImageFilter>(StringComparer.Ordinal);
            foreach (var primitive in filterElement.Children)
            {
                CheckDeadline();
                if (primitive.Name is "title" or "desc" or "metadata")
                {
                    continue;
                }
                if (++primitiveCount > MaxFilterPrimitives)
                {
                    _report.RequireFallback("SVG filter primitive budget exceeded");
                    return false;
                }

                SKImageFilter next;
                if (primitive.Name == "feFlood")
                {
                    next = BuildFlood(filterElement, primitive, target, viewport);
                }
                else if (primitive.Name == "feBlend")
                {
                    if (!TryResolveFilterInput(
                            primitive.GetAttribute("in"), current, results, owned,
                            primitive.Name, out var input) ||
                        !TryResolveFilterInput(
                            primitive.GetAttribute("in2") ?? "SourceGraphic",
                            current, results, owned, primitive.Name, out var input2))
                        return false;
                    next = BuildBlend(primitive, input, input2);
                }
                else if (primitive.Name == "feComposite")
                {
                    if (!TryResolveFilterInput(
                            primitive.GetAttribute("in"), current, results, owned,
                            primitive.Name, out var input) ||
                        !TryResolveFilterInput(
                            primitive.GetAttribute("in2") ?? "SourceGraphic",
                            current, results, owned, primitive.Name, out var input2))
                        return false;
                    next = BuildComposite(primitive, input, input2);
                }
                else if (primitive.Name == "feMerge")
                {
                    next = BuildMerge(primitive, current, results, owned);
                }
                else
                {
                    if (!TryResolveFilterInput(
                            primitive.GetAttribute("in"), current, results, owned,
                            primitive.Name, out var input))
                        return false;
                    next = primitive.Name switch
                    {
                        "feGaussianBlur" => BuildGaussianBlur(
                            primitive, input, primitiveScaleX, primitiveScaleY),
                        "feOffset" => SKImageFilter.CreateOffset(
                            ReadScaledFiniteNumber(
                                primitive, "dx", primitiveScaleX, 32767f),
                            ReadScaledFiniteNumber(
                                primitive, "dy", primitiveScaleY, 32767f),
                            input),
                        "feDropShadow" => BuildDropShadow(
                            primitive, input, primitiveScaleX, primitiveScaleY),
                        "feColorMatrix" => BuildColorMatrix(primitive, input),
                        "feMorphology" => BuildMorphology(
                            primitive, input, primitiveScaleX, primitiveScaleY),
                        _ => null
                    };
                }

                if (next == null)
                {
                    _report.RequireFallback($"SVG filter primitive '{primitive.Name}' requires compatibility fallback");
                    return false;
                }
                owned.Add(next);
                current = next;

                string resultName = primitive.GetAttribute("result")?.Trim();
                if (!string.IsNullOrEmpty(resultName))
                {
                    results[resultName] = next;
                }
            }

            if (primitiveCount == 0)
            {
                _report.RequireFallback("empty SVG filter requires compatibility fallback");
                return false;
            }
            return current != null;
        }

        private bool TryResolveFilterInput(
            string raw,
            SKImageFilter current,
            Dictionary<string, SKImageFilter> results,
            List<SKImageFilter> owned,
            string primitiveName,
            out SKImageFilter input)
        {
            input = current;
            string name = raw?.Trim();
            if (string.IsNullOrEmpty(name)) return true;
            if (name.Equals("SourceGraphic", StringComparison.Ordinal))
            {
                input = null;
                return true;
            }
            if (name.Equals("SourceAlpha", StringComparison.Ordinal))
            {
                input = BuildSourceAlpha();
                if (input == null) return false;
                owned.Add(input);
                return true;
            }
            if (results.TryGetValue(name, out input)) return true;

            _report.RequireFallback(
                $"SVG {primitiveName} filter input '{name}' requires compatibility fallback");
            input = null;
            return false;
        }

        private static SKImageFilter BuildSourceAlpha()
        {
            var matrix = new float[]
            {
                0,0,0,0,0,
                0,0,0,0,0,
                0,0,0,0,0,
                0,0,0,1,0
            };
            using var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
            return SKImageFilter.CreateColorFilter(colorFilter);
        }

        private SKImageFilter BuildFlood(
            SvgElement filter,
            SvgElement flood,
            SvgElement target,
            ViewportContext viewport)
        {
            if (!TryResolveFilterRegion(filter, target, viewport, out var filterRegion) ||
                !TryResolvePrimitiveRegion(
                    filter, flood, target, viewport, filterRegion, out var primitiveRegion))
                return null;

            string raw = flood.GetPresentationProperty("flood-color");
            if (!SvgValues.TryParseColor((raw ?? "black").AsSpan(), out var color))
                color = SKColors.Black;
            float opacity = ReadClampedOpacity(flood, "flood-opacity", 1f);
            color = color.WithAlpha((byte)Math.Clamp(
                (int)MathF.Round(color.Alpha * opacity), 0, 255));

            using var shader = SKShader.CreateColor(color);
            return SKImageFilter.CreateShader(shader, false, primitiveRegion);
        }

        private SKImageFilter BuildBlend(
            SvgElement element,
            SKImageFilter input,
            SKImageFilter input2)
        {
            SKBlendMode mode = (element.GetAttribute("mode") ?? "normal").Trim().ToLowerInvariant() switch
            {
                "normal" => SKBlendMode.SrcOver,
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
                _ => (SKBlendMode)(-1)
            };
            if ((int)mode < 0) return null;
            return SKImageFilter.CreateBlendMode(mode, input2, input);
        }

        private SKImageFilter BuildComposite(
            SvgElement element,
            SKImageFilter input,
            SKImageFilter input2)
        {
            SKBlendMode mode = (element.GetAttribute("operator") ?? "over").Trim().ToLowerInvariant() switch
            {
                "over" => SKBlendMode.SrcOver,
                "in" => SKBlendMode.SrcIn,
                "out" => SKBlendMode.SrcOut,
                "atop" => SKBlendMode.SrcATop,
                "xor" => SKBlendMode.Xor,
                _ => (SKBlendMode)(-1)
            };
            if ((int)mode < 0) return null;
            return SKImageFilter.CreateBlendMode(mode, input2, input);
        }

        private SKImageFilter BuildMerge(
            SvgElement merge,
            SKImageFilter current,
            Dictionary<string, SKImageFilter> results,
            List<SKImageFilter> owned)
        {
            var inputs = new List<SKImageFilter>();
            foreach (var node in merge.Children)
            {
                if (node.Name is "title" or "desc" or "metadata") continue;
                if (node.Name != "feMergeNode" ||
                    !TryResolveFilterInput(
                        node.GetAttribute("in"), current, results, owned,
                        node.Name, out var input))
                    return null;

                // The merge API needs an explicit filter object for SourceGraphic.
                if (input == null)
                {
                    input = SKImageFilter.CreateOffset(0f, 0f);
                    if (input == null) return null;
                    owned.Add(input);
                }
                inputs.Add(input);
            }
            return inputs.Count == 0 ? null : SKImageFilter.CreateMerge(inputs.ToArray());
        }

        private bool TryResolveFilterRegion(
            SvgElement filter,
            SvgElement target,
            ViewportContext viewport,
            out SKRect region)
        {
            bool objectUnits = !string.Equals(
                filter.GetAttribute("filterUnits"), "userSpaceOnUse", StringComparison.Ordinal);
            if (objectUnits)
            {
                if (!TryResolveObjectBounds(target, viewport, out var bounds))
                {
                    region = default;
                    return false;
                }
                float x = ReadMaskFraction(filter, "x", -.1f);
                float y = ReadMaskFraction(filter, "y", -.1f);
                float width = ReadMaskFraction(filter, "width", 1.2f);
                float height = ReadMaskFraction(filter, "height", 1.2f);
                region = new SKRect(
                    bounds.Left + x * bounds.Width,
                    bounds.Top + y * bounds.Height,
                    bounds.Left + (x + width) * bounds.Width,
                    bounds.Top + (y + height) * bounds.Height);
            }
            else
            {
                float x = ResolveMaskUserLength(filter.GetAttribute("x"),
                    -viewport.Width * .1f, viewport.Width);
                float y = ResolveMaskUserLength(filter.GetAttribute("y"),
                    -viewport.Height * .1f, viewport.Height);
                float width = ResolveMaskUserLength(filter.GetAttribute("width"),
                    viewport.Width * 1.2f, viewport.Width);
                float height = ResolveMaskUserLength(filter.GetAttribute("height"),
                    viewport.Height * 1.2f, viewport.Height);
                region = new SKRect(x, y, x + width, y + height);
            }
            return IsUsableFilterRegion(region);
        }

        private bool TryResolvePrimitiveRegion(
            SvgElement filter,
            SvgElement primitive,
            SvgElement target,
            ViewportContext viewport,
            SKRect fallback,
            out SKRect region)
        {
            if (primitive.GetAttribute("x") == null && primitive.GetAttribute("y") == null &&
                primitive.GetAttribute("width") == null && primitive.GetAttribute("height") == null)
            {
                region = fallback;
                return true;
            }

            bool objectUnits = string.Equals(
                filter.GetAttribute("primitiveUnits"), "objectBoundingBox", StringComparison.Ordinal);
            if (objectUnits)
            {
                if (!TryResolveObjectBounds(target, viewport, out var bounds))
                {
                    region = default;
                    return false;
                }
                float x = ReadMaskFraction(primitive, "x", (fallback.Left - bounds.Left) / bounds.Width);
                float y = ReadMaskFraction(primitive, "y", (fallback.Top - bounds.Top) / bounds.Height);
                float width = ReadMaskFraction(primitive, "width", fallback.Width / bounds.Width);
                float height = ReadMaskFraction(primitive, "height", fallback.Height / bounds.Height);
                region = new SKRect(
                    bounds.Left + x * bounds.Width,
                    bounds.Top + y * bounds.Height,
                    bounds.Left + (x + width) * bounds.Width,
                    bounds.Top + (y + height) * bounds.Height);
            }
            else
            {
                float x = ResolveMaskUserLength(
                    primitive.GetAttribute("x"), fallback.Left, viewport.Width);
                float y = ResolveMaskUserLength(
                    primitive.GetAttribute("y"), fallback.Top, viewport.Height);
                float width = ResolveMaskUserLength(
                    primitive.GetAttribute("width"), fallback.Width, viewport.Width);
                float height = ResolveMaskUserLength(
                    primitive.GetAttribute("height"), fallback.Height, viewport.Height);
                region = new SKRect(x, y, x + width, y + height);
            }
            return IsUsableFilterRegion(region);
        }

        private static bool IsUsableFilterRegion(SKRect region) =>
            float.IsFinite(region.Left) && float.IsFinite(region.Top) &&
            float.IsFinite(region.Right) && float.IsFinite(region.Bottom) &&
            region.Width > 0f && region.Height > 0f;

        private SKImageFilter BuildGaussianBlur(
            SvgElement element,
            SKImageFilter input,
            float scaleX,
            float scaleY)
        {
            if (!TryReadScaledNumberPair(
                    element.GetAttribute("stdDeviation"), scaleX, scaleY, 256f,
                    out float x, out float y))
            {
                return null;
            }
            return SKImageFilter.CreateBlur(x, y, input);
        }

        private SKImageFilter BuildDropShadow(
            SvgElement element,
            SKImageFilter input,
            float scaleX,
            float scaleY)
        {
            if (!TryReadScaledNumberPair(
                    element.GetAttribute("stdDeviation"), scaleX, scaleY, 256f,
                    out float sx, out float sy))
            {
                return null;
            }
            string colorRaw = element.GetPresentationProperty("flood-color") ?? element.GetAttribute("flood-color");
            if (!SvgValues.TryParseColor((colorRaw ?? "black").AsSpan(), out var color)) color = SKColors.Black;
            float opacity = ReadClampedOpacity(element, "flood-opacity", 1f);
            color = color.WithAlpha((byte)Math.Clamp((int)MathF.Round(color.Alpha * opacity), 0, 255));
            return SKImageFilter.CreateDropShadow(
                ReadScaledFiniteNumber(element, "dx", scaleX, 32767f),
                ReadScaledFiniteNumber(element, "dy", scaleY, 32767f),
                sx,
                sy,
                color,
                input);
        }

        private SKImageFilter BuildMorphology(
            SvgElement element,
            SKImageFilter input,
            float scaleX,
            float scaleY)
        {
            if (!TryReadScaledNumberPair(
                    element.GetAttribute("radius"), scaleX, scaleY, 256f,
                    out float x, out float y))
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
            else if (type.Equals("saturate", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadSingleNumber(element.GetAttribute("values"), 1f, out float saturation) ||
                    saturation < 0f)
                    return null;
                matrix = new float[]
                {
                    .213f + .787f * saturation, .715f - .715f * saturation, .072f - .072f * saturation, 0, 0,
                    .213f - .213f * saturation, .715f + .285f * saturation, .072f - .072f * saturation, 0, 0,
                    .213f - .213f * saturation, .715f - .715f * saturation, .072f + .928f * saturation, 0, 0,
                    0, 0, 0, 1, 0
                };
            }
            else if (type.Equals("hueRotate", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadSingleNumber(element.GetAttribute("values"), 0f, out float angle))
                    return null;
                float radians = angle * MathF.PI / 180f;
                float cosine = MathF.Cos(radians);
                float sine = MathF.Sin(radians);
                matrix = new float[]
                {
                    .213f + .787f * cosine - .213f * sine, .715f - .715f * cosine - .715f * sine, .072f - .072f * cosine + .928f * sine, 0, 0,
                    .213f - .213f * cosine + .143f * sine, .715f + .285f * cosine + .140f * sine, .072f - .072f * cosine - .283f * sine, 0, 0,
                    .213f - .213f * cosine - .787f * sine, .715f - .715f * cosine + .715f * sine, .072f + .928f * cosine + .072f * sine, 0, 0,
                    0, 0, 0, 1, 0
                };
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

        private static bool TryReadSingleNumber(string raw, float fallback, out float value)
        {
            value = fallback;
            if (string.IsNullOrWhiteSpace(raw)) return true;
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan());
            return tokenizer.Next(out var token) &&
                   SvgValues.TryParseNumber(token, out value) &&
                   float.IsFinite(value) &&
                   !tokenizer.Next(out _);
        }

        private static bool TryReadScaledNumberPair(
            string raw,
            float scaleX,
            float scaleY,
            float maximum,
            out float x,
            out float y)
        {
            if (!TryReadNumberPair(raw, 0f, 32767f, out x, out y)) return false;
            x *= scaleX;
            y *= scaleY;
            return float.IsFinite(x) && float.IsFinite(y) &&
                   x >= 0f && y >= 0f && x <= maximum && y <= maximum;
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

        private static float ReadScaledFiniteNumber(
            SvgElement element,
            string name,
            float scale,
            float limit)
        {
            float value = ReadFiniteNumber(element, name, 0f, 32767f) * scale;
            return float.IsFinite(value) ? Math.Clamp(value, -limit, limit) : 0f;
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
