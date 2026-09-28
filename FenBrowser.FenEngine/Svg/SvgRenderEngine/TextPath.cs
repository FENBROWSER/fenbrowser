using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FenBrowser.FenEngine.Typography;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        private void LayoutTextPathElement(
            SvgElement element,
            ViewportContext viewport,
            InheritedStyle inheritedPaint,
            TextStyle inheritedText,
            TextLayoutState state,
            List<TextPaintRun> runs)
        {
            if (IsDisplayNone(element)) return;
            if (TryResolveRotationList(element) != null)
            {
                _report.RequireFallback(
                    "per-glyph SVG text rotation on textPath requires compatibility fallback");
                return;
            }
            if ((element.TextContent?.Length ?? 0) > MaxTextRenderCharsPerElement)
            {
                _report.RequireFallback("SVG text exceeds first-party render length budget");
                return;
            }

            using var geometry = ResolveTextPathGeometry(element, viewport, out SvgElement pathOwner);
            if (geometry == null) return;

            if (!string.IsNullOrWhiteSpace(element.GetAttribute("method")) ||
                !string.IsNullOrWhiteSpace(element.GetAttribute("spacing")))
            {
                _report.RequireFallback("advanced SVG textPath layout requires compatibility fallback");
                return;
            }
            foreach (var part in element.Content)
            {
                if (part.IsText) continue;
                if (part.Element?.Name is not ("tspan" or "a"))
                {
                    _report.RequireFallback("nested SVG textPath content requires compatibility fallback");
                    return;
                }
            }

            bool reverse = element.GetAttribute("side") is { } side &&
                           side.Trim().Equals("right", StringComparison.OrdinalIgnoreCase);

            using var measure = new SKPathMeasure(geometry, false);
            float pathLength = measure.Length;
            if (!(pathLength > 0f) || !float.IsFinite(pathLength)) return;

            var paintStyle = inheritedPaint.ResolveOverrides(element, _report);
            if (!paintStyle.Visibility) return;
            var textStyle = ResolveTextStyle(element, inheritedText);

            if (textStyle.RightToLeft || textStyle.Frame != null ||
                ContainsBidirectionalText(element.TextContent))
            {
                _report.RequireFallback(
                    "bidirectional SVG text on textPath requires compatibility fallback");
                return;
            }
            if (state.CurrentChunk >= 0) state.PoisonedChunk = state.CurrentChunk;

            int firstRun = runs.Count;
            float originX = state.X;
            if (element.Content.Count > 0)
            {
                var ownedChunks = new List<TextChunk>();
                foreach (var part in element.Content)
                {
                    if (part.IsText)
                    {
                        ShapeTextPart(
                            part.Text, element, paintStyle, textStyle, viewport,
                            state, runs, applyOwnOpacity: true, TextPositionLists.None);
                    }
                    else if (part.Element?.Name == "tspan")
                    {
                        LayoutTextElement(
                            part.Element, viewport, paintStyle, textStyle, state, runs, ownedChunks, false);
                    }
                    else
                    {
                        LayoutTextAnchorElement(
                            part.Element, viewport, paintStyle, textStyle, state, runs, ownedChunks);
                    }
                }
            }

            if (runs.Count == firstRun) return;

            for (int i = firstRun; i < runs.Count; i++)
            {
                if (runs[i].GlyphRotations == null) continue;
                _report.RequireFallback(
                    "per-glyph SVG text rotation on textPath requires compatibility fallback");
                return;
            }

            float offset = ResolveTextPathOffset(element, pathOwner, pathLength, textStyle.FontSize);
            if (!float.IsFinite(offset))
            {
                runs.RemoveRange(firstRun, runs.Count - firstRun);
                return;
            }

            float totalExtent = 0f;
            for (int i = firstRun; i < runs.Count; i++) totalExtent += runs[i].AdvanceExtent;
            offset += textStyle.Anchor == TextAnchor.Middle
                ? -totalExtent / 2f
                : textStyle.Anchor == TextAnchor.End ? -totalExtent : 0f;
            for (int k = firstRun; k < runs.Count; k++)
            {
                TextPaintRun run = runs[k];
                float runOrigin = run.X - originX;
                var glyphIds = new List<ushort>(run.GlyphRun.Count);
                var transforms = new List<SKRotationScaleMatrix>(run.GlyphRun.Count);
                for (int i = 0; i < run.GlyphRun.Count; i++)
                {
                    if ((i & 255) == 0) CheckDeadline();
                    PositionedGlyph glyph = run.GlyphRun.Glyphs[i];
                    float advance = Math.Max(0f, glyph.AdvanceX);
                    float centerDistance = offset + runOrigin + glyph.X + advance / 2f;
                    if (centerDistance < 0f || centerDistance > pathLength) continue;
                    float sampled = reverse ? pathLength - centerDistance : centerDistance;
                    if (sampled < 0f || sampled > pathLength) continue;
                    if (!measure.GetPositionAndTangent(sampled, out SKPoint position, out SKPoint tangent))
                        continue;
                    float degrees = MathF.Atan2(tangent.Y, tangent.X) * (180f / MathF.PI);
                    if (reverse) degrees += 180f;
                    glyphIds.Add(glyph.GlyphId);
                    transforms.Add(SKRotationScaleMatrix.CreateDegrees(
                        1f, degrees, position.X, position.Y, advance / 2f, -glyph.Y));
                }
                if (glyphIds.Count == 0) continue;
                run.PathGlyphIds = glyphIds.ToArray();
                run.PathTransforms = transforms.ToArray();
                run.PathBounds = geometry.Bounds;
                run.Chunk = -1;
            }

            for (int k = runs.Count - 1; k >= firstRun; k--)
            {
                if (runs[k].PathTransforms == null) runs.RemoveAt(k);
            }
        }

        /// <summary>
        /// Resolves the geometry a textPath lays its glyphs on. The SVG 2
        /// <c>path</c> attribute wins over <c>href</c> and is authored directly in
        /// the user space of the textPath; path data that yields no geometry at all
        /// is an error, so the textPath falls back to its <c>href</c> target. A
        /// referenced shape contributes its own geometry plus its own transform,
        /// never an ancestor's.
        /// </summary>
        private SKPath ResolveTextPathGeometry(SvgElement element, ViewportContext viewport, out SvgElement owner)
        {
            owner = null;
            string inlinePath = element.GetAttribute("path");
            if (!string.IsNullOrWhiteSpace(inlinePath))
            {
                string data = inlinePath.Trim();
                if (SvgFeatureSupport.HasExternalUrlReference(data))
                {
                    _report.RejectResource("textPath external reference rejected by SVG resource policy");
                    return null;
                }
                if (SvgPathParser.TryBuildPath(data.AsSpan(), out SKPath parsed, _report, CheckTime) &&
                    !parsed.IsEmpty)
                {
                    return parsed;
                }
                parsed?.Dispose();
            }

            string href = element.GetAttribute("href") ?? element.GetLookup("xlink:href");
            if (string.IsNullOrWhiteSpace(href)) return null;
            if (!SvgValues.TryParseLocalReference(href, out string id))
            {
                _report.RejectResource("textPath external reference rejected by SVG resource policy");
                return null;
            }
            if (!_doc.ElementsById.TryGetValue(id, out SvgElement target))
                return null;
            if (target.Name is not ("path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon"))
            {
                _report.RequireFallback("SVG textPath target geometry requires compatibility fallback");
                return null;
            }

            owner = target;
            string transformRaw = target.GetPresentationProperty("transform");
            SKPath geometry = BuildGeometry(target, viewport);
            if (geometry == null) return null;
            if (string.IsNullOrWhiteSpace(transformRaw)) return geometry;

            if (!SvgValues.TryParseTransformList(transformRaw.AsSpan(), out SKMatrix matrix) ||
                !SvgValues.IsFinite(matrix))
            {
                geometry.Dispose();
                _report.RequireFallback("transformed SVG textPath target requires compatibility fallback");
                return null;
            }
            geometry.Transform(matrix);
            return geometry;
        }

        /// <summary>
        /// Resolves the distance along the path where the first glyph of a textPath
        /// is anchored. A declared <c>pathLength</c> on the target rescales every
        /// startOffset, and it is the basis a percentage is a fraction of, so a
        /// percentage resolves against the declared length when the target declares
        /// one and against the measured length otherwise. The scaling rule that a
        /// <c>pathLength</c> of zero is a factor of infinity applies to both forms: a
        /// zero offset stays zero and any other offset leaves the path entirely.
        /// </summary>
        private static float ResolveTextPathOffset(
            SvgElement textPath,
            SvgElement target,
            float actualLength,
            float fontSize)
        {
            string raw = textPath.GetAttribute("startOffset");
            if (string.IsNullOrWhiteSpace(raw)) return 0f;
            raw = raw.Trim();

            bool percentage = raw.EndsWith("%", StringComparison.Ordinal);
            ReadOnlySpan<char> number = percentage ? raw.AsSpan(0, raw.Length - 1) : raw.AsSpan();
            if (!SvgValues.TryParseLength(number, out float declaredOffset, out var offsetUnit)) return 0f;

            string declaredPathLengthRaw = target?.GetPresentationProperty("path-length") ??
                                          target?.GetAttribute("pathLength");
            bool hasDeclaredPathLength = SvgValues.TryParseLength(
                declaredPathLengthRaw.AsSpan(), out float declaredPathLength, out var pathUnit);
            if (hasDeclaredPathLength)
                declaredPathLength = SvgValues.ResolveUnits(declaredPathLength, pathUnit, fontSize, actualLength);

            if (percentage)
                declaredOffset = declaredOffset * 0.01f * (hasDeclaredPathLength ? declaredPathLength : actualLength);
            else
                declaredOffset = SvgValues.ResolveUnits(declaredOffset, offsetUnit, fontSize, actualLength);

            if (!hasDeclaredPathLength) return declaredOffset;
            if (declaredPathLength == 0f)
                return declaredOffset == 0f ? 0f : MathF.CopySign(float.PositiveInfinity, declaredOffset);
            if (!(declaredPathLength > 0f) || !float.IsFinite(declaredPathLength)) return declaredOffset;
            return declaredOffset * (actualLength / declaredPathLength);
        }
    }
}
