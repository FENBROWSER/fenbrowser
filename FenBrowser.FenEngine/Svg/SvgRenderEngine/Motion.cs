using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        /// <summary>
        /// Applies the deterministic SVG document-time-zero snapshot for the
        /// bounded animateMotion subset. No clock or mutable animation state is
        /// consulted, so picture recording remains repeatable and thread-safe.
        /// </summary>
        private void ApplyInitialMotionTransform(
            SvgElement target,
            SKCanvas canvas,
            ViewportContext viewport)
        {
            SvgElement motion = null;
            for (int i = 0; i < target.Children.Count; i++)
            {
                if (!target.Children[i].Name.Equals("animateMotion", StringComparison.Ordinal)) continue;
                if (motion != null)
                {
                    RequireMotionFallback("multiple animateMotion elements");
                    return;
                }
                motion = target.Children[i];
            }
            if (motion == null) return;

            if (!TryResolveInitialMotionFraction(motion, out float fraction, out bool active))
                return;
            if (!active) return;

            using var path = ResolveMotionPath(motion, viewport);
            if (path == null || path.IsEmpty) return;
            if (!TryMeasureMotionPoint(path, fraction, out SKPoint position, out SKPoint tangent))
                return;

            float degrees = 0f;
            string rotate = motion.GetAttribute("rotate")?.Trim();
            if (!string.IsNullOrEmpty(rotate))
            {
                if (rotate.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                    rotate.Equals("auto-reverse", StringComparison.OrdinalIgnoreCase))
                {
                    degrees = MathF.Atan2(tangent.Y, tangent.X) * (180f / MathF.PI);
                    if (rotate.Equals("auto-reverse", StringComparison.OrdinalIgnoreCase)) degrees += 180f;
                }
                else if (!SvgValues.TryParseNumber(rotate.AsSpan(), out degrees))
                {
                    RequireMotionFallback("animateMotion rotate mode");
                    return;
                }
            }

            canvas.Translate(position.X, position.Y);
            if (degrees != 0f) canvas.RotateDegrees(degrees);
        }

        private bool TryResolveInitialMotionFraction(
            SvgElement motion,
            out float fraction,
            out bool active)
        {
            fraction = 0f;
            active = true;
            if (!IsDefaultMotionAttribute(motion, "additive", "replace") ||
                !IsDefaultMotionAttribute(motion, "accumulate", "none") ||
                !IsDefaultMotionAttribute(motion, "restart", "always"))
            {
                RequireMotionFallback("advanced animateMotion composition");
                return false;
            }

            string begin = motion.GetAttribute("begin")?.Trim();
            if (!string.IsNullOrEmpty(begin))
            {
                if (!TryParseClockSeconds(begin, out double beginSeconds))
                {
                    RequireMotionFallback("event-based animateMotion begin");
                    return false;
                }
                if (beginSeconds > 0d)
                {
                    active = false;
                    return true;
                }
            }

            string duration = motion.GetAttribute("dur")?.Trim();
            if (string.IsNullOrEmpty(duration) ||
                !TryParseClockSeconds(duration, out double durationSeconds) ||
                durationSeconds <= 0d)
            {
                RequireMotionFallback("animateMotion duration");
                return false;
            }

            string keyTimes = motion.GetAttribute("keyTimes")?.Trim();
            float[] parsedKeyTimes = null;
            if (!string.IsNullOrEmpty(keyTimes) &&
                (!TryParseSemicolonNumbers(keyTimes, out parsedKeyTimes) ||
                 parsedKeyTimes.Length < 2 || parsedKeyTimes[0] != 0f ||
                 parsedKeyTimes[^1] != 1f || !IsOrderedUnitInterval(parsedKeyTimes)))
            {
                RequireMotionFallback("animateMotion keyTimes");
                return false;
            }

            string keyPoints = motion.GetAttribute("keyPoints")?.Trim();
            if (!string.IsNullOrEmpty(keyPoints))
            {
                if (parsedKeyTimes == null ||
                    !TryParseSemicolonNumbers(keyPoints, out float[] parsedKeyPoints) ||
                    parsedKeyPoints.Length != parsedKeyTimes.Length ||
                    !IsUnitInterval(parsedKeyPoints))
                {
                    RequireMotionFallback("animateMotion keyPoints");
                    return false;
                }
                fraction = parsedKeyPoints[0];
            }
            return true;
        }

        private SKPath ResolveMotionPath(SvgElement motion, ViewportContext viewport)
        {
            string inlinePath = motion.GetAttribute("path");
            SvgElement mpath = null;
            for (int i = 0; i < motion.Children.Count; i++)
            {
                if (!motion.Children[i].Name.Equals("mpath", StringComparison.Ordinal))
                {
                    RequireMotionFallback("animateMotion child content");
                    return null;
                }
                if (mpath != null)
                {
                    RequireMotionFallback("multiple mpath references");
                    return null;
                }
                mpath = motion.Children[i];
            }
            if (!string.IsNullOrWhiteSpace(inlinePath) && mpath != null)
            {
                RequireMotionFallback("competing animateMotion paths");
                return null;
            }
            if (!string.IsNullOrWhiteSpace(inlinePath))
            {
                return SvgPathParser.TryBuildPath(inlinePath.AsSpan(), out var parsed, _report, CheckTime)
                    ? parsed
                    : null;
            }
            if (mpath == null)
            {
                RequireMotionFallback("animateMotion without a path");
                return null;
            }

            string href = mpath.GetAttribute("href") ?? mpath.GetLookup("xlink:href");
            if (string.IsNullOrWhiteSpace(href)) return null;
            if (href[0] != '#')
            {
                _report.RejectResource("animateMotion external mpath reference rejected");
                return null;
            }
            if (!_doc.ElementsById.TryGetValue(href.Substring(1), out SvgElement referenced)) return null;
            if (referenced.Name is not ("path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon"))
            {
                RequireMotionFallback("animateMotion reference geometry");
                return null;
            }
            if (!string.IsNullOrWhiteSpace(referenced.GetAttribute("transform")) ||
                referenced.CascadedDeclarations?.ContainsKey("transform") == true)
            {
                RequireMotionFallback("transformed animateMotion path");
                return null;
            }
            return BuildGeometry(referenced, viewport);
        }

        private bool TryMeasureMotionPoint(
            SKPath path,
            float fraction,
            out SKPoint position,
            out SKPoint tangent)
        {
            position = default;
            tangent = default;
            var lengths = new List<float>(4);
            float total = 0f;
            using (var measure = new SKPathMeasure(path, false))
            {
                do
                {
                    CheckDeadline();
                    float length = measure.Length;
                    lengths.Add(length);
                    total += length;
                    if (lengths.Count > 1024)
                        throw new SvgSandboxViolationException("SVG animateMotion contour limit exceeded");
                } while (measure.NextContour());
            }
            if (!(total > 0f) || !float.IsFinite(total)) return false;

            float remaining = total * fraction;
            using var lookup = new SKPathMeasure(path, false);
            for (int i = 0; i < lengths.Count; i++)
            {
                CheckDeadline();
                if (remaining <= lengths[i] || i == lengths.Count - 1)
                    return lookup.GetPositionAndTangent(Math.Clamp(remaining, 0f, lengths[i]), out position, out tangent);
                remaining -= lengths[i];
                lookup.NextContour();
            }
            return false;
        }

        private static bool IsDefaultMotionAttribute(SvgElement element, string name, string defaultValue)
        {
            string value = element.GetAttribute(name)?.Trim();
            return string.IsNullOrEmpty(value) || value.Equals(defaultValue, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseClockSeconds(string value, out double seconds)
        {
            seconds = 0d;
            double multiplier = 1d;
            if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 0.001d;
                value = value.Substring(0, value.Length - 2);
            }
            else if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 1);
            }
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) &&
                   double.IsFinite(seconds) && (seconds *= multiplier) >= 0d;
        }

        private static bool TryParseSemicolonNumbers(string value, out float[] numbers)
        {
            numbers = null;
            string[] parts = value.Split(';');
            if (parts.Length == 0 || parts.Length > 1024) return false;
            var parsed = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!SvgValues.TryParseNumber(parts[i].AsSpan().Trim(), out parsed[i]) ||
                    !float.IsFinite(parsed[i])) return false;
            }
            numbers = parsed;
            return true;
        }

        private static bool IsUnitInterval(float[] values)
        {
            for (int i = 0; i < values.Length; i++)
                if (values[i] < 0f || values[i] > 1f) return false;
            return true;
        }

        private static bool IsOrderedUnitInterval(float[] values)
        {
            if (!IsUnitInterval(values)) return false;
            for (int i = 1; i < values.Length; i++)
                if (values[i] < values[i - 1]) return false;
            return true;
        }

        private void RequireMotionFallback(string feature)
        {
            _report.RequireFallback($"SVG {feature} requires compatibility fallback");
        }
    }
}
