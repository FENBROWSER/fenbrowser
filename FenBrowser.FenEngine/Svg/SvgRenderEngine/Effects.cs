using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        private const int MaxFilterPrimitives = 32;
        private const int MaxTileRepeats = 64;
        private const int MaxFilterTargetStyles = 128;
        private HashSet<string> _activeFilterIds;
        private HashSet<string> _activeMaskIds;
        private Dictionary<SvgElement, InheritedStyle> _filterTargetStyles;

        private sealed class ResolvedFilter
        {
            public SvgElement ContentOwner;
            public bool HasPrimitives;
            public string X;
            public string Y;
            public string Width;
            public string Height;
            public string FilterUnits;
            public string PrimitiveUnits;
            public string ColorInterpolationFilters;
        }

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
            SvgElement filterElement = null;
            SvgElement maskElement = null;
            SKPaint blendPaint = null;
            try
            {
                if (wantsFilter)
                {
                    TryEnterFilterReference(
                        filterRaw, ref _activeFilterIds, out filterId, out filterElement);
                }
                if (wantsMask)
                {
                    TryEnterMaskReference(maskRaw, ref _activeMaskIds, out maskId, out maskElement);
                }

                bool maskLayer = maskElement != null;
                int sourceLayers = EstimateOpacityLayerDepth(element, 0);
                int requestedLayers =
                    (maskLayer ? 2 : 0) +
                    (filterElement != null ? 1 : 0) +
                    (blendLayer ? 1 : 0) +
                    sourceLayers;
                if (_activeLayers + requestedLayers > _maxActiveLayers)
                {
                    _report.RequireFallback("SVG filter/mask/blend layer budget exceeded");
                    drawSource();
                    return;
                }

                if (filterElement != null &&
                    !TryBuildFilter(filterElement, element, viewport, owned, out imageFilter))
                {
                    imageFilter = null;
                }

                int requiredLayers =
                    (maskLayer ? 2 : 0) +
                    (imageFilter != null ? 1 : 0) +
                    (blendLayer ? 1 : 0) +
                    sourceLayers;
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

        private int EstimateOpacityLayerDepth(SvgElement element, int depth)
        {
            CheckDeadline();
            if (element == null || depth > MaxRenderDepth)
            {
                return depth > MaxRenderDepth ? _maxActiveLayers + 1 : 0;
            }
            if (IsDisplayNone(element) || !PassesRequiredExtensions(element) ||
                string.Equals(element.GetPresentationProperty("visibility"), "hidden", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(element.GetPresentationProperty("visibility"), "collapse", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            int ownLayer = ReadClampedOpacity(element, "opacity", 1f) < 1f ? 1 : 0;
            int childLayers = 0;
            for (int i = 0; i < element.Children.Count; i++)
            {
                if ((i & 0xF) == 0) CheckDeadline();
                var child = element.Children[i];
                if (child.Name is "defs" or "style" or "title" or "desc" or "metadata" or
                    "link" or "meta" or "script" or "symbol" or "marker" or "pattern" or
                    "linearGradient" or "radialGradient" or "stop" or "filter" or
                    "mask" or "clipPath")
                {
                    continue;
                }
                childLayers = Math.Max(
                    childLayers,
                    EstimateOpacityLayerDepth(child, depth + 1));
            }
            return ownLayer + childLayers;
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
            if (!TryResolveLocalReference(raw, out string candidate))
            {
                if (IsExternalEffectReference(raw))
                    _report.RejectResource($"SVG {expectedElement} external reference rejected");
                else
                    _report.RequireFallback($"SVG {expectedElement} reference is invalid or unresolved");
                return false;
            }
            if (!_doc.ElementsById.TryGetValue(candidate, out referenced) ||
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

        private void TryEnterFilterReference(
            string raw,
            ref HashSet<string> active,
            out string id,
            out SvgElement referenced)
        {
            id = null;
            referenced = null;
            if (IsUnresolvedLocalReference(raw, "filter")) return;
            TryEnterReference(raw, "filter", ref active, out id, out referenced);
        }

        /// <summary>
        /// A 'mask' property that is a local IRI naming nothing, or naming something
        /// that is not a 'mask', is an unresolved reference rather than an unsupported
        /// feature: browsers ignore the property and paint the element unmasked, so
        /// this leaves the target null and reports nothing. Only the external-URL
        /// rejection and the cycle/reference-depth budget of
        /// <see cref="TryEnterReference"/> still apply. Kept separate from
        /// <see cref="TryEnterFilterReference"/> because an unresolved 'filter'
        /// reference is established separately and must not be changed by a mask rule.
        /// </summary>
        private void TryEnterMaskReference(
            string raw,
            ref HashSet<string> active,
            out string id,
            out SvgElement referenced)
        {
            id = null;
            referenced = null;
            if (IsUnresolvedLocalReference(raw, "mask")) return;
            TryEnterReference(raw, "mask", ref active, out id, out referenced);
        }

        private bool IsUnresolvedLocalReference(string raw, string expectedElement) =>
            TryResolveLocalReference(raw, out string candidate) &&
            (!_doc.ElementsById.TryGetValue(candidate, out SvgElement target) ||
             target.Name != expectedElement);

        private static bool IsExternalEffectReference(string raw)
        {
            string value = raw?.Trim();
            return !string.IsNullOrEmpty(value) &&
                value.StartsWith("url(", StringComparison.OrdinalIgnoreCase) &&
                value.IndexOf('#', StringComparison.Ordinal) < 0;
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

        private bool TryResolveFilterColor(
            SvgElement element,
            ResolvedFilter filter,
            SvgElement target,
            string property,
            SKColor defaultColor,
            out SKColor color)
        {
            string raw = FindInheritedFilterProperty(element, property);
            if (raw == null && filter?.ContentOwner != null)
                raw = FindInheritedFilterProperty(filter.ContentOwner, property);
            if (string.IsNullOrWhiteSpace(raw))
            {
                color = defaultColor;
                return true;
            }
            if (raw.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
                return TryResolveCurrentColor(element, target, out color);
            if (!SvgValues.TryParseColor(raw.AsSpan(), out color))
            {
                _report.RequireFallback(
                    $"SVG filter {property} '{raw}' requires compatibility fallback");
                return false;
            }
            return true;
        }

        private static string FindInheritedFilterProperty(SvgElement element, string property)
        {
            for (SvgElement current = element; current != null; current = current.Parent)
            {
                string raw = current.GetPresentationProperty(property);
                if (string.IsNullOrWhiteSpace(raw) ||
                    raw.Equals("inherit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                return raw;
            }
            return null;
        }

        private static bool TryResolveCurrentColor(
            SvgElement context,
            SvgElement target,
            out SKColor color)
        {
            for (SvgElement current = context; current != null; current = current.Parent)
            {
                string raw = current.GetPresentationProperty("color");
                if (string.IsNullOrWhiteSpace(raw) ||
                    raw.Equals("currentColor", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("inherit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (SvgValues.TryParseColor(raw.AsSpan(), out color)) return true;
            }
            for (SvgElement current = target; current != null; current = current.Parent)
            {
                string raw = current.GetPresentationProperty("color");
                if (string.IsNullOrWhiteSpace(raw) ||
                    raw.Equals("currentColor", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("inherit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (SvgValues.TryParseColor(raw.AsSpan(), out color)) return true;
            }
            color = SKColors.Black;
            return true;
        }

        private static float ReadInheritedOpacity(
            SvgElement element,
            ResolvedFilter filter,
            string property,
            float fallback)
        {
            string raw = FindInheritedFilterProperty(element, property) ??
                (filter?.ContentOwner == null ? null : FindInheritedFilterProperty(filter.ContentOwner, property));
            if (string.IsNullOrWhiteSpace(raw) ||
                !SvgValues.TryParseNumber(raw.AsSpan(), out float value) ||
                !SvgValues.IsFinite(value))
            {
                return fallback;
            }
            return Math.Clamp(value, 0f, 1f);
        }

        private static bool HasEffectValue(string raw) =>
            !string.IsNullOrWhiteSpace(raw) &&
            !raw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);

        private static float ReadMaskFraction(SvgElement element, string name, float fallback) =>
            ReadMaskFraction(element.GetAttribute(name), fallback);

        private static float ReadMaskFraction(string raw, float fallback)
        {
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

        private bool TryResolveFilterTemplate(
            SvgElement server,
            out ResolvedFilter resolved)
        {
            resolved = new ResolvedFilter { ContentOwner = server };
            var visited = new HashSet<SvgElement>();
            SvgElement current = server;
            while (current != null)
            {
                CheckDeadline();
                if (current.Name != "filter" ||
                    !visited.Add(current) ||
                    visited.Count > _maxReferenceDepth)
                {
                    _report.RequireFallback("SVG filter template reference requires compatibility fallback");
                    return false;
                }

                resolved.X ??= current.GetAttribute("x");
                resolved.Y ??= current.GetAttribute("y");
                resolved.Width ??= current.GetAttribute("width");
                resolved.Height ??= current.GetAttribute("height");
                resolved.FilterUnits ??= current.GetAttribute("filterUnits");
                resolved.PrimitiveUnits ??= current.GetAttribute("primitiveUnits");
                resolved.ColorInterpolationFilters ??=
                    current.GetPresentationProperty("color-interpolation-filters");

                if (!resolved.HasPrimitives && HasRenderableFilterContent(current))
                {
                    resolved.ContentOwner = current;
                    resolved.HasPrimitives = true;
                }

                string href = current.GetAttribute("href") ?? current.GetLookup("xlink:href");
                if (string.IsNullOrWhiteSpace(href))
                {
                    break;
                }
                if (!SvgValues.TryParseLocalReference(href, out string id))
                {
                    _report.RejectResource("SVG filter external template reference rejected");
                    return false;
                }
                if (!_doc.ElementsById.TryGetValue(id, out var template) ||
                    template.Name != "filter")
                {
                    _report.RequireFallback("SVG filter template reference is invalid or unsupported");
                    return false;
                }
                current = template;
            }
            return true;
        }

        private static bool HasRenderableFilterContent(SvgElement filter)
        {
            for (int i = 0; i < filter.Children.Count; i++)
            {
                if (filter.Children[i].Name is not ("title" or "desc" or "metadata"))
                {
                    return true;
                }
            }
            return false;
        }

        private bool TryBuildFilter(
            SvgElement filterElement,
            SvgElement target,
            ViewportContext viewport,
            List<SKImageFilter> owned,
            out SKImageFilter current)
        {
            current = null;
            if (!TryResolveFilterTemplate(filterElement, out var filterTemplate))
            {
                _report.RequireFallback("invalid SVG filter template requires compatibility fallback");
                return false;
            }

            int primitiveCount = 0;
            float primitiveScaleX = 1f;
            float primitiveScaleY = 1f;
            float primitiveScaleZ = 1f;
            string primitiveUnits = filterTemplate.PrimitiveUnits;
            if (string.Equals(primitiveUnits, "objectBoundingBox", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveObjectBounds(target, viewport, out var bounds))
                {
                    _report.RequireFallback(
                        $"SVG filter on '{target.Name}' requires resolvable object bounds");
                    return false;
                }
                primitiveScaleX = bounds.Width;
                primitiveScaleY = bounds.Height;
                double diagonal = Math.Sqrt(
                    (double)bounds.Width * bounds.Width +
                    (double)bounds.Height * bounds.Height);
                if (double.IsNaN(diagonal) || double.IsInfinity(diagonal) || diagonal <= 0)
                {
                    _report.RequireFallback(
                        $"SVG filter on '{target.Name}' has degenerate object bounds");
                    return false;
                }
                primitiveScaleZ = (float)(diagonal / Math.Sqrt(2));
                if (!float.IsFinite(primitiveScaleZ) || primitiveScaleZ <= 0f)
                {
                    _report.RequireFallback(
                        $"SVG filter on '{target.Name}' has an unusable object bounding box depth");
                    return false;
                }
            }
            else if (!string.IsNullOrWhiteSpace(primitiveUnits) &&
                     !string.Equals(primitiveUnits, "userSpaceOnUse", StringComparison.OrdinalIgnoreCase))
            {
                _report.RequireFallback(
                    $"SVG primitiveUnits '{primitiveUnits}' requires compatibility fallback");
                return false;
            }

            if (!TryResolveFilterRegion(filterTemplate, target, viewport, out var filterRegion))
            {
                _report.RequireFallback(
                    $"SVG filter on '{target.Name}' has an unusable filter region");
                return false;
            }

            if (!filterTemplate.HasPrimitives)
            {
                using var emptyShader = SKShader.CreateColor(SKColors.Transparent);
                current = SKImageFilter.CreateShader(emptyShader, false, filterRegion);
                if (current == null)
                {
                    _report.RequireFallback("empty SVG filter result could not be created");
                    return false;
                }
                owned.Add(current);
                return true;
            }

            var results = new Dictionary<string, SKImageFilter>(StringComparer.Ordinal);
            foreach (var primitive in filterTemplate.ContentOwner.Children)
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

                bool rejectedBefore = _report.ResourceRejected;
                SKImageFilter next;
                if (primitive.Name == "feFlood")
                {
                    next = BuildFlood(
                        filterTemplate, primitive, target, viewport, filterRegion);
                }
                else if (primitive.Name == "feImage")
                {
                    next = BuildImage(
                        filterTemplate, primitive, target, viewport, filterRegion);
                }
                else if (primitive.Name == "feTurbulence")
                {
                    next = BuildTurbulence(
                        filterTemplate, primitive, target, viewport, filterRegion);
                }
                else if (primitive.Name == "feBlend")
                {
                    if (!TryResolveFilterInput(
                            filterTemplate, primitive, primitive.GetAttribute("in"),
                            target, viewport, filterRegion, current, results, owned,
                            out var input) ||
                        !TryResolveFilterInput(
                            filterTemplate, primitive,
                            primitive.GetAttribute("in2") ?? "SourceGraphic",
                            target, viewport, filterRegion, current, results, owned,
                            out var input2))
                        return false;
                    next = BuildBlend(primitive, input, input2);
                }
                else if (primitive.Name == "feComposite")
                {
                    if (!TryResolveFilterInput(
                            filterTemplate, primitive, primitive.GetAttribute("in"),
                            target, viewport, filterRegion, current, results, owned,
                            out var input) ||
                        !TryResolveFilterInput(
                            filterTemplate, primitive,
                            primitive.GetAttribute("in2") ?? "SourceGraphic",
                            target, viewport, filterRegion, current, results, owned,
                            out var input2))
                        return false;
                    next = BuildComposite(
                        filterTemplate, primitive, target, viewport, filterRegion,
                        input, input2);
                }
                else if (primitive.Name == "feDisplacementMap")
                {
                    if (!TryResolveFilterInput(
                            filterTemplate, primitive, primitive.GetAttribute("in"),
                            target, viewport, filterRegion, current, results, owned,
                            out var input) ||
                        !TryResolveFilterInput(
                            filterTemplate, primitive,
                            primitive.GetAttribute("in2") ?? "SourceGraphic",
                            target, viewport, filterRegion, current, results, owned,
                            out var input2))
                        return false;
                    if (input2 == null)
                    {
                        input2 = SKImageFilter.CreateOffset(0f, 0f);
                        if (input2 == null) return false;
                        owned.Add(input2);
                    }
                    next = BuildDisplacementMap(
                        primitive, input, input2, primitiveScaleX, primitiveScaleY);
                }
                else if (primitive.Name == "feMerge")
                {
                    next = BuildMerge(
                        filterTemplate, primitive, target, viewport, filterRegion,
                        current, results, owned);
                }
                else
                {
                    if (!TryResolveFilterInput(
                            filterTemplate, primitive, primitive.GetAttribute("in"),
                            target, viewport, filterRegion, current, results, owned,
                            out var input))
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
                            filterTemplate, target, primitive, input, primitiveScaleX, primitiveScaleY),
                        "feColorMatrix" => BuildColorMatrix(primitive, input),
                        "feComponentTransfer" => BuildComponentTransfer(
                            filterTemplate, primitive, input),
                        "feConvolveMatrix" => BuildConvolveMatrix(
                            filterTemplate, primitive, input, filterRegion, owned),
                        "feMorphology" => BuildMorphology(
                            primitive, input, primitiveScaleX, primitiveScaleY),
                        "feTile" => BuildTile(
                            primitive, filterRegion, input, owned,
                            primitiveScaleX, primitiveScaleY),
                        "feDiffuseLighting" => BuildLighting(
                            filterTemplate, primitive, target, viewport, filterRegion,
                            input, specular: false,
                            primitiveScaleX, primitiveScaleY, primitiveScaleZ),
                        "feSpecularLighting" => BuildLighting(
                            filterTemplate, primitive, target, viewport, filterRegion,
                            input, specular: true,
                            primitiveScaleX, primitiveScaleY, primitiveScaleZ),
                        _ => null
                    };
                }

                if (next == null)
                {
                    if (!rejectedBefore && !_report.ResourceRejected)
                    {
                        _report.RequireFallback(
                            $"SVG filter primitive '{primitive.Name}' requires compatibility fallback");
                    }
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

            if (current == null)
            {
                _report.RequireFallback("SVG filter produced no primitive result");
                return false;
            }

            var cropped = SKImageFilter.CreateOffset(0f, 0f, current, filterRegion);
            if (cropped == null)
            {
                _report.RequireFallback("SVG filter region crop could not be created");
                return false;
            }
            owned.Add(cropped);
            current = cropped;
            return true;
        }

        private bool TryResolveFilterInput(
            ResolvedFilter filter,
            SvgElement primitive,
            string raw,
            SvgElement target,
            ViewportContext viewport,
            SKRect filterRegion,
            SKImageFilter current,
            Dictionary<string, SKImageFilter> results,
            List<SKImageFilter> owned,
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
            if (name.Equals("FillPaint", StringComparison.Ordinal) ||
                name.Equals("StrokePaint", StringComparison.Ordinal))
            {
                if (!TryBuildPaintInput(
                        filter, primitive, target, viewport, filterRegion,
                        name[0] == 'S', out input))
                {
                    return false;
                }
                owned.Add(input);
                return true;
            }
            if (results.TryGetValue(name, out input)) return true;
            if (name.Equals("BackgroundImage", StringComparison.Ordinal) ||
                name.Equals("BackgroundAlpha", StringComparison.Ordinal))
            {
                _report.RequireFallback(
                    $"SVG {primitive.Name} filter input '{name}' requires compatibility fallback: " +
                    "the renderer paints each frame in a single pass and captures no document backdrop");
                input = null;
                return false;
            }

            _report.RequireFallback(
                $"SVG {primitive.Name} filter input '{name}' requires compatibility fallback");
            input = null;
            return false;
        }

        /// <summary>
        /// Resolved paint style of a filter target, reached by replaying the
        /// ancestor chain from the document root exactly the way the render walk
        /// accumulates it, so a paint keyword sees the same fill/stroke value the
        /// target element itself paints with.
        /// </summary>
        private InheritedStyle ResolveFilterTargetStyle(SvgElement target)
        {
            if (target == null) return new InheritedStyle();
            _filterTargetStyles ??= new Dictionary<SvgElement, InheritedStyle>();
            if (_filterTargetStyles.TryGetValue(target, out var cached)) return cached;
            var resolved = ResolvePatternContentStyle(target);
            if (_filterTargetStyles.Count < MaxFilterTargetStyles)
            {
                _filterTargetStyles[target] = resolved;
            }
            return resolved;
        }

        /// <summary>
        /// Builds the FillPaint/StrokePaint standard input: the target element's own
        /// fill or stroke paint, with conceptually infinite extent, so the primitive
        /// subregion (the filter region unless the primitive narrows it) is the only
        /// clip. The paint is the element's own paint resolved through the shared
        /// paint-server path, so a paint server lands on the target's object
        /// bounding box and an unresolvable server fails closed rather than
        /// guessing a colour.
        /// </summary>
        private bool TryBuildPaintInput(
            ResolvedFilter filter,
            SvgElement primitive,
            SvgElement target,
            ViewportContext viewport,
            SKRect filterRegion,
            bool stroke,
            out SKImageFilter input)
        {
            input = null;
            string keyword = stroke ? "StrokePaint" : "FillPaint";
            string primitiveName = primitive.Name;

            if (!TryResolvePrimitiveRegion(
                    filter, primitive, target, viewport, filterRegion, out var region))
            {
                _report.RequireFallback(
                    $"SVG {primitiveName} '{keyword}' input has an unusable primitive subregion");
                return false;
            }

            InheritedStyle style = ResolveFilterTargetStyle(target);
            var spec = stroke ? style.Stroke : style.Fill;
            float opacity = ReadOwnPaintOpacity(target, stroke ? "stroke-opacity" : "fill-opacity");

            if (spec.Kind == SvgValues.PaintKind.None)
            {
                input = BuildFlatPaintFilter(SKColors.Transparent, 1f, region);
                if (input == null)
                {
                    _report.RequireFallback(
                        $"SVG {primitiveName} '{keyword}' input could not be created");
                }
                return input != null;
            }
            if (stroke && !(style.StrokeWidth > 0f))
            {
                // A zero-width stroke paints no stroke, but the keyword names the
                // stroke property value, which a zero width leaves ambiguous.
                _report.RequireFallback(
                    $"SVG {primitiveName} '{keyword}' input needs a resolvable stroke geometry");
                return false;
            }

            if (spec.Kind == SvgValues.PaintKind.Color ||
                spec.Kind == SvgValues.PaintKind.CurrentColor)
            {
                SKColor color = spec.Kind == SvgValues.PaintKind.CurrentColor
                    ? style.CurrentColor
                    : spec.Color;
                input = BuildFlatPaintFilter(color, opacity, region);
                if (input == null)
                {
                    _report.RequireFallback(
                        $"SVG {primitiveName} '{keyword}' input could not be created");
                }
                return input != null;
            }

            if (spec.Kind != SvgValues.PaintKind.ServerRef)
            {
                _report.RequireFallback(
                    $"SVG {primitiveName} '{keyword}' input has no resolvable paint");
                return false;
            }
            if (!TryResolveObjectBounds(target, viewport, out var bounds))
            {
                _report.RequireFallback(
                    $"SVG {primitiveName} '{keyword}' input requires resolvable object bounds");
                return false;
            }

            SKShader shader;
            bool ownsShader;
            using (var boundsBuilder = new SKPathBuilder())
            {
                boundsBuilder.AddRect(bounds);
                using var boundsPath = boundsBuilder.Detach();
                shader = BuildServerShader(
                    spec.Fragment, boundsPath, spec.Fallback, style, viewport,
                    ResolveContextPaintFrame(spec.ContextSource, target, viewport),
                    out SKColor? fallbackColor, out ownsShader);
                if (shader == null && fallbackColor.HasValue)
                {
                    shader = SKShader.CreateColor(fallbackColor.Value);
                    ownsShader = shader != null;
                }
            }
            if (shader == null)
            {
                _report.RequireFallback(
                    $"SVG {primitiveName} '{keyword}' input paint server is unresolvable");
                return false;
            }

            try
            {
                input = BuildShaderPaintFilter(shader, opacity, region);
            }
            finally
            {
                if (ownsShader) shader.Dispose();
            }
            if (input == null)
            {
                _report.RequireFallback(
                    $"SVG {primitiveName} '{keyword}' input could not be created");
            }
            return input != null;
        }

        private static SKImageFilter BuildFlatPaintFilter(
            SKColor color,
            float opacity,
            SKRect region)
        {
            byte alpha = (byte)(color.Alpha * opacity);
            using var shader = SKShader.CreateColor(
                new SKColor(color.Red, color.Green, color.Blue, alpha));
            return SKImageFilter.CreateShader(shader, false, region);
        }

        private static SKImageFilter BuildShaderPaintFilter(
            SKShader shader,
            float opacity,
            SKRect region)
        {
            SKImageFilter paint = SKImageFilter.CreateShader(shader, false, region);
            if (paint == null) return null;

            // The paint-server path modulates a shader by the paint's own alpha,
            // so the keyword's opacity is a plain premultiplied scale of alpha that
            // leaves the colour channels untouched in either colour space.
            byte alpha = (byte)(255f * opacity);
            if (alpha == 255) return paint;
            if (alpha == 0)
            {
                paint.Dispose();
                using var clear = SKShader.CreateColor(SKColors.Transparent);
                return SKImageFilter.CreateShader(clear, false, region);
            }

            var matrix = new float[]
            {
                0,0,0,0,0,
                0,0,0,0,0,
                0,0,0,0,0,
                0,0,0,alpha / 255f,0
            };
            using var colorFilter = SKColorFilter.CreateColorMatrix(matrix);
            using (paint)
            {
                return SKImageFilter.CreateColorFilter(colorFilter, paint);
            }
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
            ResolvedFilter filter,
            SvgElement flood,
            SvgElement target,
            ViewportContext viewport,
            SKRect filterRegion)
        {
            if (!TryResolvePrimitiveRegion(
                    filter, flood, target, viewport, filterRegion, out var primitiveRegion))
                return null;

            if (!TryResolveFilterColor(
                    flood, filter, target, "flood-color", SKColors.Black, out var color))
                return null;
            float opacity = ReadInheritedOpacity(flood, filter, "flood-opacity", 1f);
            color = color.WithAlpha((byte)Math.Clamp(
                (int)MathF.Round(color.Alpha * opacity), 0, 255));

            using var shader = SKShader.CreateColor(color);
            return SKImageFilter.CreateShader(shader, false, primitiveRegion);
        }

        private SKImageFilter BuildImage(
            ResolvedFilter filter,
            SvgElement image,
            SvgElement target,
            ViewportContext viewport,
            SKRect filterRegion)
        {
            if (!TryResolvePrimitiveRegion(
                    filter, image, target, viewport, filterRegion, out var primitiveRegion))
                return null;

            string href = image.GetAttribute("href") ?? image.GetLookup("xlink:href");
            if (!SvgValues.TryParseLocalReference(href, out string id))
            {
                _report.RejectResource(
                    "feImage external reference rejected by SVG resource policy");
                return null;
            }
            if (!_doc.ElementsById.TryGetValue(id, out var referenced))
            {
                return null;
            }
            if (_activeLayers + 1 + EstimateOpacityLayerDepth(referenced, 0) > _maxActiveLayers)
            {
                _report.RequireFallback("SVG filter image layer budget exceeded");
                return null;
            }

            using var recorder = new SKPictureRecorder();
            var pictureCanvas = recorder.BeginRecording(primitiveRegion);
            pictureCanvas.ClipRect(primitiveRegion);
            var inherited = referenced.Parent == null
                ? new InheritedStyle()
                : ResolvePatternContentStyle(referenced.Parent);
            DrawElement(referenced, pictureCanvas, viewport, inherited);
            using var picture = recorder.EndRecording();
            return SKImageFilter.CreatePicture(picture, primitiveRegion);
        }

        private SKImageFilter BuildTurbulence(
            ResolvedFilter filter,
            SvgElement turbulence,
            SvgElement target,
            ViewportContext viewport,
            SKRect filterRegion)
        {
            if (string.Equals(
                    filter.PrimitiveUnits, "objectBoundingBox",
                    StringComparison.OrdinalIgnoreCase) ||
                !TryResolvePrimitiveRegion(
                    filter, turbulence, target, viewport, filterRegion, out var primitiveRegion) ||
                !TryReadNumberPair(
                    turbulence.GetAttribute("baseFrequency"), 0f, 1024f,
                    out float frequencyX, out float frequencyY) ||
                !TryReadBoundedInteger(
                    turbulence.GetAttribute("numOctaves"), 1, 16, out int octaves) ||
                !TryReadSingleNumber(turbulence.GetAttribute("seed"), 0f, out float seed) ||
                MathF.Abs(seed) > 32767f ||
                !string.Equals(
                    turbulence.GetAttribute("stitchTiles") ?? "noStitch", "noStitch",
                    StringComparison.OrdinalIgnoreCase))
                return null;

            string type = (turbulence.GetAttribute("type") ?? "turbulence").Trim();
            using var shader = type switch
            {
                "turbulence" => SKShader.CreatePerlinNoiseTurbulence(
                    frequencyX, frequencyY, octaves, seed),
                "fractalNoise" => SKShader.CreatePerlinNoiseFractalNoise(
                    frequencyX, frequencyY, octaves, seed),
                _ => null
            };
            return shader == null
                ? null
                : SKImageFilter.CreateShader(shader, false, primitiveRegion);
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
            ResolvedFilter filter,
            SvgElement element,
            SvgElement target,
            ViewportContext viewport,
            SKRect filterRegion,
            SKImageFilter input,
            SKImageFilter input2)
        {
            string operation = (element.GetAttribute("operator") ?? "over").Trim().ToLowerInvariant();
            if (operation == "arithmetic")
            {
                if (!TryResolvePrimitiveRegion(
                        filter, element, target, viewport, filterRegion, out var primitiveRegion) ||
                    !TryReadBoundedCoefficient(element, "k1", out float k1) ||
                    !TryReadBoundedCoefficient(element, "k2", out float k2) ||
                    !TryReadBoundedCoefficient(element, "k3", out float k3) ||
                    !TryReadBoundedCoefficient(element, "k4", out float k4))
                    return null;

                // SVG's `in` is the foreground/source term and `in2` is the
                // background/destination term in Skia's arithmetic formula.
                return SKImageFilter.CreateArithmetic(
                    k1, k2, k3, k4, true, input2, input, primitiveRegion);
            }

            SKBlendMode mode = operation switch
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

        private static bool TryReadBoundedCoefficient(
            SvgElement element,
            string name,
            out float value)
        {
            return TryReadSingleNumber(element.GetAttribute(name), 0f, out value) &&
                MathF.Abs(value) <= 32767f;
        }

        private static bool TryReadBoundedInteger(
            string raw,
            int fallback,
            int maximum,
            out int value)
        {
            value = fallback;
            if (string.IsNullOrWhiteSpace(raw)) return true;
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan());
            return tokenizer.Next(out var token) &&
                TryParseBoundedInteger(token, maximum, out value) &&
                !tokenizer.Next(out _);
        }

        private SKImageFilter BuildLighting(
            ResolvedFilter filter,
            SvgElement lighting,
            SvgElement target,
            ViewportContext viewport,
            SKRect filterRegion,
            SKImageFilter input,
            bool specular,
            float scaleX,
            float scaleY,
            float scaleZ)
        {
            if (!TryResolvePrimitiveRegion(
                    filter, lighting, target, viewport, filterRegion, out var primitiveRegion) ||
                !SupportsUnitKernel(lighting.GetAttribute("kernelUnitLength")) ||
                !TryReadBoundedLightingNumber(
                    lighting.GetAttribute("surfaceScale"), 1f, -32767f, 32767f,
                    out float surfaceScale))
                return null;

            SvgElement light = null;
            foreach (var child in lighting.Children)
            {
                CheckDeadline();
                if (child.Name is "title" or "desc" or "metadata") continue;
                if (light != null) return null;
                light = child;
            }
            if (light == null ||
                !TryResolveFilterColor(
                    lighting, filter, target, "lighting-color", SKColors.White, out var lightColor) ||
                !TryReadLightingGains(lighting, specular, out float specularConstant, out float specularExponent))
                return null;

            if (light.Name == "feDistantLight")
            {
                if (!TryReadBoundedLightingNumber(
                        light.GetAttribute("azimuth"), 0f, -360000f, 360000f,
                        out float azimuth) ||
                    !TryReadBoundedLightingNumber(
                        light.GetAttribute("elevation"), 0f, -360000f, 360000f,
                        out float elevation))
                    return null;

                const float DegreesToRadians = MathF.PI / 180f;
                float azimuthRadians = azimuth * DegreesToRadians;
                float elevationRadians = elevation * DegreesToRadians;
                float elevationCosine = MathF.Cos(elevationRadians);
                var direction = new SKPoint3(
                    MathF.Cos(azimuthRadians) * elevationCosine,
                    MathF.Sin(azimuthRadians) * elevationCosine,
                    MathF.Sin(elevationRadians));

                if (!specular)
                {
                    if (!TryReadBoundedLightingNumber(
                            lighting.GetAttribute("diffuseConstant"), 1f, 0f, 32767f,
                            out float diffuseConstant))
                        return null;
                    return SKImageFilter.CreateDistantLitDiffuse(
                        direction, lightColor, surfaceScale, diffuseConstant,
                        input, primitiveRegion);
                }
                return SKImageFilter.CreateDistantLitSpecular(
                    direction, lightColor, surfaceScale, specularConstant,
                    specularExponent, input, primitiveRegion);
            }

            if (light.Name == "fePointLight")
            {
                var location = ReadLightPosition(light, scaleX, scaleY, scaleZ);
                if (!specular)
                {
                    if (!TryReadBoundedLightingNumber(
                            lighting.GetAttribute("diffuseConstant"), 1f, 0f, 32767f,
                            out float diffuseConstant))
                        return null;
                    return SKImageFilter.CreatePointLitDiffuse(
                        location, lightColor, surfaceScale, diffuseConstant,
                        input, primitiveRegion);
                }
                return SKImageFilter.CreatePointLitSpecular(
                    location, lightColor, surfaceScale, specularConstant,
                    specularExponent, input, primitiveRegion);
            }

            if (light.Name == "feSpotLight")
            {
                var location = ReadLightPosition(light, scaleX, scaleY, scaleZ);
                var focus = ReadSpotLightFocus(light, scaleX, scaleY, scaleZ);
                if (!TryReadLightExponent(
                        light.GetAttribute("specularExponent"), out float lightExponent) ||
                    !TryReadConeAngle(light.GetAttribute("limitingConeAngle"), out float coneAngle))
                    return null;
                if (!specular)
                {
                    if (!TryReadBoundedLightingNumber(
                            lighting.GetAttribute("diffuseConstant"), 1f, 0f, 32767f,
                            out float diffuseConstant))
                        return null;
                    return SKImageFilter.CreateSpotLitDiffuse(
                        location, focus, lightExponent, coneAngle, lightColor, surfaceScale,
                        diffuseConstant, input, primitiveRegion);
                }
                return SKImageFilter.CreateSpotLitSpecular(
                    location, focus, lightExponent, coneAngle, lightColor, surfaceScale,
                    specularConstant, specularExponent, input, primitiveRegion);
            }

            return null;
        }

        private static bool TryReadLightingGains(
            SvgElement lighting,
            bool specular,
            out float specularConstant,
            out float specularExponent)
        {
            specularConstant = 1f;
            specularExponent = 1f;
            if (!specular) return true;
            return TryReadBoundedLightingNumber(
                       lighting.GetAttribute("specularConstant"), 1f, 0f, 32767f,
                       out specularConstant) &&
                   TryReadBoundedLightingNumber(
                       lighting.GetAttribute("specularExponent"), 1f, 1f, 128f,
                       out specularExponent);
        }

        private static SKPoint3 ReadLightPosition(
            SvgElement light,
            float scaleX,
            float scaleY,
            float scaleZ) =>
            new SKPoint3(
                ReadScaledLength(light.GetAttribute("x"), scaleX, 32767f),
                ReadScaledLength(light.GetAttribute("y"), scaleY, 32767f),
                ReadScaledLength(light.GetAttribute("z"), scaleZ, 32767f));

        private static SKPoint3 ReadSpotLightFocus(
            SvgElement light,
            float scaleX,
            float scaleY,
            float scaleZ) =>
            new SKPoint3(
                ReadScaledLength(
                    light.GetAttribute("pointsAtX") ?? light.GetAttribute("x"), scaleX, 32767f),
                ReadScaledLength(
                    light.GetAttribute("pointsAtY") ?? light.GetAttribute("y"), scaleY, 32767f),
                ReadScaledLength(light.GetAttribute("pointsAtZ"), scaleZ, 32767f));

        private static bool TryReadLightExponent(string raw, out float value)
        {
            if (!TryReadSingleNumber(raw, 1f, out value)) return false;
            value = Math.Clamp(value, 1f, 128f);
            return float.IsFinite(value);
        }

        private static bool TryReadConeAngle(string raw, out float value)
        {
            if (!TryReadSingleNumber(raw, 180f, out value)) return false;
            value = Math.Clamp(value, 0f, 180f);
            return float.IsFinite(value);
        }

        private static bool TryReadBoundedLightingNumber(
            string raw,
            float fallback,
            float minimum,
            float maximum,
            out float value)
        {
            return TryReadSingleNumber(raw, fallback, out value) &&
                value >= minimum && value <= maximum;
        }

        private static SKImageFilter BuildDisplacementMap(
            SvgElement element,
            SKImageFilter input,
            SKImageFilter displacement,
            float scaleX,
            float scaleY)
        {
            if (!TryReadColorChannel(
                    element.GetAttribute("xChannelSelector"), out var xChannel) ||
                !TryReadColorChannel(
                    element.GetAttribute("yChannelSelector"), out var yChannel) ||
                !TryReadSingleNumber(element.GetAttribute("scale"), 0f, out float scale))
                return null;

            // Skia's displacement primitive accepts one device-space scale.
            // A non-uniform object-bounding-box mapping would require separate
            // X/Y scales and must remain an honest compatibility fallback.
            if (scale != 0f && MathF.Abs(scaleX - scaleY) > .0001f) return null;
            scale *= scaleX;
            if (!float.IsFinite(scale) || MathF.Abs(scale) > 32767f) return null;

            return SKImageFilter.CreateDisplacementMapEffect(
                xChannel, yChannel, scale, displacement, input);
        }

        private static bool TryReadColorChannel(string raw, out SKColorChannel channel)
        {
            channel = SKColorChannel.A;
            string value = raw?.Trim();
            if (string.IsNullOrEmpty(value)) return true;
            switch (value.ToUpperInvariant())
            {
                case "R": channel = SKColorChannel.R; return true;
                case "G": channel = SKColorChannel.G; return true;
                case "B": channel = SKColorChannel.B; return true;
                case "A": channel = SKColorChannel.A; return true;
                default: return false;
            }
        }

        private SKImageFilter BuildMerge(
            ResolvedFilter filter,
            SvgElement merge,
            SvgElement target,
            ViewportContext viewport,
            SKRect filterRegion,
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
                        filter, node, node.GetAttribute("in"), target, viewport, filterRegion,
                        current, results, owned, out var input))
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
            ResolvedFilter filter,
            SvgElement target,
            ViewportContext viewport,
            out SKRect region)
        {
            bool objectUnits = !string.Equals(
                filter.FilterUnits, "userSpaceOnUse", StringComparison.OrdinalIgnoreCase);
            if (objectUnits)
            {
                if (!TryResolveObjectBounds(target, viewport, out var bounds))
                {
                    region = default;
                    return false;
                }
                float x = ReadMaskFraction(filter.X, -.1f);
                float y = ReadMaskFraction(filter.Y, -.1f);
                float width = ReadMaskFraction(filter.Width, 1.2f);
                float height = ReadMaskFraction(filter.Height, 1.2f);
                region = new SKRect(
                    bounds.Left + x * bounds.Width,
                    bounds.Top + y * bounds.Height,
                    bounds.Left + (x + width) * bounds.Width,
                    bounds.Top + (y + height) * bounds.Height);
            }
            else
            {
                float x = ResolveMaskUserLength(filter.X,
                    -viewport.Width * .1f, viewport.Width);
                float y = ResolveMaskUserLength(filter.Y,
                    -viewport.Height * .1f, viewport.Height);
                float width = ResolveMaskUserLength(filter.Width,
                    viewport.Width * 1.2f, viewport.Width);
                float height = ResolveMaskUserLength(filter.Height,
                    viewport.Height * 1.2f, viewport.Height);
                region = new SKRect(x, y, x + width, y + height);
            }
            return IsFilterRegionAdmitted(region);
        }

        private bool TryResolvePrimitiveRegion(
            ResolvedFilter filter,
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
                return IsFilterRegionAdmitted(region);
            }

            bool objectUnits = string.Equals(
                filter.PrimitiveUnits, "objectBoundingBox", StringComparison.OrdinalIgnoreCase);
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
            return IsFilterRegionAdmitted(region);
        }

        private bool IsFilterRegionAdmitted(SKRect region)
        {
            if (!IsUsableFilterRegion(region)) return false;
            double width = Math.Ceiling(region.Width);
            double height = Math.Ceiling(region.Height);
            if (width <= _limits.MaxRasterWidth && height <= _limits.MaxRasterHeight &&
                width * height <= _limits.MaxRasterPixels)
            {
                return true;
            }
            _report.RequireFallback("SVG filter region exceeds raster admission limits");
            return false;
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
            ResolvedFilter filter,
            SvgElement target,
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
            if (!TryResolveFilterColor(
                    element, filter, target, "flood-color", SKColors.Black, out var color))
                return null;
            float opacity = ReadInheritedOpacity(element, filter, "flood-opacity", 1f);
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
            string raw = (element.GetAttribute("operator") ?? "erode").Trim();
            if (string.Equals(raw, "erode", StringComparison.OrdinalIgnoreCase))
                return SKImageFilter.CreateErode(x, y, input);
            if (string.Equals(raw, "dilate", StringComparison.OrdinalIgnoreCase))
                return SKImageFilter.CreateDilate(x, y, input);
            _report.RequireFallback(
                $"SVG feMorphology operator '{raw}' requires compatibility fallback");
            return null;
        }

        private SKImageFilter BuildTile(
            SvgElement tile,
            SKRect filterRegion,
            SKImageFilter input,
            List<SKImageFilter> owned,
            float scaleX,
            float scaleY)
        {
            if (!TryReadTileLength(
                    tile.GetAttribute("x"), filterRegion.Left, scaleX, out float x) ||
                !TryReadTileLength(
                    tile.GetAttribute("y"), filterRegion.Top, scaleY, out float y) ||
                !TryReadTileLength(
                    tile.GetAttribute("width"), filterRegion.Width, scaleX, out float width) ||
                !TryReadTileLength(
                    tile.GetAttribute("height"), filterRegion.Height, scaleY, out float height))
                return null;

            x = MathF.Round(x);
            y = MathF.Round(y);
            width = MathF.Round(width);
            height = MathF.Round(height);
            if (width < 1f || height < 1f ||
                width > 32767f || height > 32767f ||
                MathF.Abs(x) > 1048576f || MathF.Abs(y) > 1048576f)
                return null;

            if (x > filterRegion.Left)
                x -= width * (MathF.Floor((x - filterRegion.Left) / width) + 1f);
            if (y > filterRegion.Top)
                y -= height * (MathF.Floor((y - filterRegion.Top) / height) + 1f);
            if (!float.IsFinite(x) || !float.IsFinite(y) ||
                MathF.Abs(x) > 1048576f || MathF.Abs(y) > 1048576f)
                return null;

            int columns = TileRepeatCount(x, width, filterRegion.Right);
            int rows = TileRepeatCount(y, height, filterRegion.Bottom);
            long repeats = (long)columns * rows;
            if (repeats > MaxTileRepeats)
            {
                _report.RequireFallback("SVG feTile repeat budget exceeded");
                return null;
            }
            if (repeats == 1)
                return SKImageFilter.CreateOffset(0f, 0f, input, filterRegion);

            var copies = new SKImageFilter[repeats];
            for (int row = 0; row < rows; row++)
            {
                CheckDeadline();
                for (int column = 0; column < columns; column++)
                {
                    var copy = SKImageFilter.CreateOffset(column * width, row * height, input);
                    if (copy == null) return null;
                    owned.Add(copy);
                    copies[row * columns + column] = copy;
                }
            }
            var repeated = SKImageFilter.CreateMerge(copies);
            return repeated == null
                ? null
                : SKImageFilter.CreateOffset(0f, 0f, repeated, filterRegion);
        }

        private static int TileRepeatCount(float start, float size, float limit) =>
            limit > start
                ? Math.Max(1, (int)MathF.Ceiling((limit - start) / size))
                : 1;

        private static bool TryReadTileLength(
            string raw,
            float fallback,
            float scale,
            out float value)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                value = fallback;
                return true;
            }
            if (!TryReadSingleNumber(raw, 0f, out value)) return false;
            value *= scale;
            return float.IsFinite(value);
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

        private SKImageFilter BuildComponentTransfer(
            ResolvedFilter filter,
            SvgElement element,
            SKImageFilter input)
        {
            byte[] alpha = CreateIdentityTransferTable();
            byte[] red = CreateIdentityTransferTable();
            byte[] green = CreateIdentityTransferTable();
            byte[] blue = CreateIdentityTransferTable();

            foreach (var function in element.Children)
            {
                CheckDeadline();
                if (function.Name is "title" or "desc" or "metadata") continue;
                byte[] table = function.Name switch
                {
                    "feFuncA" or "feFuncR" or "feFuncG" or "feFuncB" =>
                        BuildComponentTransferTable(function),
                    _ => null
                };
                if (table == null) return null;

                // SVG applies the last function for a duplicated channel.
                switch (function.Name)
                {
                    case "feFuncA": alpha = table; break;
                    case "feFuncR": red = table; break;
                    case "feFuncG": green = table; break;
                    case "feFuncB": blue = table; break;
                }
            }

            using var colorFilter = SKColorFilter.CreateTable(alpha, red, green, blue);
            return BuildColorImageFilter(filter, element, colorFilter, input);
        }

        private SKImageFilter BuildColorImageFilter(
            ResolvedFilter filter,
            SvgElement element,
            SKColorFilter operation,
            SKImageFilter input)
        {
            if (!TryUseLinearFilterColorSpace(filter, element, out bool useLinear)) return null;
            if (!useLinear)
                return SKImageFilter.CreateColorFilter(operation, input);

            // Filter color operations default to linear-light channels. Compose
            // explicit transfer functions around Skia's channel operation so the
            // backing surface can remain sRGB.
            using var toLinear = SKColorFilter.CreateSrgbToLinearGamma();
            using var operationInLinear = SKColorFilter.CreateCompose(operation, toLinear);
            using var toSrgb = SKColorFilter.CreateLinearToSrgbGamma();
            using var composed = SKColorFilter.CreateCompose(toSrgb, operationInLinear);
            return SKImageFilter.CreateColorFilter(composed, input);
        }

        private static bool TryUseLinearFilterColorSpace(
            ResolvedFilter filter,
            SvgElement element,
            out bool useLinear)
        {
            string interpolation = filter?.ColorInterpolationFilters?.Trim();
            for (SvgElement current = element;
                 string.IsNullOrEmpty(interpolation) && current != null;
                 current = current.Parent)
            {
                interpolation = current.GetPresentationProperty("color-interpolation-filters")?.Trim();
            }

            useLinear = !string.Equals(interpolation, "sRGB", StringComparison.OrdinalIgnoreCase);
            return string.IsNullOrEmpty(interpolation) ||
                string.Equals(interpolation, "sRGB", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(interpolation, "linearRGB", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(interpolation, "auto", StringComparison.OrdinalIgnoreCase);
        }

        private SKImageFilter BuildConvolveMatrix(
            ResolvedFilter filter,
            SvgElement element,
            SKImageFilter input,
            SKRect filterRegion,
            List<SKImageFilter> owned)
        {
            if (!TryReadIntegerPair(element.GetAttribute("order"), 3, 25,
                    out int orderX, out int orderY) ||
                orderX * orderY > 625 ||
                !TryReadKernel(element.GetAttribute("kernelMatrix"), orderX * orderY,
                    out float[] kernel))
                return null;

            float divisor = 0f;
            string divisorRaw = element.GetAttribute("divisor");
            if (string.IsNullOrWhiteSpace(divisorRaw))
            {
                for (int i = 0; i < kernel.Length; i++) divisor += kernel[i];
                if (divisor == 0f) divisor = 1f;
            }
            else if (!TryReadSingleNumber(divisorRaw, 1f, out divisor) || divisor == 0f)
            {
                return null;
            }
            if (!float.IsFinite(divisor) || MathF.Abs(divisor) < 1e-6f) return null;

            if (!TryReadSingleNumber(element.GetAttribute("bias"), 0f, out float bias) ||
                MathF.Abs(bias) > 128f ||
                !TryReadTarget(element.GetAttribute("targetX"), orderX / 2, orderX, out int targetX) ||
                !TryReadTarget(element.GetAttribute("targetY"), orderY / 2, orderY, out int targetY) ||
                !SupportsUnitKernel(element.GetAttribute("kernelUnitLength")))
                return null;

            SKShaderTileMode edgeMode = (element.GetAttribute("edgeMode") ?? "duplicate")
                .Trim().ToLowerInvariant() switch
                {
                    "duplicate" => SKShaderTileMode.Clamp,
                    "wrap" => SKShaderTileMode.Repeat,
                    "none" => SKShaderTileMode.Decal,
                    _ => (SKShaderTileMode)(-1)
                };
            if ((int)edgeMode < 0) return null;

            bool preserveAlpha = string.Equals(
                element.GetAttribute("preserveAlpha")?.Trim(), "true",
                StringComparison.OrdinalIgnoreCase);
            if (!TryUseLinearFilterColorSpace(filter, element, out bool useLinear)) return null;

            SKImageFilter convolutionInput = input;
            if (useLinear)
            {
                using var toLinear = SKColorFilter.CreateSrgbToLinearGamma();
                convolutionInput = SKImageFilter.CreateColorFilter(toLinear, input);
                if (convolutionInput == null) return null;
                owned.Add(convolutionInput);
            }

            var convolution = SKImageFilter.CreateMatrixConvolution(
                new SKSizeI(orderX, orderY), kernel, 1f / divisor, bias * 255f,
                new SKPointI(targetX, targetY), edgeMode, !preserveAlpha,
                convolutionInput, filterRegion);
            if (convolution == null) return null;

            if (!useLinear) return convolution;
            owned.Add(convolution);
            using var toSrgb = SKColorFilter.CreateLinearToSrgbGamma();
            return SKImageFilter.CreateColorFilter(toSrgb, convolution);
        }

        private static bool TryReadIntegerPair(
            string raw,
            int fallback,
            int maximum,
            out int x,
            out int y)
        {
            x = y = fallback;
            if (string.IsNullOrWhiteSpace(raw)) return true;
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan());
            if (!tokenizer.Next(out var first) || !TryParseBoundedInteger(first, maximum, out x))
                return false;
            y = x;
            return !tokenizer.Next(out var second) ||
                (TryParseBoundedInteger(second, maximum, out y) && !tokenizer.Next(out _));
        }

        private static bool TryParseBoundedInteger(ReadOnlySpan<char> raw, int maximum, out int value)
        {
            value = 0;
            return SvgValues.TryParseNumber(raw, out float parsed) &&
                float.IsFinite(parsed) && parsed >= 1f && parsed <= maximum &&
                parsed == MathF.Truncate(parsed) && (value = (int)parsed) > 0;
        }

        private static bool TryReadKernel(string raw, int count, out float[] kernel)
        {
            kernel = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var values = new float[count];
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan());
            int index = 0;
            while (tokenizer.Next(out var token))
            {
                if (index >= count || !SvgValues.TryParseNumber(token, out values[index]) ||
                    !float.IsFinite(values[index])) return false;
                index++;
            }
            if (index != count) return false;
            kernel = values;
            return true;
        }

        private static bool TryReadTarget(string raw, int fallback, int order, out int target)
        {
            target = fallback;
            if (string.IsNullOrWhiteSpace(raw)) return true;
            return SvgValues.TryParseNumber(raw.AsSpan(), out float value) &&
                float.IsFinite(value) && value == MathF.Truncate(value) &&
                value >= 0f && value < order && (target = (int)value) >= 0;
        }

        private static bool SupportsUnitKernel(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return true;
            return TryReadNumberPair(raw, 0f, 32767f, out float x, out float y) &&
                MathF.Abs(x - 1f) < 1e-6f && MathF.Abs(y - 1f) < 1e-6f;
        }

        private static byte[] BuildComponentTransferTable(SvgElement function)
        {
            string type = (function.GetAttribute("type") ?? "identity").Trim();
            if (type.Equals("identity", StringComparison.OrdinalIgnoreCase))
                return CreateIdentityTransferTable();

            float[] values = null;
            if (type.Equals("table", StringComparison.OrdinalIgnoreCase) ||
                type.Equals("discrete", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadTransferValues(function.GetAttribute("tableValues"), out values))
                    return null;
                if (values.Length == 0) return CreateIdentityTransferTable();
            }

            float slope = 1f;
            float intercept = 0f;
            float amplitude = 1f;
            float exponent = 1f;
            float offset = 0f;
            if (type.Equals("linear", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadSingleNumber(function.GetAttribute("slope"), 1f, out slope) ||
                    !TryReadSingleNumber(function.GetAttribute("intercept"), 0f, out intercept))
                    return null;
            }
            else if (type.Equals("gamma", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadSingleNumber(function.GetAttribute("amplitude"), 1f, out amplitude) ||
                    !TryReadSingleNumber(function.GetAttribute("exponent"), 1f, out exponent) ||
                    !TryReadSingleNumber(function.GetAttribute("offset"), 0f, out offset))
                    return null;
            }
            else if (!type.Equals("table", StringComparison.OrdinalIgnoreCase) &&
                     !type.Equals("discrete", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var table = new byte[256];
            for (int i = 0; i < table.Length; i++)
            {
                float input = i / 255f;
                float output;
                if (type.Equals("table", StringComparison.OrdinalIgnoreCase))
                {
                    if (values.Length == 1)
                    {
                        output = values[0];
                    }
                    else
                    {
                        float position = input * (values.Length - 1);
                        int lower = Math.Min((int)MathF.Floor(position), values.Length - 2);
                        float fraction = position - lower;
                        output = values[lower] +
                            fraction * (values[lower + 1] - values[lower]);
                    }
                }
                else if (type.Equals("discrete", StringComparison.OrdinalIgnoreCase))
                {
                    int index = Math.Min((int)MathF.Floor(input * values.Length), values.Length - 1);
                    output = values[index];
                }
                else if (type.Equals("linear", StringComparison.OrdinalIgnoreCase))
                {
                    output = slope * input + intercept;
                }
                else output = amplitude * MathF.Pow(input, exponent) + offset;

                table[i] = ToTransferByte(output);
            }
            return table;
        }

        private static byte[] CreateIdentityTransferTable()
        {
            var table = new byte[256];
            for (int i = 0; i < table.Length; i++) table[i] = (byte)i;
            return table;
        }

        private static byte ToTransferByte(float value)
        {
            if (float.IsNaN(value)) return 0;
            if (value <= 0f) return 0;
            if (value >= 1f) return 255;
            return (byte)MathF.Round(value * 255f);
        }

        private static bool TryReadTransferValues(string raw, out float[] values)
        {
            values = Array.Empty<float>();
            if (string.IsNullOrWhiteSpace(raw)) return true;
            var parsed = new List<float>();
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan());
            while (tokenizer.Next(out var token))
            {
                if (parsed.Count >= 1024 ||
                    !SvgValues.TryParseNumber(token, out float value) ||
                    !float.IsFinite(value))
                    return false;
                parsed.Add(value);
            }
            values = parsed.ToArray();
            return true;
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

        private static float ReadScaledLength(string raw, float scale, float limit)
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                !SvgValues.TryParseNumber(raw.AsSpan(), out float value) ||
                !float.IsFinite(value)) return 0f;
            float scaled = value * scale;
            return float.IsFinite(scaled) ? Math.Clamp(scaled, -limit, limit) : 0f;
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
