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
            public bool HasTransform;
            public SKMatrix Transform = SKMatrix.Identity;
            public string ViewBox;
            public string PreserveAspectRatio;
        }

        private SKShader BuildPatternShader(
            SvgElement server,
            SKPath geometry,
            ContextPaintFrame context,
            InheritedStyle referencingStyle,
            ViewportContext viewport,
            SKRect? paintedArea)
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
                return BuildPatternShaderCore(
                    server, geometry, context, referencingStyle, viewport, paintedArea);
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
            ContextPaintFrame context,
            InheritedStyle referencingStyle,
            ViewportContext viewport,
            SKRect? paintedArea)
        {
            if (!TryResolvePattern(server, referencingStyle, viewport, out var pattern))
            {
                return null;
            }

            bool objectBoundingBox = !string.Equals(
                pattern.Units, "userSpaceOnUse", StringComparison.Ordinal);
            bool objectBoundingBoxContent = string.Equals(
                pattern.ContentUnits, "objectBoundingBox", StringComparison.Ordinal);

            SKRect bounds = default;
            bool hasBounds = TryGetPatternBounds(context, geometry, out bounds);
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

            SKMatrix patternTransform = pattern.Transform;

            var localMatrix = context == null
                ? SKMatrix.Concat(unitsMatrix, patternTransform)
                : SKMatrix.Concat(
                    SKMatrix.Concat(unitsMatrix, patternTransform), context.ToElementSpace);
            if (!SvgValues.IsFinite(localMatrix))
            {
                _report.RequireFallback("SVG pattern transform is not finite and bounded");
                return null;
            }
            if (!localMatrix.TryInvert(out _))
            {
                return null;
            }

            // The recorded part of the tile. Normally the whole tile, repeated. A tile
            // too large to rasterize is still paintable when everything the shape can
            // paint lies inside a single tile instance: only that instance's visible
            // part is recorded and nothing repeats.
            SKRect recordRect = tile;
            SKMatrix shaderMatrix = localMatrix;
            var tileMode = SKShaderTileMode.Repeat;
            var mappedTile = localMatrix.MapRect(tile);
            if (ExceedsRasterLimits(mappedTile) &&
                context == null &&
                paintedArea.HasValue &&
                TryConfineToOneTile(localMatrix, tile, paintedArea.Value, out SKRect confined, out SKPoint offset))
            {
                recordRect = confined;
                shaderMatrix = SKMatrix.Concat(localMatrix, SKMatrix.CreateTranslation(offset.X, offset.Y));
                tileMode = SKShaderTileMode.Clamp;
                mappedTile = shaderMatrix.MapRect(recordRect);
            }
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
            var canvas = recorder.BeginRecording(recordRect);
            canvas.ClipRect(recordRect);

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
                        if (!ApplyViewportTransform(
                                canvas,
                                tileViewport,
                                hasViewBox: true,
                                vbX,
                                vbY,
                                vbWidth,
                                vbHeight,
                                pattern.PreserveAspectRatio))
                        {
                            return null;
                        }
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
                tileMode,
                tileMode,
                shaderMatrix,
                recordRect);
        }

        private bool ExceedsRasterLimits(SKRect mapped) =>
            !IsFinite(mapped) ||
            Math.Ceiling(mapped.Width) > _limits.MaxRasterWidth ||
            Math.Ceiling(mapped.Height) > _limits.MaxRasterHeight ||
            (double)Math.Ceiling(mapped.Width) * Math.Ceiling(mapped.Height) > _limits.MaxRasterPixels;

        /// <summary>
        /// Maps the painted area into pattern space and, when it lies inside one
        /// instance of the tile, returns that area in base-tile coordinates and the
        /// instance's offset from the base tile. The instance is the one holding the
        /// area's centre; up to one user unit beyond its edges is tolerated, since
        /// that is only antialiasing fringe and the clamped shader extends the edge.
        /// </summary>
        private static bool TryConfineToOneTile(
            SKMatrix localMatrix, SKRect tile, SKRect paintedArea, out SKRect confined, out SKPoint offset)
        {
            confined = default;
            offset = default;
            if (!localMatrix.TryInvert(out SKMatrix toPattern))
            {
                return false;
            }

            SKRect area = toPattern.MapRect(paintedArea);
            if (!IsFinite(area) || area.IsEmpty)
            {
                return false;
            }

            double column = Math.Floor((area.MidX - tile.Left) / (double)tile.Width);
            double row = Math.Floor((area.MidY - tile.Top) / (double)tile.Height);
            float dx = (float)(column * tile.Width);
            float dy = (float)(row * tile.Height);
            SKPoint unit = toPattern.MapVector(1f, 1f);
            float toleranceX = Math.Abs(unit.X);
            float toleranceY = Math.Abs(unit.Y);
            if (!float.IsFinite(dx) || !float.IsFinite(dy) ||
                area.Left < tile.Left + dx - toleranceX || area.Right > tile.Right + dx + toleranceX ||
                area.Top < tile.Top + dy - toleranceY || area.Bottom > tile.Bottom + dy + toleranceY)
            {
                return false;
            }

            confined = SKRect.Intersect(new SKRect(area.Left - dx, area.Top - dy, area.Right - dx, area.Bottom - dy), tile);
            offset = new SKPoint(dx, dy);
            return !confined.IsEmpty;
        }

        private bool TryResolvePattern(
            SvgElement server,
            InheritedStyle style,
            ViewportContext viewport,
            out ResolvedPattern resolved)
        {
            resolved = new ResolvedPattern();
            var visited = new HashSet<SvgElement>();
            SvgElement current = server;

            while (current != null)
            {
                if (current.Name != "pattern" ||
                    !IsPatternInValidContext(current) ||
                    !visited.Add(current) ||
                    visited.Count > _maxReferenceDepth)
                {
                    _report.RequireFallback("SVG pattern template reference requires compatibility fallback");
                    return false;
                }

                resolved.X ??= InheritedAttribute(current, "x");
                resolved.Y ??= InheritedAttribute(current, "y");
                resolved.Width ??= InheritedAttribute(current, "width");
                resolved.Height ??= InheritedAttribute(current, "height");
                resolved.Units ??= InheritedAttribute(current, "patternUnits");
                resolved.ContentUnits ??= InheritedAttribute(current, "patternContentUnits");
                if (!resolved.HasTransform)
                {
                    switch (ResolveServerTransform(current, "patternTransform", style, viewport, out var transform))
                    {
                        case ServerTransformStatus.NotSpecified:
                            break;
                        case ServerTransformStatus.Resolved:
                            resolved.HasTransform = true;
                            resolved.Transform = transform;
                            break;
                        default:
                            _report.RequireFallback(
                                "SVG pattern transform requires compatibility fallback");
                            return false;
                    }
                }
                resolved.ViewBox ??= InheritedAttribute(current, "viewBox");
                resolved.PreserveAspectRatio ??= InheritedAttribute(current, "preserveAspectRatio");

                if (resolved.ContentOwner == null && HasRenderablePatternContent(current))
                {
                    resolved.ContentOwner = current;
                }

                string href = current.GetAttribute("href") ?? current.GetLookup("xlink:href");
                if (string.IsNullOrWhiteSpace(href))
                {
                    break;
                }
                if (!SvgValues.TryParseLocalReference(href, out string id))
                {
                    _report.RejectResource("SVG pattern external template reference rejected");
                    return false;
                }
                if (!_doc.ElementsById.TryGetValue(id, out var template))
                {
                    return false;
                }
                if (template.Name != "pattern")
                {
                    _report.RequireFallback("SVG pattern template reference is invalid or unsupported");
                    return false;
                }
                if (!IsPatternInValidContext(template))
                {
                    return false;
                }
                current = template;
            }

            return resolved.ContentOwner != null;
        }

        private static bool HasRenderablePatternContent(SvgElement pattern)
        {
            for (int i = 0; i < pattern.Children.Count; i++)
            {
                var child = pattern.Children[i];
                if (child.Name is "title" or "desc" or "metadata")
                {
                    continue;
                }
                if (child.Name is "g" or "a" or "switch")
                {
                    if (HasRenderablePatternContent(child)) return true;
                    continue;
                }
                return true;
            }
            return false;
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

        private static bool TryGetPatternBounds(
            ContextPaintFrame context,
            SKPath geometry,
            out SKRect bounds)
        {
            bounds = ResolveObjectBoundingBox(context, geometry) ?? default;
            return IsPaintableBounds(bounds);
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
