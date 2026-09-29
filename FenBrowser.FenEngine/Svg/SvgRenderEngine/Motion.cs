using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
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
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !float.IsFinite(tangent.X) || !float.IsFinite(tangent.Y))
            {
                RequireMotionFallback("animateMotion path sample");
                return;
            }

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
            active = false;
            if (!IsDefaultMotionAttribute(motion, "additive", "replace") ||
                !IsDefaultMotionAttribute(motion, "accumulate", "none") ||
                !IsDefaultMotionAttribute(motion, "restart", "always") ||
                motion.GetAttribute("min") != null ||
                motion.GetAttribute("max") != null ||
                motion.GetAttribute("repeatDur") != null)
            {
                RequireMotionFallback("advanced animateMotion composition");
                return false;
            }

            double beginSeconds = 0d;
            string begin = motion.GetAttribute("begin")?.Trim();
            if (!string.IsNullOrEmpty(begin) && begin.IndexOf(';') < 0 &&
                !TryParseClockSeconds(begin, out beginSeconds))
            {
                RequireMotionFallback("event-based animateMotion begin");
                return false;
            }

            string duration = motion.GetAttribute("dur")?.Trim();
            if (string.IsNullOrEmpty(duration) ||
                !TryParseClockSeconds(duration, out double durationSeconds) ||
                durationSeconds <= 0d)
            {
                RequireMotionFallback("animateMotion duration");
                return false;
            }

            if (!TryBuildSmilClockList(motion, out double[] begins, out double[] ends))
                return false;
            if (begins == null || begins.Length == 0) begins = new[] { beginSeconds };
            if ((long)begins.Length * (ends?.Length ?? 1) > MaxSmilTimingPairs)
            {
                RequireMotionFallback("animateMotion begin timing");
                return false;
            }

            string calcMode = motion.GetAttribute("calcMode")?.Trim();
            bool linear = calcMode == null || calcMode.Length == 0 ||
                calcMode.Equals("linear", StringComparison.OrdinalIgnoreCase);
            bool paced = calcMode?.Equals("paced", StringComparison.OrdinalIgnoreCase) == true;
            bool discrete = calcMode?.Equals("discrete", StringComparison.OrdinalIgnoreCase) == true;
            bool spline = calcMode?.Equals("spline", StringComparison.OrdinalIgnoreCase) == true;
            if (!linear && !paced && !discrete && !spline)
            {
                RequireMotionFallback("animateMotion calculation mode");
                return false;
            }

            float[] parsedKeyTimes = null;
            string keyTimes = motion.GetAttribute("keyTimes")?.Trim();
            if (!string.IsNullOrEmpty(keyTimes))
            {
                if (!TryParseSemicolonNumbers(keyTimes, out parsedKeyTimes) ||
                    parsedKeyTimes.Length < 2 || parsedKeyTimes[0] != 0f ||
                    parsedKeyTimes[^1] != 1f || !IsOrderedUnitInterval(parsedKeyTimes))
                {
                    RequireMotionFallback("animateMotion keyTimes");
                    return false;
                }
            }

            float[] parsedKeyPoints = null;
            string keyPoints = motion.GetAttribute("keyPoints")?.Trim();
            if (!string.IsNullOrEmpty(keyPoints))
            {
                if (parsedKeyTimes == null ||
                    !TryParseSemicolonNumbers(keyPoints, out parsedKeyPoints) ||
                    parsedKeyPoints.Length != parsedKeyTimes.Length ||
                    !IsUnitInterval(parsedKeyPoints))
                {
                    RequireMotionFallback("animateMotion keyPoints");
                    return false;
                }
            }
            else if (discrete && parsedKeyTimes == null && MotionValueCount(motion) > 1)
            {
                int steps = MotionValueCount(motion);
                parsedKeyTimes = new float[steps];
                parsedKeyPoints = new float[steps];
                for (int i = 0; i < steps; i++)
                {
                    parsedKeyTimes[i] = (float)i / steps;
                    parsedKeyPoints[i] = steps == 1 ? 0f : (float)i / (steps - 1);
                }
            }

            float[] parsedSplines = null;
            if (spline)
            {
                int segments = MotionValueCount(motion) - 1;
                if (segments < 1)
                {
                    RequireMotionFallback("animateMotion keySplines");
                    return false;
                }
                if (!TryParseMotionKeySplines(motion, segments, out parsedSplines))
                {
                    RequireMotionFallback("animateMotion keySplines");
                    return false;
                }
            }
            if (paced && (parsedKeyPoints != null || parsedKeyTimes != null))
            {
                RequireMotionFallback("animateMotion calcMode='paced' with key points");
                return false;
            }

            double repeatCount = 1d;
            string repeatRaw = motion.GetAttribute("repeatCount")?.Trim();
            if (!string.IsNullOrEmpty(repeatRaw))
            {
                if (repeatRaw.Equals("indefinite", StringComparison.OrdinalIgnoreCase))
                {
                    repeatCount = double.PositiveInfinity;
                }
                else if (!double.TryParse(
                             repeatRaw,
                             NumberStyles.Float,
                             CultureInfo.InvariantCulture,
                             out repeatCount) ||
                         !double.IsFinite(repeatCount) || repeatCount <= 0d || repeatCount > 1_000_000d)
                {
                    RequireMotionFallback("animateMotion repeat count");
                    return false;
                }
            }

            bool freeze = string.Equals(
                motion.GetAttribute("fill")?.Trim(), "freeze", StringComparison.OrdinalIgnoreCase);
            string fill = motion.GetAttribute("fill")?.Trim();
            if (!string.IsNullOrEmpty(fill) &&
                !fill.Equals("remove", StringComparison.OrdinalIgnoreCase) && !freeze)
            {
                RequireMotionFallback("animateMotion fill mode");
                return false;
            }

            if (!double.IsFinite(_documentTimeSeconds))
            {
                RequireMotionFallback("animateMotion document time");
                return false;
            }
            double totalDuration = repeatCount * durationSeconds;
            if (!(totalDuration > 0d) ||
                (double.IsPositiveInfinity(totalDuration) &&
                 !double.IsPositiveInfinity(repeatCount)))
            {
                RequireMotionFallback("animateMotion total duration");
                return false;
            }
            bool indefiniteActive = double.IsPositiveInfinity(totalDuration);

            double chosenBegin = 0d;
            double chosenEnd = 0d;
            bool foundInside = false;
            bool foundFrozen = false;
            for (int i = 0; i < begins.Length; i++)
            {
                double instanceBegin = begins[i];
                if (instanceBegin > _documentTimeSeconds) continue;
                double instanceEnd = indefiniteActive
                    ? double.PositiveInfinity
                    : instanceBegin + totalDuration;
                if (!double.IsPositiveInfinity(instanceEnd) && ends != null)
                {
                    for (int j = 0; j < ends.Length; j++)
                        if (ends[j] > instanceBegin && ends[j] < instanceEnd)
                            instanceEnd = ends[j];
                }
                bool inside = double.IsPositiveInfinity(instanceEnd) ||
                    _documentTimeSeconds < instanceEnd;
                if (inside)
                {
                    if (!foundInside || instanceBegin >= chosenBegin)
                    {
                        chosenBegin = instanceBegin;
                        chosenEnd = instanceEnd;
                        foundInside = true;
                    }
                    continue;
                }
                if (freeze && (!foundFrozen || instanceBegin > chosenBegin))
                {
                    chosenBegin = instanceBegin;
                    chosenEnd = instanceEnd;
                    foundFrozen = true;
                }
            }

            bool contributing = foundInside || foundFrozen;
            if (!contributing) return true;
            double elapsed = foundInside
                ? _documentTimeSeconds - chosenBegin
                : Math.Max(0d, chosenEnd - chosenBegin);
            if (!double.IsFinite(elapsed))
            {
                RequireMotionFallback("animateMotion elapsed time");
                return false;
            }
            double cycleProgress = elapsed / durationSeconds;
            if (!double.IsFinite(cycleProgress))
            {
                RequireMotionFallback("animateMotion progress");
                return false;
            }
            double progress = foundInside ? cycleProgress % 1d : 1d;
            if (progress < 0d) progress += 1d;
            progress = Math.Clamp(progress, 0d, 1d);
            if (!double.IsFinite(progress))
            {
                RequireMotionFallback("animateMotion progress");
                return false;
            }

            if (parsedSplines != null)
            {
                double segments = parsedSplines.Length / 4d;
                int index = (int)Math.Floor(progress * segments);
                if (index < 0) index = 0;
                if (index >= (int)segments) index = (int)segments - 1;
                float eased = EaseSplineSegment((float)(progress - index / segments) * (float)segments,
                    parsedSplines[index * 4], parsedSplines[index * 4 + 1],
                    parsedSplines[index * 4 + 2], parsedSplines[index * 4 + 3]);
                progress = (index + eased) / segments;
                if (!double.IsFinite(progress))
                {
                    RequireMotionFallback("animateMotion progress");
                    return false;
                }
            }

            if (parsedKeyPoints == null)
            {
                fraction = (float)progress;
                if (!float.IsFinite(fraction))
                {
                    RequireMotionFallback("animateMotion progress");
                    return false;
                }
                active = true;
                return true;
            }

            if (discrete)
            {
                int selected = 0;
                for (int i = 1; i < parsedKeyTimes.Length && progress >= parsedKeyTimes[i]; i++)
                    selected = i;
                fraction = parsedKeyPoints[selected];
                active = true;
                return true;
            }

            int segment = parsedKeyTimes.Length - 2;
            for (int i = 0; i < parsedKeyTimes.Length - 1; i++)
            {
                if (progress <= parsedKeyTimes[i + 1] || i == parsedKeyTimes.Length - 2)
                {
                    segment = i;
                    break;
                }
            }
            float start = parsedKeyTimes[segment];
            float finish = parsedKeyTimes[segment + 1];
            double segmentProgress = finish > start
                ? Math.Clamp((progress - start) / (finish - start), 0d, 1d)
                : 1d;
            if (!double.IsFinite(segmentProgress))
            {
                RequireMotionFallback("animateMotion segment progress");
                return false;
            }
            fraction = (float)(parsedKeyPoints[segment] +
                (parsedKeyPoints[segment + 1] - parsedKeyPoints[segment]) * segmentProgress);
            if (!float.IsFinite(fraction))
            {
                RequireMotionFallback("animateMotion fraction");
                return false;
            }
            active = true;
            return true;
        }

        private static int MotionValueCount(SvgElement motion)
        {
            string values = motion.GetAttribute("values");
            if (values != null)
            {
                if (values.Length > MaxSmilRawTimingChars * 8) return 0;
                string[] parts = values.Split(';', StringSplitOptions.TrimEntries);
                int count = 0;
                for (int i = 0; i < parts.Length; i++)
                    if (parts[i].Length != 0) count++;
                return count;
            }
            if (motion.GetAttribute("from") != null && motion.GetAttribute("to") != null) return 2;
            return 1;
        }

        private bool TryBuildSmilClockList(
            SvgElement animation,
            out double[] begins,
            out double[] ends)
        {
            begins = null;
            ends = null;
            string beginRaw = animation.GetAttribute("begin");
            if (!string.IsNullOrWhiteSpace(beginRaw) &&
                !TryParseSmilClockList(beginRaw, out begins))
            {
                RequireMotionFallback("event-based animateMotion begin");
                return false;
            }
            string endRaw = animation.GetAttribute("end");
            if (!string.IsNullOrWhiteSpace(endRaw) &&
                !TryParseSmilClockList(endRaw, out ends))
            {
                RequireMotionFallback("animateMotion end timing");
                return false;
            }
            return true;
        }

        private static bool TryParseMotionKeySplines(
            SvgElement motion,
            int segmentCount,
            out float[] splines)
        {
            splines = null;
            string raw = motion.GetAttribute("keySplines")?.Trim();
            if (string.IsNullOrEmpty(raw) || raw.Length > MaxSmilRawTimingChars) return false;
            string[] groups = raw.Split(';');
            if (groups.Length != segmentCount) return false;
            var parsed = new float[groups.Length * 4];
            for (int i = 0; i < groups.Length; i++)
            {
                var tokenizer = SvgValues.CreateTokenizer(groups[i].AsSpan());
                for (int c = 0; c < 4; c++)
                {
                    if (!tokenizer.Next(out var token) ||
                        !SvgValues.TryParseNumber(token, out float control) ||
                        !float.IsFinite(control) || control < 0f || control > 1f)
                        return false;
                    parsed[i * 4 + c] = control;
                }
                if (tokenizer.Next(out _)) return false;
            }
            splines = parsed;
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
                return BuildMotionValuePath(motion);
            }

            string href = mpath.GetAttribute("href") ?? mpath.GetLookup("xlink:href");
            if (string.IsNullOrWhiteSpace(href))
            {
                RequireMotionFallback("animateMotion mpath without a reference");
                return null;
            }
            if (!SvgValues.TryParseLocalReference(href, out string id))
            {
                _report.RejectResource("animateMotion external mpath reference rejected");
                return null;
            }
            if (!_doc.ElementsById.TryGetValue(id, out SvgElement referenced))
            {
                RequireMotionFallback("animateMotion mpath reference is unresolved");
                return null;
            }
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

        private SKPath BuildMotionValuePath(SvgElement motion)
        {
            float[] points;
            string valuesRaw = motion.GetAttribute("values");
            if (valuesRaw != null)
            {
                if (valuesRaw.Length > MaxSmilRawTimingChars * 8)
                {
                    RequireMotionFallback("animateMotion values");
                    return null;
                }
                string[] parts = valuesRaw.Split(';', StringSplitOptions.TrimEntries);
                int count = 0;
                for (int i = 0; i < parts.Length; i++)
                    if (parts[i].Length != 0) count++;
                if (count < 2 || count > MaxSmilValueListEntries)
                {
                    RequireMotionFallback("animateMotion without a path");
                    return null;
                }
                points = new float[count * 2];
                int cursor = 0;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Length == 0) continue;
                    if (!TryParseMotionPoint(parts[i], out float x, out float y))
                    {
                        RequireMotionFallback("animateMotion values");
                        return null;
                    }
                    points[cursor++] = x;
                    points[cursor++] = y;
                }
            }
            else
            {
                string from = motion.GetAttribute("from");
                string to = motion.GetAttribute("to");
                if (from == null || to == null ||
                    !TryParseMotionPoint(from, out float startX, out float startY) ||
                    !TryParseMotionPoint(to, out float endX, out float endY))
                {
                    RequireMotionFallback("animateMotion without a path");
                    return null;
                }
                points = new[] { startX, startY, endX, endY };
            }

            using var builder = new SKPathBuilder();
            builder.MoveTo(points[0], points[1]);
            for (int i = 2; i + 1 < points.Length; i += 2)
                builder.LineTo(points[i], points[i + 1]);
            SKPath path = builder.Detach();
            SKRect bounds = path.Bounds;
            if (!float.IsFinite(bounds.Left) || !float.IsFinite(bounds.Top) ||
                !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom))
            {
                path.Dispose();
                RequireMotionFallback("animateMotion values");
                return null;
            }
            return path;
        }

        private static bool TryParseMotionPoint(string raw, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan());
            if (!tokenizer.Next(out var first) || !tokenizer.Next(out var second))
                return false;
            if (tokenizer.Next(out _)) return false;
            if (!SvgValues.TryParseNumber(first, out x) ||
                !SvgValues.TryParseNumber(second, out y))
                return false;
            if (!float.IsFinite(x) || !float.IsFinite(y)) return false;
            x = SvgValues.ClampCoord(x);
            y = SvgValues.ClampCoord(y);
            return true;
        }

        private bool TryMeasureMotionPoint(
            SKPath path,
            float fraction,
            out SKPoint position,
            out SKPoint tangent)
        {
            position = default;
            tangent = default;
            if (!float.IsFinite(fraction)) return false;
            fraction = Math.Clamp(fraction, 0f, 1f);
            var lengths = new List<float>(4);
            float total = 0f;
            using (var measure = new SKPathMeasure(path, false))
            {
                do
                {
                    CheckDeadline();
                    float length = measure.Length;
                    if (!float.IsFinite(length))
                    {
                        RequireMotionFallback("animateMotion path length");
                        return false;
                    }
                    lengths.Add(length);
                    total += length;
                    if (lengths.Count > 1024)
                        throw new SvgSandboxViolationException("SVG animateMotion contour limit exceeded");
                } while (measure.NextContour());
            }
            if (!(total > 0f) || !float.IsFinite(total))
            {
                if (float.IsNaN(total) || float.IsInfinity(total))
                    RequireMotionFallback("animateMotion path length");
                return false;
            }

            float remaining = total * fraction;
            if (!float.IsFinite(remaining))
            {
                RequireMotionFallback("animateMotion path distance");
                return false;
            }
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
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            if (value.StartsWith("-", StringComparison.Ordinal)) return false;
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
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) ||
                !double.IsFinite(seconds) || seconds < 0d)
                return false;
            seconds *= multiplier;
            return double.IsFinite(seconds) && seconds >= 0d;
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
