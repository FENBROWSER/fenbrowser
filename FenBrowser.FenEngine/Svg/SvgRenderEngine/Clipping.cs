using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using SkiaSharp;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        // ------------------------------------------------------------ clipping

        /// <summary>Ids currently being resolved as clip paths (cycle guard).</summary>
        private HashSet<string> _activeClipIds;
        private HashSet<SvgElement> _activeObjectBounds;

        /// <summary>
        /// The use element whose own filter and mask are being built. Its x/y are a
        /// translation appended to its transform (SVG 2 §5.6), already on the canvas
        /// while those effects are built, so its bounding box is measured in that
        /// translated space. Anywhere else, such as a parent group's union, the
        /// offset is part of the instance's placement and stays in its bounds.
        /// </summary>
        private SvgElement _useInstanceSpace;

        /// <summary>
        /// Applies the element's clip-path attribute, if any. Must be called
        /// inside a CanvasState scope so the clip unwinds with the element.
        /// Supported: userSpaceOnUse (default) and objectBoundingBox units for
        /// shapes; direct shape children of &lt;clipPath&gt; (plus one level of
        /// use-to-shape). Everything else degrades to "no clip" with a warning.
        /// </summary>
        private bool TryResolveClipPathDefinition(
            SvgElement clip,
            out SvgElement contentOwner,
            out string clipPathUnits,
            out SvgElement transformOwner)
        {
            contentOwner = null;
            clipPathUnits = null;
            transformOwner = null;
            var visited = new HashSet<SvgElement>();
            SvgElement current = clip;
            while (current != null)
            {
                CheckDeadline();
                if (current.Name != "clipPath" || !visited.Add(current) ||
                    visited.Count > _maxReferenceDepth)
                {
                    _report.Warn("clipPath href cycle or depth budget exceeded");
                    _report.RequireFallback("SVG clipPath href cycle or depth budget exceeded");
                    return false;
                }
                if (clipPathUnits == null)
                    clipPathUnits = current.GetAttribute("clipPathUnits");
                if (transformOwner == null && current.GetAttribute("transform") != null)
                    transformOwner = current;
                if (contentOwner == null)
                {
                    foreach (var child in current.Children)
                    {
                        if (child.Name is not ("title" or "desc" or "metadata"))
                        {
                            contentOwner = current;
                            break;
                        }
                    }
                }

                string href = current.GetAttribute("href") ?? current.GetLookup("xlink:href");
                if (href == null) break;
                if (!SvgValues.TryParseLocalReference(href, out string id))
                {
                    _report.RejectResource("clipPath external href rejected by SVG resource policy");
                    return false;
                }
                if (!_doc.ElementsById.TryGetValue(id, out var next) || next.Name != "clipPath")
                {
                    _report.Warn("clipPath href reference unresolved");
                    break;
                }
                current = next;
            }
            return true;
        }

        private void ApplyClipPath(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle inherited)
        {
            var raw = el.GetPresentationProperty("clip-path");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            if (!SvgValues.TryParsePaint(raw.AsSpan(), out var kind, out _, out var fragment, out _))
            {
                if (!raw.Equals("none", StringComparison.OrdinalIgnoreCase))
                    _report.RequireFallback("SVG clip-path value requires compatibility fallback");
                return;
            }
            if (kind != SvgValues.PaintKind.ServerRef || fragment == null)
            {
                // none / remote reference: nothing to apply (remote is fail-closed).
                return;
            }
            if (!_doc.ElementsById.TryGetValue(fragment, out var clipEl) ||
                clipEl.Name != "clipPath" ||
                !TryResolveClipPathDefinition(
                    clipEl, out _, out string clipPathUnits, out _))
            {
                _report.Warn("clip-path reference unresolved");
                return;
            }

            _activeClipIds ??= new HashSet<string>(System.StringComparer.Ordinal);
            if (_activeClipIds.Contains(fragment) || _activeClipIds.Count >= _maxReferenceDepth)
            {
                _report.Warn("clip-path cycle or depth exceeded; clip ignored");
                return;
            }
            _activeClipIds.Add(fragment);
            try
            {
                bool isObjectBoundingBox = string.Equals(
                    clipPathUnits,
                    "objectBoundingBox",
                    System.StringComparison.Ordinal);

                var clipViewport = isObjectBoundingBox
                    ? new ViewportContext(1f, 1f)
                    : viewport;
                using var clipPath = BuildClipGeometry(clipEl, clipViewport);
                if (clipPath == null || clipPath.IsEmpty)
                {
                    // Empty clip path hides the element entirely (spec).
                    canvas.ClipRect(new SKRect(0f, 0f, 0f, 0f));
                    return;
                }

                if (isObjectBoundingBox)
                {
                    if (!TryResolveObjectBounds(el, viewport, out var bbox))
                    {
                        _report.RequireFallback(
                            $"objectBoundingBox clip on '{el.Name}' requires compatibility fallback");
                        bbox = new SKRect(0f, 0f, viewport.Width, viewport.Height);
                    }
                    using var mapped = new SKPath();
                    clipPath.Transform(SKMatrix.Concat(
                        SKMatrix.CreateTranslation(bbox.Left, bbox.Top),
                        SKMatrix.CreateScale(bbox.Width, bbox.Height)), mapped);
                    canvas.ClipPath(mapped, SKClipOperation.Intersect, antialias: true);
                }
                else
                {
                    canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
                }
            }
            finally
            {
                _activeClipIds.Remove(fragment);
            }
        }

        private bool TryResolveObjectBounds(
            SvgElement element,
            ViewportContext viewport,
            out SKRect bounds)
        {
            CheckDeadline();
            _activeObjectBounds ??= new HashSet<SvgElement>();
            if (element == null || _activeObjectBounds.Contains(element) ||
                _activeObjectBounds.Count >= _maxReferenceDepth)
            {
                _report.Warn("object-bounds reference cycle or depth budget exceeded");
                bounds = default;
                return false;
            }

            _activeObjectBounds.Add(element);
            try
            {
                return TryResolveObjectBoundsCore(element, viewport, out bounds);
            }
            finally
            {
                _activeObjectBounds.Remove(element);
            }
        }

        private bool TryResolveObjectBoundsTransform(
            SvgElement element,
            ViewportContext viewport,
            out SKMatrix matrix)
        {
            CheckDeadline();
            matrix = SKMatrix.Identity;
            if (!TryResolveCssZoomMatrix(element, out var zoom))
            {
                matrix = SKMatrix.Identity;
                return false;
            }
            string cssTransform = element.GetCascadedPresentationProperty("transform");
            if (string.IsNullOrWhiteSpace(cssTransform) &&
                string.IsNullOrWhiteSpace(element.GetAttribute("transform")))
            {
                matrix = zoom;
                return SvgValues.IsFinite(matrix);
            }
            if (!string.IsNullOrWhiteSpace(cssTransform) &&
                cssTransform.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                matrix = zoom;
                return SvgValues.IsFinite(matrix);
            }

            ResolveGeometryFontContext(element, out float fontSize, out float rootFontSize);
            if (!string.IsNullOrWhiteSpace(cssTransform))
            {
                string boxRaw = element.GetPresentationProperty("transform-box");
                if (!TryBuildTransformFillBox(element, viewport, boxRaw, out SKRect? fillBox))
                    return false;
                bool originUsesAttributeSyntax =
                    element.CascadedDeclarations == null ||
                    !element.CascadedDeclarations.ContainsKey("transform-origin");
                var status = SvgCssTransform.TryResolve(
                    cssTransform,
                    element.GetPresentationProperty("transform-origin"),
                    boxRaw,
                    viewport.Width,
                    viewport.Height,
                    fontSize,
                    rootFontSize,
                    fillBox,
                    originUsesAttributeSyntax,
                    out matrix,
                    out _);
                if (status == SvgCssTransformStatus.Unsupported)
                {
                    _report.RequireFallback(
                        $"SVG CSS transform '{cssTransform.Trim()}' requires compatibility fallback");
                    return false;
                }
                matrix = SKMatrix.Concat(zoom, matrix);
                if (!SvgValues.IsFinite(matrix))
                {
                    _report.RequireFallback("SVG transform is not finite and bounded");
                    return false;
                }
                return true;
            }

            string transformText = element.GetAttribute("transform");
            if (string.IsNullOrWhiteSpace(transformText))
            {
                matrix = zoom;
                return SvgValues.IsFinite(matrix);
            }
            if (!SvgValues.TryParseTransformList(transformText.AsSpan(), out matrix))
            {
                _report.RequireFallback("SVG transform attribute requires compatibility fallback");
                return false;
            }

            string originText = element.GetPresentationProperty("transform-origin");
            string boxText = element.GetPresentationProperty("transform-box");
            if (!string.IsNullOrWhiteSpace(originText) || !string.IsNullOrWhiteSpace(boxText))
            {
                if (!TryBuildTransformFillBox(element, viewport, boxText, out SKRect? fillBox))
                    return false;
                bool originUsesAttributeSyntax =
                    element.CascadedDeclarations == null ||
                    !element.CascadedDeclarations.ContainsKey("transform-origin");
                var originStatus = SvgCssTransform.TryResolveReferenceOrigin(
                    originText,
                    boxText,
                    viewport.Width,
                    viewport.Height,
                    fontSize,
                    rootFontSize,
                    fillBox,
                    originUsesAttributeSyntax,
                    out var origin);
                if (originStatus == SvgCssTransformStatus.Unsupported)
                {
                    _report.RequireFallback("SVG transform origin/box requires compatibility fallback");
                    return false;
                }
                if (origin.X != 0f || origin.Y != 0f)
                {
                    matrix = SKMatrix.Concat(
                        SKMatrix.Concat(SKMatrix.CreateTranslation(origin.X, origin.Y), matrix),
                        SKMatrix.CreateTranslation(-origin.X, -origin.Y));
                    if (!SvgValues.TryNormalizeMatrix(matrix, out SKMatrix normalized))
                    {
                        _report.RequireFallback("SVG transform origin composition is not finite");
                        return false;
                    }
                    matrix = normalized;
                }
            }
            matrix = SKMatrix.Concat(zoom, matrix);
            if (!SvgValues.IsFinite(matrix))
            {
                _report.RequireFallback("SVG transform is not finite and bounded");
                return false;
            }
            return true;
        }

        private bool TryResolveObjectBoundsCore(
            SvgElement element,
            ViewportContext viewport,
            out SKRect bounds)
        {
            CheckDeadline();
            switch (element.Name)
            {
                case "path":
                case "rect":
                case "circle":
                case "ellipse":
                case "line":
                case "polyline":
                case "polygon":
                    using (var geometry = BuildGeometry(element, viewport))
                    {
                        if (geometry != null && !geometry.IsEmpty)
                        {
                            bounds = geometry.TightBounds;
                            return SvgValues.IsFinite(bounds.Left) &&
                                SvgValues.IsFinite(bounds.Top) &&
                                SvgValues.IsFinite(bounds.Right) &&
                                SvgValues.IsFinite(bounds.Bottom) &&
                                bounds.Width > 0f && bounds.Height > 0f;
                        }
                    }
                    break;
                case "image":
                    if (TryResolveImageBox(element, viewport, 0f, 0f, out bounds))
                        return true;
                    break;
                case "svg":
                    bounds = new SKRect(0f, 0f, viewport.Width, viewport.Height);
                    return true;
                case "use":
                    string href = element.GetAttribute("href") ?? element.GetLookup("xlink:href");
                    if (SvgValues.TryParseLocalReference(href, out string useId) &&
                        _doc.ElementsById.TryGetValue(useId, out var useTarget) &&
                        useTarget.Name is not ("svg" or "symbol") &&
                        TryResolveObjectBounds(useTarget, viewport, out bounds))
                    {
                        if (!TryResolveObjectBoundsTransform(
                                useTarget, viewport, out var targetTransform) ||
                            !TryResolveObjectBoundsTransform(
                                element, viewport, out _))
                        {
                            bounds = default;
                            return false;
                        }
                        bounds = targetTransform.MapRect(bounds);
                        if (ReferenceEquals(element, _useInstanceSpace))
                        {
                            return SvgValues.IsFinite(bounds.Left) && SvgValues.IsFinite(bounds.Top) &&
                                SvgValues.IsFinite(bounds.Right) && SvgValues.IsFinite(bounds.Bottom) &&
                                bounds.Width > 0f && bounds.Height > 0f;
                        }
                        float useX = ResolveGeometryCoordinate(element, "x", viewport, viewport.Width);
                        float useY = ResolveGeometryCoordinate(element, "y", viewport, viewport.Height);
                        if (useX != 0f || useY != 0f)
                        {
                            if (!SvgValues.IsFinite(useX) || !SvgValues.IsFinite(useY))
                            {
                                bounds = default;
                                return false;
                            }
                            bounds = SKMatrix.CreateTranslation(useX, useY).MapRect(bounds);
                        }
                        return SvgValues.IsFinite(bounds.Left) &&
                            SvgValues.IsFinite(bounds.Top) &&
                            SvgValues.IsFinite(bounds.Right) &&
                            SvgValues.IsFinite(bounds.Bottom) &&
                            bounds.Width > 0f && bounds.Height > 0f;
                    }
                    break;
                case "g":
                case "a":
                    bool hasBounds = false;
                    SKRect combined = default;
                    foreach (var child in element.Children)
                    {
                        CheckDeadline();
                        if (!TryResolveObjectBounds(child, viewport, out var childBounds))
                            continue;

                        SKMatrix childTransform = SKMatrix.Identity;
                        if (!TryResolveObjectBoundsTransform(
                                child, viewport, out childTransform))
                        {
                            bounds = default;
                            return false;
                        }
                        if (!childTransform.IsIdentity)
                            childBounds = childTransform.MapRect(childBounds);

                        combined = hasBounds ? SKRect.Union(combined, childBounds) : childBounds;
                        hasBounds = true;
                    }
                    if (hasBounds && combined.Width > 0f && combined.Height > 0f &&
                        SvgValues.IsFinite(combined.Left) && SvgValues.IsFinite(combined.Top) &&
                        SvgValues.IsFinite(combined.Right) && SvgValues.IsFinite(combined.Bottom))
                    {
                        bounds = combined;
                        return true;
                    }
                    break;
            }

            bounds = default;
            return false;
        }

        private string ResolveInheritedClipRule(
            SvgElement shape,
            SvgElement reference,
            SvgElement clipRoot)
        {
            if (TryGetClipRule(shape, out string rule)) return rule;
            if (reference != null && !ReferenceEquals(reference, shape) &&
                TryGetClipRule(reference, out rule)) return rule;
            for (SvgElement current = shape; current != null; current = current.Parent)
            {
                if (TryGetClipRule(current, out rule)) return rule;
                if (ReferenceEquals(current, clipRoot)) break;
            }
            for (SvgElement current = clipRoot; current != null; current = current.Parent)
                if (TryGetClipRule(current, out rule)) return rule;
            return null;
        }

        private static bool TryGetClipRule(SvgElement element, out string rule)
        {
            rule = element?.GetPresentationProperty("clip-rule");
            if (string.IsNullOrWhiteSpace(rule))
                rule = element?.GetPresentationProperty("fill-rule");
            return !string.IsNullOrWhiteSpace(rule) &&
                (rule.Equals("evenodd", StringComparison.OrdinalIgnoreCase) ||
                 rule.Equals("nonzero", StringComparison.OrdinalIgnoreCase));
        }

        private SKPath BuildClipGeometry(SvgElement clipEl, ViewportContext viewport)
        {
            if (!TryResolveClipPathDefinition(
                    clipEl, out var contentOwner, out _, out var transformOwner) ||
                contentOwner == null)
                return null;

            using var combinedBuilder = new SKPathBuilder();
            bool hasGeometry = false;
            bool evenOdd = false;

            foreach (var child in contentOwner.Children)
            {
                CheckDeadline();
                if (!PassesConditionalProcessing(child)) continue;
                SvgElement shapeEl = child;
                if (child.Name == "use")
                {
                    var href = child.GetAttribute("href") ?? child.GetLookup("xlink:href");
                    if (!SvgValues.TryParseLocalReference(href, out string id) ||
                        !_doc.ElementsById.TryGetValue(id, out var target))
                    {
                        continue;
                    }
                    shapeEl = target;
                }

                switch (shapeEl.Name)
                {
                    case "path":
                    case "rect":
                    case "circle":
                    case "ellipse":
                    case "polygon":
                    case "polyline":
                        break;
                    default:
                        WarnUnsupportedOnce($"clipPath child '{child.Name}'");
                        continue;
                }

                using var childPath = BuildGeometry(shapeEl, viewport);
                if (childPath == null)
                {
                    continue;
                }

                ApplyPathTransform(shapeEl, childPath);
                if (child.Name == "use")
                {
                    float useX = ResolveGeometryCoordinate(child, "x", viewport, viewport.Width);
                    float useY = ResolveGeometryCoordinate(child, "y", viewport, viewport.Height);
                    if (useX != 0f || useY != 0f)
                    {
                        childPath.Transform(SKMatrix.CreateTranslation(useX, useY));
                    }
                    ApplyPathTransform(child, childPath);
                }
                combinedBuilder.AddPath(childPath, SKPathAddMode.Append);
                hasGeometry = true;

                if (string.Equals(
                        ResolveInheritedClipRule(shapeEl, child, contentOwner),
                        "evenodd",
                        StringComparison.OrdinalIgnoreCase))
                {
                    evenOdd = true;
                }
            }

            if (!hasGeometry)
            {
                return null;
            }

            var combined = combinedBuilder.Detach();
            ApplyPathTransform(transformOwner ?? clipEl, combined);
            if (evenOdd)
            {
                combined.FillType = SKPathFillType.EvenOdd;
            }
            return combined;
        }

        private void ApplyPathTransform(SvgElement element, SKPath path)
        {
            string css = element.GetCascadedPresentationProperty("transform");
            if (!string.IsNullOrWhiteSpace(css))
            {
                _report.RequireFallback(
                    "SVG CSS transform on clipPath content requires compatibility fallback");
                return;
            }
            var transformText = element.GetAttribute("transform");
            if (!string.IsNullOrWhiteSpace(transformText))
            {
                if (!SvgValues.TryParseTransformList(transformText.AsSpan(), out var transform))
                {
                    _report.RequireFallback("SVG clipPath transform requires compatibility fallback");
                    return;
                }
                path.Transform(transform);
            }
        }

        private bool TryResolveImageBox(
            SvgElement element,
            ViewportContext viewport,
            float defaultWidth,
            float defaultHeight,
            out SKRect box)
        {
            ResolveGeometryFontContext(element, out float fontSize, out float rootFontSize);
            float x = TryResolveGeometryLength(
                element, "x", viewport, viewport.Width, fontSize, rootFontSize, out float xValue)
                ? xValue
                : 0f;
            float y = TryResolveGeometryLength(
                element, "y", viewport, viewport.Height, fontSize, rootFontSize, out float yValue)
                ? yValue
                : 0f;

            string widthText = element.GetPresentationProperty("width");
            string heightText = element.GetPresentationProperty("height");
            bool widthIsAuto = widthText == null ||
                               widthText.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
            bool heightIsAuto = heightText == null ||
                                heightText.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
            float width;
            if (widthIsAuto)
            {
                width = defaultWidth;
            }
            else if (!TryResolveGeometryLength(
                         element, "width", viewport, viewport.Width, fontSize, rootFontSize,
                         out width))
            {
                RejectsUnresolvedGeometrySizing("image", "width", widthText);
                box = default;
                return false;
            }
            else if (!(width > 0f))
            {
                box = default;
                return false;
            }

            float height;
            if (heightIsAuto)
            {
                height = defaultHeight;
            }
            else if (!TryResolveGeometryLength(
                         element, "height", viewport, viewport.Height, fontSize, rootFontSize,
                         out height))
            {
                RejectsUnresolvedGeometrySizing("image", "height", heightText);
                box = default;
                return false;
            }
            else if (!(height > 0f))
            {
                box = default;
                return false;
            }

            if (widthIsAuto && !heightIsAuto && defaultHeight > 0f)
                width = height * (defaultWidth / defaultHeight);
            else if (heightIsAuto && !widthIsAuto && defaultWidth > 0f)
                height = width * (defaultHeight / defaultWidth);

            if (!SvgValues.IsFinite(x) || !SvgValues.IsFinite(y) ||
                !SvgValues.IsFinite(width) || !SvgValues.IsFinite(height) ||
                width <= 0f || height <= 0f ||
                !SvgValues.IsFinite(x + width) || !SvgValues.IsFinite(y + height))
            {
                box = default;
                return false;
            }
            box = new SKRect(x, y, x + width, y + height);
            return true;
        }
    }
}
