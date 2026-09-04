using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        private sealed class ResolvedPattern
        {
            public SvgElement ContentOwner;
            public string X;
            public string Y;
            public string Width;
            public string Height;
            public string Units;
            public string ContentUnits;
            public string Transform;
            public string ViewBox;
            public string PreserveAspectRatio;
        }

        private SKShader BuildPatternShader(
            SvgElement server,
            SKPath geometry,
            InheritedStyle referencingStyle,
            ViewportContext viewport)
        {
            _activePatterns ??= new HashSet<SvgElement>();
            if (_activePatterns.Contains(server) ||
                _activePatterns.Count >= _maxReferenceDepth)
            {
                _report.Warn("pattern paint-server cycle detected; nested paint skipped");
                return null;
            }

            _activePatterns.Add(server);
            try
            {
                return BuildPatternShaderCore(server, geometry, referencingStyle, viewport);
            }
            finally
            {
                _activePatterns.Remove(server);
            }
        }

        private HashSet<SvgElement> _activePatterns;

        private SKShader BuildPatternShaderCore(
            SvgElement server,
            SKPath geometry,
            InheritedStyle referencingStyle,
            ViewportContext viewport)
        {
            if (!TryResolvePattern(server, out var pattern))
            {
                return null;
            }

            bool objectBoundingBox = !string.Equals(
                pattern.Units, "userSpaceOnUse", StringComparison.Ordinal);
            bool objectBoundingBoxContent = string.Equals(
                pattern.ContentUnits, "objectBoundingBox", StringComparison.Ordinal);

            SKRect bounds = default;
            bool hasBounds = TryGetPatternBounds(geometry, out bounds);
            if ((objectBoundingBox || objectBoundingBoxContent) && !hasBounds)
            {
                return null;
            }

            float xReference = objectBoundingBox ? 1f : viewport.Width;
            float yReference = objectBoundingBox ? 1f : viewport.Height;
            if (!TryResolvePatternLength(pattern.X, 0f, xReference, objectBoundingBox,
                    referencingStyle, out float x) ||
                !TryResolvePatternLength(pattern.Y, 0f, yReference, objectBoundingBox,
                    referencingStyle, out float y) ||
                !TryResolvePatternLength(pattern.Width, 0f, xReference, objectBoundingBox,
                    referencingStyle, out float width) ||
                !TryResolvePatternLength(pattern.Height, 0f, yReference, objectBoundingBox,
                    referencingStyle, out float height) ||
                !(width > 0f) || !(height > 0f))
            {
                return null;
            }

            var tile = new SKRect(x, y, x + width, y + height);
            if (!IsFinite(tile))
            {
                return null;
            }

            SKMatrix unitsMatrix = objectBoundingBox
                ? SKMatrix.Concat(
                    SKMatrix.CreateTranslation(bounds.Left, bounds.Top),
                    SKMatrix.CreateScale(bounds.Width, bounds.Height))
                : SKMatrix.Identity;

            if (!TryResolvePatternTransform(server, pattern.Transform, out var patternTransform))
            {
                return null;
            }

            var localMatrix = SKMatrix.Concat(unitsMatrix, patternTransform);
            if (!localMatrix.TryInvert(out _))
            {
                return null;
            }

            var mappedTile = localMatrix.MapRect(tile);
            if (!IsFinite(mappedTile) ||
                Math.Ceiling(mappedTile.Width) > _limits.MaxRasterWidth ||
                Math.Ceiling(mappedTile.Height) > _limits.MaxRasterHeight ||
                (double)Math.Ceiling(mappedTile.Width) * Math.Ceiling(mappedTile.Height) >
                    _limits.MaxRasterPixels)
            {
                _report.RequireFallback("SVG pattern tile exceeds bounded raster limits");
                return null;
            }

            SKMatrix inverseUnits = SKMatrix.Identity;
            if (objectBoundingBox && !objectBoundingBoxContent &&
                !unitsMatrix.TryInvert(out inverseUnits))
            {
                return null;
            }

            bool hasViewBox = TryParseViewBox(
                pattern.ViewBox,
                out float vbX,
                out float vbY,
                out float vbWidth,
                out float vbHeight,
                out bool viewBoxDisablesRendering);
            if (!string.IsNullOrWhiteSpace(pattern.ViewBox) &&
                !hasViewBox && !viewBoxDisablesRendering)
            {
                return null;
            }

            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(tile);
            canvas.ClipRect(tile);

            if (!viewBoxDisablesRendering)
            {
                var contentStyle = ResolvePatternContentStyle(pattern.ContentOwner);
                ViewportContext contentViewport;

                using (new CanvasState(canvas))
                {
                    if (hasViewBox)
                    {
                        canvas.Translate(tile.Left, tile.Top);
                        var tileViewport = new ViewportContext(tile.Width, tile.Height);
                        ApplyViewportTransform(
                            canvas,
                            tileViewport,
                            hasViewBox: true,
                            vbX,
                            vbY,
                            vbWidth,
                            vbHeight,
                            pattern.PreserveAspectRatio);
                        contentViewport = new ViewportContext(vbWidth, vbHeight);
                    }
                    else if (objectBoundingBoxContent)
                    {
                        if (!objectBoundingBox)
                        {
                            canvas.Translate(bounds.Left, bounds.Top);
                            canvas.Scale(bounds.Width, bounds.Height);
                        }
                        contentViewport = new ViewportContext(1f, 1f);
                    }
                    else
                    {
                        if (objectBoundingBox)
                        {
                            canvas.Concat(inverseUnits);
                        }
                        contentViewport = viewport;
                    }

                    DrawChildren(
                        pattern.ContentOwner,
                        canvas,
                        contentViewport,
                        contentStyle);
                }
            }

            using var picture = recorder.EndRecording();
            return SKShader.CreatePicture(
                picture,
                SKShaderTileMode.Repeat,
                SKShaderTileMode.Repeat,
                localMatrix,
                tile);
        }

        private bool TryResolvePattern(SvgElement server, out ResolvedPattern resolved)
        {
            resolved = new ResolvedPattern();
            var visited = new HashSet<SvgElement>();
            SvgElement current = server;

            while (current != null && current.Name == "pattern")
            {
                if (!IsPatternInValidContext(current) ||
                    !visited.Add(current) ||
                    visited.Count > _maxReferenceDepth)
                {
                    break;
                }

                resolved.X ??= current.GetAttribute("x");
                resolved.Y ??= current.GetAttribute("y");
                resolved.Width ??= current.GetAttribute("width");
                resolved.Height ??= current.GetAttribute("height");
                resolved.Units ??= current.GetAttribute("patternUnits");
                resolved.ContentUnits ??= current.GetAttribute("patternContentUnits");
                resolved.Transform ??= current.GetAttribute("patternTransform");
                resolved.ViewBox ??= current.GetAttribute("viewBox");
                resolved.PreserveAspectRatio ??= current.GetAttribute("preserveAspectRatio");

                if (resolved.ContentOwner == null && current.Children.Count != 0)
                {
                    resolved.ContentOwner = current;
                }

                string href = current.GetAttribute("href") ?? current.GetLookup("xlink:href");
                if (!SvgValues.TryParseLocalReference(href, out string id) ||
                    !_doc.ElementsById.TryGetValue(id, out var template) ||
                    template.Name != "pattern" ||
                    !IsPatternInValidContext(template))
                {
                    break;
                }
                current = template;
            }

            // A pattern with no renderable content is not a usable paint server;
            // the optional fallback paint in url(...) must be selected instead.
            return resolved.ContentOwner != null;
        }

        private static bool IsPatternInValidContext(SvgElement pattern)
        {
            for (SvgElement ancestor = pattern.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (ancestor.Name is "text" or "tspan" or "textPath")
                {
                    return false;
                }
            }
            return true;
        }

        private InheritedStyle ResolvePatternContentStyle(SvgElement contentOwner)
        {
            var ancestry = new List<SvgElement>();
            for (SvgElement current = contentOwner; current != null; current = current.Parent)
            {
                ancestry.Add(current);
            }

            var style = new InheritedStyle();
            for (int i = ancestry.Count - 1; i >= 0; i--)
            {
                style = style.ResolveOverrides(ancestry[i], _report);
            }
            return style;
        }

        private bool TryResolvePatternTransform(
            SvgElement server,
            string inheritedPatternTransform,
            out SKMatrix transform)
        {
            transform = SKMatrix.Identity;
            string raw = inheritedPatternTransform;

            if (server.CascadedDeclarations != null &&
                server.CascadedDeclarations.ContainsKey("transform"))
            {
                raw = server.GetPresentationProperty("transform");
            }

            if (string.IsNullOrWhiteSpace(raw) ||
                string.Equals(raw.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (SvgValues.TryParseTransformList(raw.AsSpan(), out transform))
            {
                return true;
            }

            _report.RequireFallback("SVG pattern CSS transform requires compatibility fallback");
            return false;
        }

        private static bool TryGetPatternBounds(SKPath geometry, out SKRect bounds)
        {
            bounds = geometry?.TightBounds ?? default;
            return geometry != null &&
                bounds.Width > 0f && bounds.Height > 0f &&
                IsFinite(bounds);
        }

        private static bool TryResolvePatternLength(
            string raw,
            float defaultValue,
            float percentReference,
            bool objectBoundingBox,
            InheritedStyle style,
            out float result)
        {
            result = defaultValue;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;
            }
            if (!SvgValues.TryParseLength(raw.AsSpan(), out float value, out var unit) ||
                !float.IsFinite(value))
            {
                return false;
            }

            if (unit == SvgValues.SvgUnit.Percent)
            {
                result = value * 0.01f * percentReference;
            }
            else
            {
                float scale = objectBoundingBox ? 1f : percentReference;
                result = SvgValues.ResolveUnits(
                    value,
                    unit,
                    style.FontSize,
                    scale > 0f ? scale : 1f);
            }
            return float.IsFinite(result);
        }

        private static bool IsFinite(SKRect rect) =>
            float.IsFinite(rect.Left) && float.IsFinite(rect.Top) &&
            float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom);
    }
}
