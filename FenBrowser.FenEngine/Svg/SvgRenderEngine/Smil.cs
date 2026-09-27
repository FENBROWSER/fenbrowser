using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        private enum SmilValueKind : byte
        {
            Rejected = 0,
            Color,
            Scalar,
            Discrete,
            Points,
            NumberList,
            PathData,
            Reference
        }

        private static readonly HashSet<string> SmilColorAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "fill", "stroke", "color", "stop-color", "flood-color", "lighting-color"
        };

        private static readonly HashSet<string> SmilScalarAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "x", "y", "width", "height", "cx", "cy", "r", "rx", "ry",
            "x1", "y1", "x2", "y2",
            "stroke-width", "stroke-dashoffset", "stroke-miterlimit",
            "font-size", "letter-spacing", "word-spacing",
            "opacity", "fill-opacity", "stroke-opacity",
            "offset", "startOffset"
        };

        private static readonly HashSet<string> SmilDiscreteAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "display", "visibility", "fill-rule", "clip-rule",
            "stroke-linecap", "stroke-linejoin",
            "text-anchor", "font-style", "font-weight",
            "font-family", "font-variant", "font-stretch",
            "direction", "unicode-bidi", "alignment-baseline",
            "dominant-baseline", "baseline-shift",
            "clipPathUnits", "maskUnits", "markerUnits",
            "patternUnits", "filterUnits", "primitiveUnits",
            "spreadMethod", "over", "d"
        };

        private static readonly HashSet<string> SmilNumberListAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "viewBox"
        };

        private const int MaxSmilTimingListEntries = 256;
        private const int MaxSmilTimingPairs = 4096;
        private const int MaxSmilValueListEntries = 1024;
        private const int MaxSmilListNumbers = 4096;
        private const int MaxSmilRawTimingChars = 8 * 1024;
        private const int MaxSmilSyncbaseDepth = 8;
        private const int SmilEasingIterations = 32;
        private const int MaxSmilTransformNumbers = 6;

        private static readonly HashSet<string> SmilUndeliveredEvents = new(StringComparer.OrdinalIgnoreCase)
        {
            "click", "dblclick", "mousedown", "mouseup", "mouseover", "mouseout",
            "mousemove", "keydown", "keypress", "keyup", "focusin", "focusout",
            "activate", "DOMActivate", "beginEvent", "endEvent", "repeatEvent"
        };

        private Dictionary<SvgElement, SKMatrix> _animatedTransforms;
        private HashSet<SvgElement> _animatedTransformReplacesBase;

        private bool TryGetAnimatedTransformBase(
            SvgElement element,
            out SKMatrix matrix,
            out bool replacesBase)
        {
            matrix = SKMatrix.Identity;
            replacesBase = false;
            if (element == null || _animatedTransforms == null) return false;
            if (!_animatedTransforms.TryGetValue(element, out matrix)) return false;
            replacesBase = _animatedTransformReplacesBase?.Contains(element) == true;
            return true;
        }

        private readonly struct SmilTiming
        {
            public readonly double Duration;
            public readonly double Iterations;
            public readonly double Elapsed;
            public readonly bool Contributing;
            public readonly bool Freeze;

            public SmilTiming(
                double duration,
                double iterations,
                double elapsed,
                bool contributing,
                bool freeze)
            {
                Duration = duration;
                Iterations = iterations;
                Elapsed = elapsed;
                Contributing = contributing;
                Freeze = freeze;
            }
        }

        private void ApplySmilSnapshot(SvgElement root)
        {
            var pending = new Stack<SvgElement>();
            pending.Push(root);
            while (pending.Count != 0)
            {
                CheckDeadline();
                SvgElement element = pending.Pop();
                if (element.Name == "set") ApplySetSnapshot(element);
                else if (element.Name is "animate" or "animateColor")
                    ApplyAnimateSnapshot(element);
                else if (element.Name == "animateTransform")
                    ApplyAnimateTransformSnapshot(element);
                for (int i = element.Children.Count - 1; i >= 0; i--)
                    pending.Push(element.Children[i]);
            }
        }

        private void ApplyAnimateSnapshot(SvgElement animation)
        {
            SvgElement target = ResolveSmilTarget(animation);
            // An interval that is not live at the sampled document time cannot
            // alter the frame, so the sampled value and the target form are not
            // demanded. Only a live interval has to be honoured exactly.
            if (!TryResolveSmilTiming(animation, true, out SmilTiming timing))
                return;
            if (!timing.Contributing) return;
            string attributeName = animation.GetAttribute("attributeName")?.Trim();
            if (target == null || string.IsNullOrEmpty(attributeName) ||
                !TryClassifySmilAttribute(attributeName, out SmilValueKind kind) ||
                IsStructurallyUnsupportedSmilAttribute(attributeName))
            {
                RequireSmilFallback("animate values or target");
                return;
            }
            if (!IsSupportedSmilAttributeType(animation.GetAttribute("attributeType")?.Trim()) ||
                !IsSupportedSmilAdditive(animation.GetAttribute("additive")?.Trim()))
            {
                RequireSmilFallback("animate composition");
                return;
            }
            if (!IsDefaultMotionAttribute(animation, "accumulate", "none") &&
                !(timing.Iterations == 1d &&
                  IsDefaultMotionAttribute(animation, "accumulate", "sum")))
            {
                RequireSmilFallback("animate accumulate mode");
                return;
            }

            string calcMode = animation.GetAttribute("calcMode")?.Trim();
            bool linear = calcMode == null || calcMode.Length == 0 ||
                calcMode.Equals("linear", StringComparison.OrdinalIgnoreCase);
            bool discrete = calcMode?.Equals("discrete", StringComparison.OrdinalIgnoreCase) == true;
            bool paced = calcMode?.Equals("paced", StringComparison.OrdinalIgnoreCase) == true;
            bool spline = calcMode?.Equals("spline", StringComparison.OrdinalIgnoreCase) == true;
            if (!linear && !discrete && !paced && !spline)
            {
                RequireSmilFallback("animate calculation mode");
                return;
            }
            if ((paced || spline) && kind is SmilValueKind.Discrete or SmilValueKind.Points
                    or SmilValueKind.NumberList or SmilValueKind.PathData or SmilValueKind.Reference)
            {
                RequireSmilFallback("animate calculation mode");
                return;
            }
            if (kind == SmilValueKind.PathData && !discrete)
            {
                RequireSmilFallback("animate path data");
                return;
            }

            if (!TryBuildSmilValueList(animation, target, attributeName, kind,
                    out string[] values))
            {
                RequireSmilFallback("animate values");
                return;
            }
            if (paced && values.Length > 2)
            {
                values = new[] { values[0], values[values.Length - 1] };
            }
            if (!TryReadSmilKeyTimes(animation, values.Length, discrete, paced,
                    out float[] keyTimes))
            {
                RequireSmilFallback("animate key times");
                return;
            }
            float[] splines = null;
            if (spline && !TryReadSmilKeySplines(animation, values.Length, out splines))
            {
                RequireSmilFallback("animate key splines");
                return;
            }

            if (!TryComputeSmilProgress(timing, out float progress))
            {
                RequireSmilFallback("animate progress");
                return;
            }

            if (!TrySampleSmilValue(kind, values, keyTimes, splines, discrete, progress,
                    out string value))
            {
                RequireSmilFallback("animate values");
                return;
            }
            if (kind == SmilValueKind.Reference)
            {
                if (string.Equals(animation.GetAttribute("additive")?.Trim(), "sum",
                        StringComparison.OrdinalIgnoreCase))
                {
                    RequireSmilFallback("set additive sum");
                    return;
                }
                TryApplySmilReference(target, attributeName, value);
                return;
            }
            if (string.Equals(animation.GetAttribute("additive")?.Trim(), "sum",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!TryCombineSmilValues(kind,
                        BindSmilCurrentColor(target, kind,
                            ReadUnderlyingValue(target, attributeName)),
                        value, out value))
                {
                    RequireSmilFallback("animate additive sum");
                    return;
                }
            }

            target.AnimatedProperties ??=
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            target.AnimatedProperties[attributeName] = value;
        }

        private void ApplyAnimateTransformSnapshot(SvgElement animation)
        {
            CheckDeadline();
            SvgElement target = ResolveSmilTarget(animation);
            if (!TryResolveSmilTiming(animation, true, out SmilTiming timing))
                return;
            if (!timing.Contributing) return;
            string attributeName = animation.GetAttribute("attributeName")?.Trim();
            if (target == null || attributeName == null ||
                !attributeName.Equals("transform", StringComparison.OrdinalIgnoreCase) ||
                !IsSupportedSmilTransformAttributeType(
                    animation.GetAttribute("attributeType")?.Trim()) ||
                !IsSupportedSmilAdditive(animation.GetAttribute("additive")?.Trim()))
            {
                RequireSmilFallback("animateTransform target or attribute");
                return;
            }
            if (!IsSupportedSmilTransformTarget(target))
            {
                RequireSmilFallback("animateTransform target element");
                return;
            }
            if (!IsDefaultMotionAttribute(animation, "accumulate", "none") &&
                !(timing.Iterations == 1d &&
                  IsDefaultMotionAttribute(animation, "accumulate", "sum")))
            {
                RequireSmilFallback("animateTransform accumulate mode");
                return;
            }
            if (!TryParseSmilTransformKind(animation.GetAttribute("type"), out var kind))
            {
                RequireSmilFallback("animateTransform type");
                return;
            }

            string calcMode = animation.GetAttribute("calcMode")?.Trim();
            bool linear = calcMode == null || calcMode.Length == 0 ||
                calcMode.Equals("linear", StringComparison.OrdinalIgnoreCase);
            bool discrete = calcMode?.Equals("discrete", StringComparison.OrdinalIgnoreCase) == true;
            bool paced = calcMode?.Equals("paced", StringComparison.OrdinalIgnoreCase) == true;
            bool spline = calcMode?.Equals("spline", StringComparison.OrdinalIgnoreCase) == true;
            if (!linear && !discrete && !paced && !spline)
            {
                RequireSmilFallback("animateTransform calculation mode");
                return;
            }

            bool byAnimation = animation.GetAttribute("values") == null &&
                animation.GetAttribute("by") != null;
            if (!TryBuildSmilTransformValues(animation, kind, byAnimation,
                    out float[] tuples, out int count))
            {
                RequireSmilFallback("animateTransform values");
                return;
            }
            if (!TryReadSmilKeyTimes(animation, count, discrete, paced, out float[] keyTimes))
            {
                RequireSmilFallback("animateTransform key times");
                return;
            }
            float[] splines = null;
            if (spline && !TryReadSmilKeySplines(animation, count, out splines))
            {
                RequireSmilFallback("animateTransform key splines");
                return;
            }
            if (!TryComputeSmilProgress(timing, out float progress))
            {
                RequireSmilFallback("animateTransform progress");
                return;
            }
            if (!TrySampleSmilTransform(kind, tuples, count, keyTimes, splines,
                    discrete, paced, progress, out SKMatrix sampled))
            {
                RequireSmilFallback("animateTransform values");
                return;
            }

            bool additiveSum = byAnimation || string.Equals(
                animation.GetAttribute("additive")?.Trim(), "sum",
                StringComparison.OrdinalIgnoreCase);
            ComposeSmilTransform(target, sampled, additiveSum);
        }

        private void ApplySetSnapshot(SvgElement animation)
        {
            SvgElement target = ResolveSmilTarget(animation);
            // A set that is not live at the sampled document time holds nothing,
            // so neither its target form nor its value has to be representable.
            if (!TryResolveSmilTiming(animation, false, out SmilTiming timing))
                return;
            if (!timing.Contributing) return;
            string attributeName = animation.GetAttribute("attributeName")?.Trim();
            string value = animation.GetAttribute("to");
            if (target == null || string.IsNullOrEmpty(attributeName) ||
                !TryClassifySmilAttribute(attributeName, out SmilValueKind kind) ||
                IsStructurallyUnsupportedSmilAttribute(attributeName) ||
                value == null ||
                !IsSupportedSmilAttributeType(animation.GetAttribute("attributeType")?.Trim()) ||
                !IsSupportedSmilAdditive(animation.GetAttribute("additive")?.Trim()))
            {
                RequireSmilFallback("set target or attribute");
                return;
            }

            if (kind == SmilValueKind.Reference)
            {
                if (string.Equals(animation.GetAttribute("additive")?.Trim(), "sum",
                        StringComparison.OrdinalIgnoreCase))
                {
                    RequireSmilFallback("animate additive sum");
                    return;
                }
                TryApplySmilReference(target, attributeName, value);
                return;
            }

            value = BindSmilCurrentColor(target, kind, value);

            if (string.Equals(animation.GetAttribute("additive")?.Trim(), "sum",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!TryCombineSmilValues(kind,
                        BindSmilCurrentColor(target, kind,
                            ReadUnderlyingValue(target, attributeName)),
                        value, out value))
                {
                    RequireSmilFallback("set additive sum");
                    return;
                }
                value = BindSmilCurrentColor(target, kind, value);
            }

            target.AnimatedProperties ??=
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            target.AnimatedProperties[attributeName] = value;
        }

        private SvgElement ResolveSmilTarget(SvgElement animation)
        {
            string href = animation.GetAttribute("href") ?? animation.GetLookup("xlink:href");
            if (string.IsNullOrWhiteSpace(href)) return animation.Parent;
            if (!SvgValues.TryParseLocalReference(href, out string id))
            {
                _report.RejectResource("SVG animation external target rejected");
                return null;
            }
            return _doc.ElementsById.TryGetValue(id, out SvgElement target) ? target : null;
        }

        private bool TryApplySmilReference(
            SvgElement target,
            string attributeName,
            string value)
        {
            if (target.Name != "use")
            {
                RequireSmilFallback("animated reference target");
                return false;
            }
            if (!attributeName.Equals("href", StringComparison.OrdinalIgnoreCase) &&
                ReadUnderlyingValue(target, "href") != null)
            {
                RequireSmilFallback("animated reference precedence");
                return false;
            }
            if (value == null ||
                (!SvgValues.TryParseLocalReference(value, out _) &&
                 !SvgFeatureSupport.ResolvesToNoElement(value)))
            {
                RequireSmilFallback("animated reference resolution");
                return false;
            }
            target.AnimatedProperties ??=
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            target.AnimatedProperties[attributeName] = value;
            target.AnimatedProperties["href"] = value;
            return true;
        }

        private static bool TryClassifySmilAttribute(string name, out SmilValueKind kind)
        {
            if (SmilColorAttributes.Contains(name)) kind = SmilValueKind.Color;
            else if (SmilScalarAttributes.Contains(name)) kind = SmilValueKind.Scalar;
            else if (SmilNumberListAttributes.Contains(name)) kind = SmilValueKind.NumberList;
            else if (name.Equals("points", StringComparison.OrdinalIgnoreCase)) kind = SmilValueKind.Points;
            else if (IsSmilReferenceAttribute(name)) kind = SmilValueKind.Reference;
            else if (SmilDiscreteAttributes.Contains(name))
                kind = name.Equals("d", StringComparison.OrdinalIgnoreCase)
                    ? SmilValueKind.PathData
                    : SmilValueKind.Discrete;
            else
            {
                kind = SmilValueKind.Rejected;
                return false;
            }
            return true;
        }

        private static bool IsSmilReferenceAttribute(string name) =>
            name.Equals("href", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("xlink:href", StringComparison.OrdinalIgnoreCase);

        private static bool IsStructurallyUnsupportedSmilAttribute(string name) =>
            name.Equals("class", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("style", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOf("url", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsSupportedSmilAttributeType(string attributeType) =>
            string.IsNullOrEmpty(attributeType) ||
            attributeType.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
            attributeType.Equals("CSS", StringComparison.OrdinalIgnoreCase) ||
            attributeType.Equals("XML", StringComparison.OrdinalIgnoreCase);

        private static bool IsSupportedSmilAdditive(string additive) =>
            string.IsNullOrEmpty(additive) ||
            additive.Equals("replace", StringComparison.OrdinalIgnoreCase) ||
            additive.Equals("sum", StringComparison.OrdinalIgnoreCase);

        private static string ReadUnderlyingValue(SvgElement target, string attributeName)
        {
            if (target?.CascadedDeclarations != null &&
                target.CascadedDeclarations.TryGetValue(attributeName, out string cascaded) &&
                !string.IsNullOrWhiteSpace(cascaded))
            {
                return cascaded.Trim();
            }
            var attributes = target?.Attributes;
            if (attributes == null) return null;
            for (int i = 0; i < attributes.Length; i++)
            {
                if (!string.Equals(attributes[i].Key, attributeName,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                return string.IsNullOrWhiteSpace(attributes[i].Value)
                    ? null
                    : attributes[i].Value.Trim();
            }
            return null;
        }

        private string ResolveSmilCurrentColor(SvgElement target)
        {
            for (SvgElement current = target; current != null; current = current.Parent)
            {
                string raw = ReadUnderlyingValue(current, "color");
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (raw.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) continue;
                if (SvgValues.TryParseColor(raw.AsSpan(), out SKColor resolved))
                    return $"#{resolved.Red:x2}{resolved.Green:x2}{resolved.Blue:x2}" +
                        $"{resolved.Alpha:x2}";
            }
            return "#000000ff";
        }

        private bool TryResolveSmilTiming(
            SvgElement animation,
            bool requireDuration,
            out SmilTiming timing)
        {
            timing = default;
            if (!double.IsFinite(_documentTimeSeconds))
            {
                RequireSmilFallback("animate document time");
                return false;
            }
            if (!IsDefaultMotionAttribute(animation, "restart", "always") ||
                animation.GetAttribute("min") != null ||
                animation.GetAttribute("max") != null ||
                animation.GetAttribute("repeatDur") != null)
            {
                RequireSmilFallback("advanced animate timing");
                return false;
            }

            bool freeze = false;
            string fillRaw = animation.GetAttribute("fill")?.Trim();
            if (string.IsNullOrEmpty(fillRaw) ||
                fillRaw.Equals("remove", StringComparison.OrdinalIgnoreCase))
                freeze = false;
            else if (fillRaw.Equals("freeze", StringComparison.OrdinalIgnoreCase))
                freeze = true;
            else
            {
                RequireSmilFallback("animate fill mode");
                return false;
            }

            double duration = double.PositiveInfinity;
            string durationRaw = animation.GetAttribute("dur")?.Trim();
            if (string.IsNullOrEmpty(durationRaw) ||
                durationRaw.Equals("indefinite", StringComparison.OrdinalIgnoreCase))
            {
                if (requireDuration)
                {
                    RequireSmilFallback("animate duration");
                    return false;
                }
            }
            else if (!TryParseClockSeconds(durationRaw, out duration) || duration <= 0d)
            {
                RequireSmilFallback("animate duration");
                return false;
            }

            double repeatCount = 1d;
            string repeatRaw = animation.GetAttribute("repeatCount")?.Trim();
            if (!string.IsNullOrEmpty(repeatRaw))
            {
                if (repeatRaw.Equals("indefinite", StringComparison.OrdinalIgnoreCase))
                {
                    repeatCount = double.PositiveInfinity;
                }
                else if (!double.TryParse(repeatRaw, NumberStyles.Float, CultureInfo.InvariantCulture,
                             out repeatCount) ||
                         !double.IsFinite(repeatCount) || repeatCount <= 0d || repeatCount > 1_000_000d)
                {
                    RequireSmilFallback("animate repeat count");
                    return false;
                }
            }

            if (!TryParseSmilTimeList(animation, animation.GetAttribute("begin"), true, 0,
                    out SmilTimeValue[] begins))
            {
                RequireSmilFallback("animate begin timing");
                return false;
            }
            if (begins == null || begins.Length == 0) begins = new[] { SmilClockValue(0d) };

            SmilTimeValue[] ends = null;
            string endRaw = animation.GetAttribute("end");
            if (!string.IsNullOrWhiteSpace(endRaw))
            {
                if (!TryParseSmilTimeList(animation, endRaw, false, 0, out ends) ||
                    ends.Length == 0)
                {
                    RequireSmilFallback("animate end timing");
                    return false;
                }
            }
            if ((long)begins.Length * (ends?.Length ?? 1) > MaxSmilTimingPairs)
            {
                RequireSmilFallback("animate begin timing");
                return false;
            }

            double activeDuration = duration * repeatCount;
            if (double.IsNaN(activeDuration)) activeDuration = 0d;
            bool indefiniteActive = double.IsPositiveInfinity(activeDuration);

            double chosenBegin = 0d;
            double chosenEnd = 0d;
            bool foundInside = false;
            bool foundFrozen = false;

            for (int i = 0; i < begins.Length; i++)
            {
                if (!begins[i].Instant) continue;
                double instanceBegin = begins[i].Seconds;
                if (instanceBegin > _documentTimeSeconds) continue;
                double instanceEnd = indefiniteActive
                    ? double.PositiveInfinity
                    : instanceBegin + activeDuration;
                if (!double.IsPositiveInfinity(instanceEnd) && ends != null)
                {
                    for (int j = 0; j < ends.Length; j++)
                        if (ends[j].Instant && ends[j].Seconds > instanceBegin &&
                            ends[j].Seconds < instanceEnd)
                            instanceEnd = ends[j].Seconds;
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
                if (freeze && (!foundFrozen || instanceBegin > chosenBegin ||
                    (instanceBegin == chosenBegin && instanceEnd > chosenEnd)))
                {
                    chosenBegin = instanceBegin;
                    chosenEnd = instanceEnd;
                    foundFrozen = true;
                }
            }

            bool contributing = foundInside || foundFrozen;
            double elapsed = 0d;
            if (contributing)
                elapsed = foundInside
                    ? _documentTimeSeconds - chosenBegin
                    : Math.Max(0d, chosenEnd - chosenBegin);

            timing = new SmilTiming(
                duration, repeatCount, elapsed, contributing, freeze && foundFrozen && !foundInside);
            return true;
        }

        private readonly struct SmilTimeValue
        {
            public readonly double Seconds;
            public readonly bool Instant;
            public readonly bool InError;

            public SmilTimeValue(double seconds, bool instant, bool inError)
            {
                Seconds = seconds;
                Instant = instant;
                InError = inError;
            }
        }

        private static SmilTimeValue SmilClockValue(double seconds) =>
            new(seconds, true, false);

        private static SmilTimeValue SmilPendingValue() => new(0d, false, false);

        private static SmilTimeValue SmilInErrorValue() => new(0d, false, true);

        private static bool TryParseSmilClockList(string raw, out double[] times)
        {
            times = null;
            if (string.IsNullOrWhiteSpace(raw)) return true;
            if (raw.Length > MaxSmilRawTimingChars) return false;
            string[] parts = raw.Split(';');
            if (parts.Length == 0 || parts.Length > MaxSmilTimingListEntries) return false;
            var parsed = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!TryParseClockSeconds(parts[i], out parsed[i]))
                    return false;
            times = parsed;
            return true;
        }

        private bool TryParseSmilTimeList(
            SvgElement owner,
            string raw,
            bool isBegin,
            int depth,
            out SmilTimeValue[] values)
        {
            values = null;
            if (string.IsNullOrWhiteSpace(raw)) return true;
            if (raw.Length > MaxSmilRawTimingChars) return false;
            string[] parts = raw.Split(';');
            if (parts.Length == 0 || parts.Length > MaxSmilTimingListEntries) return false;
            var parsed = new SmilTimeValue[parts.Length];
            int instantCount = 0;
            bool hasInError = false;
            for (int i = 0; i < parts.Length; i++)
            {
                if (!TryResolveSmilTimeValue(owner, parts[i], depth, out parsed[i])) return false;
                if (parsed[i].Instant) instantCount++;
                else if (parsed[i].InError) hasInError = true;
            }
            if (isBegin && instantCount == 0 && hasInError) return false;
            values = parsed;
            return true;
        }

        private bool TryResolveSmilTimeValue(
            SvgElement owner,
            string raw,
            int depth,
            out SmilTimeValue value)
        {
            value = SmilPendingValue();
            string text = raw?.Trim() ?? string.Empty;
            if (text.Length == 0 || text.Length > MaxSmilRawTimingChars) return false;
            if (TryParseClockSeconds(text, out double seconds))
            {
                value = SmilClockValue(seconds);
                return true;
            }
            if (text.Equals("indefinite", StringComparison.OrdinalIgnoreCase)) return true;
            if (depth >= MaxSmilSyncbaseDepth) return false;

            string head = TrySplitSmilOffset(text, out string offsetHead, out double offset)
                ? offsetHead
                : text;
            if (head.Length == 0) return false;
            if (IsSmilFunctionalTimeValue(head, "wallclock")) return false;
            if (IsSmilFunctionalTimeValue(head, "repeat")) return false;
            if (IsSmilFunctionalTimeValue(head, "accessKey")) return true;

            int dot = head.IndexOf('.');
            if (dot < 0)
            {
                if (!SmilUndeliveredEvents.Contains(head)) return false;
                return true;
            }
            string id = head.Substring(0, dot).Trim();
            string token = head.Substring(dot + 1).Trim();
            if (id.Length == 0 || token.Length == 0 || token.IndexOf('.') >= 0) return false;
            if (IsSmilFunctionalTimeValue(token, "repeat")) return false;
            if (SmilUndeliveredEvents.Contains(token))
                return IsSmilSyncbaseTargetInDocument(id) || SetSmilInError(out value);
            bool wantsEnd;
            if (token.Equals("begin", StringComparison.OrdinalIgnoreCase)) wantsEnd = false;
            else if (token.Equals("end", StringComparison.OrdinalIgnoreCase)) wantsEnd = true;
            else return false;
            return TryResolveSmilSyncbase(owner, id, wantsEnd, offset, depth, out value);
        }

        private bool IsSmilSyncbaseTargetInDocument(string id) =>
            _doc.ElementsById.ContainsKey(id);

        private static bool SetSmilInError(out SmilTimeValue value)
        {
            value = SmilInErrorValue();
            return true;
        }

        private static bool SetSmilPending(out SmilTimeValue value)
        {
            value = SmilPendingValue();
            return true;
        }

        private bool TryResolveSmilSyncbase(
            SvgElement owner,
            string id,
            bool wantsEnd,
            double offset,
            int depth,
            out SmilTimeValue value)
        {
            if (!_doc.ElementsById.TryGetValue(id, out SvgElement referenced) ||
                ReferenceEquals(referenced, owner) ||
                !IsSmilTimingElement(referenced))
            {
                value = SmilInErrorValue();
                return true;
            }
            if (!TryCollectSmilInterval(referenced, depth + 1,
                    out double intervalBegin, out double intervalEnd, out bool hasAnchor))
            {
                value = SmilPendingValue();
                return false;
            }
            if (!hasAnchor) return SetSmilPending(out value);
            double anchor = wantsEnd ? intervalEnd : intervalBegin;
            if (double.IsPositiveInfinity(anchor))
            {
                value = SmilClockValue(double.PositiveInfinity);
                return true;
            }
            if (!double.IsFinite(anchor))
            {
                value = SmilPendingValue();
                return false;
            }
            double instant = anchor + offset;
            if (instant < 0d) return SetSmilInError(out value);
            value = SmilClockValue(instant);
            return true;
        }

        private bool TryCollectSmilInterval(
            SvgElement referenced,
            int depth,
            out double begin,
            out double end,
            out bool hasAnchor)
        {
            begin = 0d;
            end = 0d;
            hasAnchor = false;
            if (referenced.GetAttribute("min") != null ||
                referenced.GetAttribute("max") != null ||
                referenced.GetAttribute("repeatDur") != null ||
                !TryParseSmilTimeList(referenced, referenced.GetAttribute("begin"), true, depth,
                    out SmilTimeValue[] begins))
            {
                return false;
            }
            if (begins == null || begins.Length == 0) begins = new[] { SmilClockValue(0d) };
            int instantCount = 0;
            double onlyBegin = 0d;
            for (int i = 0; i < begins.Length; i++)
            {
                if (!begins[i].Instant) continue;
                instantCount++;
                onlyBegin = begins[i].Seconds;
            }
            if (instantCount == 0) return true;
            if (instantCount > 1) return false;

            begin = onlyBegin;
            hasAnchor = true;
            if (!TryResolveSmilActiveDuration(referenced, out double activeDuration,
                    out bool indefinite))
            {
                return false;
            }
            if (indefinite)
            {
                end = double.PositiveInfinity;
                return true;
            }
            double instanceEnd = onlyBegin + activeDuration;
            if (!double.IsFinite(instanceEnd))
            {
                end = double.PositiveInfinity;
                return true;
            }
            if (!TryParseSmilTimeList(referenced, referenced.GetAttribute("end"), false, depth,
                    out SmilTimeValue[] ends))
            {
                return false;
            }
            if (ends != null)
            {
                for (int i = 0; i < ends.Length; i++)
                {
                    if (!ends[i].Instant) continue;
                    if (ends[i].Seconds > onlyBegin && ends[i].Seconds < instanceEnd)
                        instanceEnd = ends[i].Seconds;
                }
            }
            end = instanceEnd;
            return true;
        }

        private static bool TryResolveSmilActiveDuration(
            SvgElement element,
            out double activeDuration,
            out bool indefinite)
        {
            activeDuration = 0d;
            indefinite = false;
            string durationRaw = element.GetAttribute("dur")?.Trim();
            if (string.IsNullOrEmpty(durationRaw) ||
                durationRaw.Equals("indefinite", StringComparison.OrdinalIgnoreCase))
            {
                indefinite = true;
                return true;
            }
            if (!TryParseClockSeconds(durationRaw, out double duration) ||
                !double.IsFinite(duration) || duration <= 0d)
                return false;
            string repeatRaw = element.GetAttribute("repeatCount")?.Trim();
            if (!string.IsNullOrEmpty(repeatRaw))
            {
                if (repeatRaw.Equals("indefinite", StringComparison.OrdinalIgnoreCase)) return false;
                if (!double.TryParse(repeatRaw, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double repeatCount) ||
                    !double.IsFinite(repeatCount) || repeatCount <= 0d || repeatCount > 1_000_000d ||
                    repeatCount != 1d)
                    return false;
            }
            activeDuration = duration;
            return true;
        }

        private static bool IsSmilTimingElement(SvgElement element) =>
            element != null &&
            element.Name is "set" or "animate" or "animateColor" or
                "animateTransform" or "animateMotion";

        private static bool IsSmilFunctionalTimeValue(string value, string name) =>
            value.Length > name.Length + 1 &&
            value.EndsWith(")", StringComparison.Ordinal) &&
            value.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
            value[name.Length] == '(';

        private static bool TrySplitSmilOffset(string text, out string head, out double offset)
        {
            head = text;
            offset = 0d;
            for (int i = text.Length - 1; i > 0; i--)
            {
                char sign = text[i];
                if (sign != '+' && sign != '-') continue;
                if (!TryParseSmilOffsetSeconds(text.Substring(i + 1).Trim(),
                        sign == '-' ? -1d : 1d, out offset)) continue;
                head = text.Substring(0, i).Trim();
                return true;
            }
            return false;
        }

        private static bool TryParseSmilOffsetSeconds(string raw, double sign, out double seconds)
        {
            seconds = 0d;
            if (!TryParseClockSeconds(raw, out double magnitude)) return false;
            seconds = magnitude * sign;
            return double.IsFinite(seconds);
        }

        private static bool TryReadSmilKeyTimes(
            SvgElement animation,
            int valueCount,
            bool discrete,
            bool paced,
            out float[] keyTimes)
        {
            keyTimes = null;
            if (paced) return true;
            string keyTimesRaw = animation.GetAttribute("keyTimes")?.Trim();
            if (string.IsNullOrEmpty(keyTimesRaw)) return true;
            if (!TryParseSemicolonNumbers(keyTimesRaw, out float[] parsed) ||
                parsed.Length != valueCount || parsed[0] != 0f ||
                !IsOrderedUnitInterval(parsed) || (!discrete && parsed[^1] != 1f))
                return false;
            keyTimes = parsed;
            return true;
        }

        private static bool TryReadSmilKeySplines(
            SvgElement animation,
            int valueCount,
            out float[] splines)
        {
            splines = null;
            string raw = animation.GetAttribute("keySplines")?.Trim();
            if (string.IsNullOrEmpty(raw) || raw.Length > MaxSmilRawTimingChars) return false;
            string[] groups = raw.Split(';');
            if (groups.Length != valueCount - 1 || groups.Length == 0) return false;
            if (groups.Length > MaxSmilTimingListEntries) return false;
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

        private bool TryBuildSmilValueList(
            SvgElement animation,
            SvgElement target,
            string attributeName,
            SmilValueKind kind,
            out string[] values)
        {
            values = null;
            string valuesRaw = animation.GetAttribute("values");
            if (valuesRaw != null)
            {
                values = SplitSmilValueList(valuesRaw);
                if (values == null) return false;
                for (int i = 0; i < values.Length; i++)
                    values[i] = BindSmilCurrentColor(target, kind, values[i]);
                return true;
            }

            string from = BindSmilCurrentColor(target, kind, animation.GetAttribute("from"));
            string to = BindSmilCurrentColor(target, kind, animation.GetAttribute("to"));
            string by = BindSmilCurrentColor(target, kind, animation.GetAttribute("by"));
            if (from == null && to == null && by == null) return false;

            string baseValue = null;
            if (by != null || (from == null && to != null))
            {
                baseValue = BindSmilCurrentColor(target, kind,
                    from ?? ReadUnderlyingValue(target, attributeName));
                if (string.IsNullOrWhiteSpace(baseValue)) return false;
            }

            if (by != null)
            {
                string start = from ?? baseValue;
                if (!TryCombineSmilValues(kind, start, by, out string finish))
                    return false;
                values = new[] { start, finish };
                return true;
            }
            if (from != null && to != null)
            {
                values = new[] { from, to };
                return true;
            }
            values = new[] { baseValue, to };
            return true;
        }

        private static string[] SplitSmilValueList(string raw)
        {
            if (raw.Length > 64 * 1024) return null;
            string[] parts = raw.Split(';');
            int count = parts.Length;
            if (count > 1 && parts[count - 1].Trim().Length == 0) count--;
            if (count == 0 || count > MaxSmilValueListEntries) return null;
            var values = new string[count];
            for (int i = 0; i < count; i++)
            {
                string value = parts[i].Trim();
                if (value.Length == 0) return null;
                values[i] = value;
            }
            return values;
        }

        private static bool IsDiscreteSmilKind(SmilValueKind kind) =>
            kind is SmilValueKind.Discrete or SmilValueKind.PathData or SmilValueKind.Reference;

        private static bool TryComputeSmilProgress(SmilTiming timing, out float progress)
        {
            progress = 0f;
            double iterationsElapsed = timing.Elapsed / timing.Duration;
            if (!double.IsFinite(iterationsElapsed) || iterationsElapsed < 0d) return false;
            double completed = Math.Floor(iterationsElapsed);
            progress = (float)Math.Clamp(iterationsElapsed - completed, 0d, 1d);
            if (timing.Freeze && Math.Abs(progress) <= 1e-12d && iterationsElapsed > 0d)
                progress = 1f;
            return true;
        }

        private static bool TrySampleSmilValue(
            SmilValueKind kind,
            string[] values,
            float[] keyTimes,
            float[] splines,
            bool discrete,
            float progress,
            out string value)
        {
            value = null;
            if (values.Length == 1)
            {
                value = values[0];
                return true;
            }

            if (discrete || IsDiscreteSmilKind(kind))
            {
                int selected;
                if (keyTimes == null)
                {
                    int step = (int)MathF.Floor(progress * values.Length);
                    selected = step < 0 ? 0 : Math.Min(step, values.Length - 1);
                }
                else
                {
                    selected = 0;
                    for (int i = 1; i < keyTimes.Length && progress >= keyTimes[i]; i++)
                        selected = i;
                }
                value = values[selected];
                return true;
            }

            int segment = values.Length - 2;
            float start = 0f;
            float finish = 0f;
            for (int i = 0; i < values.Length - 1; i++)
            {
                float segmentStart = keyTimes?[i] ?? (float)i / (values.Length - 1);
                float segmentFinish = keyTimes?[i + 1] ?? (float)(i + 1) / (values.Length - 1);
                if (progress > segmentFinish && i < values.Length - 2) continue;
                segment = i;
                start = segmentStart;
                finish = segmentFinish;
                break;
            }
            if (!(finish > start)) return false;
            float local = Math.Clamp((progress - start) / (finish - start), 0f, 1f);
            if (splines != null)
            {
                local = EaseSplineSegment(local,
                    splines[segment * 4], splines[segment * 4 + 1],
                    splines[segment * 4 + 2], splines[segment * 4 + 3]);
            }
            return TryInterpolateSmilValue(kind, values[segment], values[segment + 1], local,
                out value);
        }

        private static bool TryInterpolateSmilValue(
            SmilValueKind kind,
            string from,
            string to,
            float progress,
            out string value)
        {
            value = null;
            switch (kind)
            {
                case SmilValueKind.Color:
                    return TryInterpolateSmilColor(from, to, progress, out value);
                case SmilValueKind.Scalar:
                    return TryInterpolateSmilScalar(from, to, progress, out value);
                case SmilValueKind.Points:
                    return TryInterpolateSmilPoints(from, to, progress, out value);
                case SmilValueKind.NumberList:
                    return TryInterpolateSmilNumberList(from, to, progress, out value);
                default:
                    return false;
            }
        }

        private static bool TryResolveSmilColor(string raw, out SKColor color)
        {
            color = default;
            return !string.IsNullOrWhiteSpace(raw) &&
                SvgValues.TryParseColor(raw.AsSpan(), out color);
        }

        private string BindSmilCurrentColor(SvgElement target, SmilValueKind kind, string raw)
        {
            if (kind != SmilValueKind.Color || raw == null) return raw;
            string value = raw.Trim();
            if (value.IndexOf("currentColor", StringComparison.OrdinalIgnoreCase) < 0)
                return value;
            string resolved = ResolveSmilCurrentColor(target);
            if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
                return resolved;
            if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "transparent", StringComparison.OrdinalIgnoreCase))
                return value;
            return resolved;
        }

        private static bool TryInterpolateSmilColor(
            string from,
            string to,
            float progress,
            out string value)
        {
            value = null;
            if (!TryResolveSmilColor(from, out SKColor start) ||
                !TryResolveSmilColor(to, out SKColor finish))
                return false;
            byte Lerp(byte a, byte b) => (byte)Math.Clamp(
                (int)MathF.Round(a + (b - a) * progress), 0, 255);
            value = $"#{Lerp(start.Red, finish.Red):x2}{Lerp(start.Green, finish.Green):x2}" +
                $"{Lerp(start.Blue, finish.Blue):x2}{Lerp(start.Alpha, finish.Alpha):x2}";
            return true;
        }

        private static bool TryAbsoluteUnitFactor(SvgValues.SvgUnit unit, out float factor)
        {
            switch (unit)
            {
                case SvgValues.SvgUnit.User:
                case SvgValues.SvgUnit.Px:
                    factor = 1f;
                    return true;
                case SvgValues.SvgUnit.Pt:
                    factor = 96f / 72f;
                    return true;
                case SvgValues.SvgUnit.Pc:
                    factor = 16f;
                    return true;
                case SvgValues.SvgUnit.Mm:
                    factor = 96f / 25.4f;
                    return true;
                case SvgValues.SvgUnit.Cm:
                    factor = 960f / 25.4f;
                    return true;
                case SvgValues.SvgUnit.In:
                    factor = 96f;
                    return true;
                default:
                    factor = 0f;
                    return false;
            }
        }

        private static bool TryInterpolateSmilScalar(
            string from,
            string to,
            float progress,
            out string value)
        {
            value = null;
            if (!SvgValues.TryParseLength(from.AsSpan(), out float start, out var startUnit) ||
                !SvgValues.TryParseLength(to.AsSpan(), out float end, out var endUnit))
                return false;
            if (startUnit == endUnit)
            {
                float plain = start + (end - start) * progress;
                if (!float.IsFinite(plain)) return false;
                value = plain.ToString("R", CultureInfo.InvariantCulture) +
                    SmilUnitSuffix(startUnit);
                return true;
            }
            if (!TryAbsoluteUnitFactor(startUnit, out float startFactor) ||
                !TryAbsoluteUnitFactor(endUnit, out float endFactor))
                return false;
            float pixels = (start * startFactor) +
                (end * endFactor - start * startFactor) * progress;
            if (!float.IsFinite(pixels)) return false;
            value = pixels.ToString("R", CultureInfo.InvariantCulture) + "px";
            return true;
        }

        private static bool TryInterpolateSmilPoints(
            string from,
            string to,
            float progress,
            out string value)
        {
            value = null;
            if (!TryParseSmilPointList(from, out float[] start) ||
                !TryParseSmilPointList(to, out float[] finish))
                return false;
            if (start.Length != finish.Length)
            {
                value = progress < 1f ? from.Trim() : to.Trim();
                return true;
            }
            var blended = new float[start.Length];
            for (int i = 0; i < start.Length; i++)
            {
                float mixed = start[i] + (finish[i] - start[i]) * progress;
                if (!float.IsFinite(mixed)) return false;
                blended[i] = SvgValues.ClampCoord(mixed);
            }
            value = FormatSmilPointList(blended);
            return true;
        }

        private static bool TryInterpolateSmilNumberList(
            string from,
            string to,
            float progress,
            out string value)
        {
            value = null;
            if (!TryParseSmilNumberList(from, out float[] start) ||
                !TryParseSmilNumberList(to, out float[] finish))
                return false;
            if (start.Length != finish.Length)
            {
                value = progress < 1f ? from.Trim() : to.Trim();
                return true;
            }
            var blended = new float[start.Length];
            for (int i = 0; i < start.Length; i++)
            {
                float mixed = start[i] + (finish[i] - start[i]) * progress;
                if (!float.IsFinite(mixed)) return false;
                blended[i] = mixed;
            }
            var builder = new StringBuilder(blended.Length * 6);
            for (int i = 0; i < blended.Length; i++)
            {
                if (i != 0) builder.Append(' ');
                builder.Append(blended[i].ToString("R", CultureInfo.InvariantCulture));
            }
            value = builder.ToString();
            return true;
        }

        private static bool TryCombineSmilValues(
            SmilValueKind kind,
            string baseValue,
            string delta,
            out string value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(baseValue) || string.IsNullOrWhiteSpace(delta))
                return false;
            switch (kind)
            {
                case SmilValueKind.Color:
                    {
                        if (!SvgValues.TryParseColor(baseValue.AsSpan(), out SKColor start) ||
                            !SvgValues.TryParseColor(delta.AsSpan(), out SKColor finish))
                            return false;
                        byte Add(byte a, byte b) => (byte)Math.Clamp(a + b, 0, 255);
                        value = $"#{Add(start.Red, finish.Red):x2}{Add(start.Green, finish.Green):x2}" +
                            $"{Add(start.Blue, finish.Blue):x2}{Add(start.Alpha, finish.Alpha):x2}";
                        return true;
                    }
                case SmilValueKind.Scalar:
                    {
                        if (!SvgValues.TryParseLength(baseValue.AsSpan(), out float start,
                                out var startUnit) ||
                            !SvgValues.TryParseLength(delta.AsSpan(), out float finish,
                                out var finishUnit))
                            return false;
                        if (startUnit != finishUnit) return false;
                        float total = start + finish;
                        if (!float.IsFinite(total)) return false;
                        value = total.ToString("R", CultureInfo.InvariantCulture) +
                            SmilUnitSuffix(startUnit);
                        return true;
                    }
                case SmilValueKind.Points:
                    {
                        if (!TryParseSmilPointList(baseValue, out float[] startPoints) ||
                            !TryParseSmilPointList(delta, out float[] finishPoints) ||
                            startPoints.Length != finishPoints.Length)
                            return false;
                        var summed = new float[startPoints.Length];
                        for (int i = 0; i < summed.Length; i++)
                        {
                            float total = startPoints[i] + finishPoints[i];
                            if (!float.IsFinite(total)) return false;
                            summed[i] = SvgValues.ClampCoord(total);
                        }
                        value = FormatSmilPointList(summed);
                        return true;
                    }
                case SmilValueKind.NumberList:
                    {
                        if (!TryParseSmilNumberList(baseValue, out float[] startList) ||
                            !TryParseSmilNumberList(delta, out float[] finishList) ||
                            startList.Length != finishList.Length)
                            return false;
                        var builder = new StringBuilder(startList.Length * 6);
                        for (int i = 0; i < startList.Length; i++)
                        {
                            float total = startList[i] + finishList[i];
                            if (!float.IsFinite(total)) return false;
                            if (i != 0) builder.Append(' ');
                            builder.Append(total.ToString("R", CultureInfo.InvariantCulture));
                        }
                        value = builder.ToString();
                        return true;
                    }
                default:
                    return false;
            }
        }

        private static bool TryParseSmilPointList(string raw, out float[] values)
        {
            values = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var parsed = new List<float>(16);
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan());
            while (tokenizer.Next(out var token))
            {
                if (!SvgValues.TryParseNumber(token, out float number) ||
                    !float.IsFinite(number))
                    return false;
                parsed.Add(number);
                if (parsed.Count > MaxSmilListNumbers) return false;
            }
            if (parsed.Count < 2 || (parsed.Count & 1) != 0) return false;
            values = parsed.ToArray();
            return true;
        }

        private static bool TryParseSmilNumberList(string raw, out float[] values)
        {
            values = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var parsed = new List<float>(8);
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan());
            while (tokenizer.Next(out var token))
            {
                if (!SvgValues.TryParseNumber(token, out float number) ||
                    !float.IsFinite(number))
                    return false;
                parsed.Add(number);
                if (parsed.Count > MaxSmilListNumbers) return false;
            }
            if (parsed.Count == 0) return false;
            values = parsed.ToArray();
            return true;
        }

        private static string FormatSmilPointList(float[] values)
        {
            var builder = new StringBuilder(values.Length * 8);
            for (int i = 0; i + 1 < values.Length; i += 2)
            {
                if (i != 0) builder.Append(' ');
                builder.Append(values[i].ToString("R", CultureInfo.InvariantCulture));
                builder.Append(' ');
                builder.Append(values[i + 1].ToString("R", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static float EaseSplineSegment(float progress, float x1, float y1, float x2, float y2)
        {
            float low = 0f;
            float high = 1f;
            for (int i = 0; i < SmilEasingIterations; i++)
            {
                float mid = (low + high) * 0.5f;
                if (CubicBezierCoordinate(mid, x1, x2) < progress) low = mid;
                else high = mid;
            }
            return CubicBezierCoordinate((low + high) * 0.5f, y1, y2);
        }

        private static float CubicBezierCoordinate(float u, float first, float second)
        {
            float inverse = 1f - u;
            return 3f * inverse * inverse * u * first +
                3f * inverse * u * u * second +
                u * u * u;
        }

        private static string SmilUnitSuffix(SvgValues.SvgUnit unit) => unit switch
        {
            SvgValues.SvgUnit.Px => "px",
            SvgValues.SvgUnit.Pt => "pt",
            SvgValues.SvgUnit.Pc => "pc",
            SvgValues.SvgUnit.Mm => "mm",
            SvgValues.SvgUnit.Cm => "cm",
            SvgValues.SvgUnit.In => "in",
            SvgValues.SvgUnit.Em => "em",
            SvgValues.SvgUnit.Ex => "ex",
            SvgValues.SvgUnit.Percent => "%",
            _ => string.Empty
        };

        private enum SmilTransformKind : byte
        {
            Rejected = 0,
            Translate,
            Scale,
            Rotate,
            SkewX,
            SkewY,
            Matrix
        }

        private static bool IsSupportedSmilTransformAttributeType(string attributeType) =>
            !string.Equals(attributeType, "CSS", StringComparison.OrdinalIgnoreCase) &&
            IsSupportedSmilAttributeType(attributeType);

        private static bool IsSupportedSmilTransformTarget(SvgElement target) =>
            target.Name is
                "g" or "a" or "use" or "svg" or "image" or "text" or "switch" or
                "view" or "path" or "rect" or "circle" or "ellipse" or "line" or
                "polyline" or "polygon";

        private static bool TryParseSmilTransformKind(string raw, out SmilTransformKind kind)
        {
            string value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                kind = SmilTransformKind.Rejected;
                return false;
            }
            if (value.Equals("translate", StringComparison.OrdinalIgnoreCase))
                kind = SmilTransformKind.Translate;
            else if (value.Equals("scale", StringComparison.OrdinalIgnoreCase))
                kind = SmilTransformKind.Scale;
            else if (value.Equals("rotate", StringComparison.OrdinalIgnoreCase))
                kind = SmilTransformKind.Rotate;
            else if (value.Equals("skewX", StringComparison.OrdinalIgnoreCase))
                kind = SmilTransformKind.SkewX;
            else if (value.Equals("skewY", StringComparison.OrdinalIgnoreCase))
                kind = SmilTransformKind.SkewY;
            else if (value.Equals("matrix", StringComparison.OrdinalIgnoreCase))
                kind = SmilTransformKind.Matrix;
            else
            {
                kind = SmilTransformKind.Rejected;
                return false;
            }
            return true;
        }

        private static int SmilTransformStride(SmilTransformKind kind) => kind switch
        {
            SmilTransformKind.Translate => 2,
            SmilTransformKind.Scale => 2,
            SmilTransformKind.Rotate => 3,
            SmilTransformKind.SkewX => 1,
            SmilTransformKind.SkewY => 1,
            SmilTransformKind.Matrix => 6,
            _ => 0
        };

        private static bool TryBuildSmilTransformValues(
            SvgElement animation,
            SmilTransformKind kind,
            bool byAnimation,
            out float[] tuples,
            out int count)
        {
            tuples = null;
            count = 0;
            string valuesRaw = animation.GetAttribute("values");
            if (valuesRaw != null)
            {
                string[] values = SplitSmilValueList(valuesRaw);
                if (values == null) return false;
                return TryPackSmilTransformTuples(kind, values, out tuples, out count);
            }

            string from = animation.GetAttribute("from");
            string to = animation.GetAttribute("to");
            string by = animation.GetAttribute("by");
            if (by != null)
            {
                if (!byAnimation || from != null || to != null) return false;
                if (!TryCreateSmilTransformIdentity(kind, out float[] identity) ||
                    !TryReadSmilTransformTuple(kind, by, out float[] delta))
                    return false;
                int stride = SmilTransformStride(kind);
                tuples = new float[stride * 2];
                identity.CopyTo(tuples, 0);
                delta.CopyTo(tuples, stride);
                count = 2;
                return true;
            }
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return false;
            return TryPackSmilTransformTuples(kind, new[] { from, to }, out tuples, out count);
        }

        private static bool TryPackSmilTransformTuples(
            SmilTransformKind kind,
            string[] values,
            out float[] tuples,
            out int count)
        {
            tuples = null;
            count = values.Length;
            int stride = SmilTransformStride(kind);
            if (count == 0 || count > MaxSmilValueListEntries || stride == 0) return false;
            tuples = new float[count * stride];
            for (int i = 0; i < count; i++)
            {
                if (!TryReadSmilTransformTuple(kind, values[i], out float[] tuple))
                    return false;
                for (int c = 0; c < stride; c++) tuples[i * stride + c] = tuple[c];
            }
            return true;
        }

        private static bool TryCreateSmilTransformIdentity(
            SmilTransformKind kind,
            out float[] tuple)
        {
            tuple = null;
            int stride = SmilTransformStride(kind);
            if (stride == 0) return false;
            tuple = new float[stride];
            switch (kind)
            {
                case SmilTransformKind.Scale:
                    tuple[0] = 1f;
                    tuple[1] = 1f;
                    break;
                case SmilTransformKind.Matrix:
                    tuple[0] = 1f;
                    tuple[3] = 1f;
                    break;
            }
            return true;
        }

        private static bool TryReadSmilTransformTuple(
            SmilTransformKind kind,
            string raw,
            out float[] tuple)
        {
            tuple = null;
            int stride = SmilTransformStride(kind);
            if (stride == 0 || string.IsNullOrWhiteSpace(raw)) return false;
            if (raw.Length > MaxSmilRawTimingChars) return false;
            Span<float> numbers = stackalloc float[MaxSmilTransformNumbers + 1];
            int count = 0;
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan());
            while (tokenizer.Next(out var token))
            {
                if (count > MaxSmilTransformNumbers) return false;
                if (!SvgValues.TryParseNumber(token, out float number) ||
                    !float.IsFinite(number))
                    return false;
                numbers[count++] = number;
            }
            switch (kind)
            {
                case SmilTransformKind.Translate:
                case SmilTransformKind.Scale:
                    if (count == 0 || count > 2) return false;
                    break;
                case SmilTransformKind.Rotate:
                    if (count != 1 && count != 3) return false;
                    break;
                case SmilTransformKind.SkewX:
                case SmilTransformKind.SkewY:
                    if (count != 1) return false;
                    break;
                case SmilTransformKind.Matrix:
                    if (count != MaxSmilTransformNumbers) return false;
                    break;
                default:
                    return false;
            }

            tuple = new float[stride];
            switch (kind)
            {
                case SmilTransformKind.Translate:
                    tuple[0] = numbers[0];
                    tuple[1] = count == 2 ? numbers[1] : 0f;
                    break;
                case SmilTransformKind.Scale:
                    tuple[0] = numbers[0];
                    tuple[1] = count == 2 ? numbers[1] : numbers[0];
                    break;
                case SmilTransformKind.Rotate:
                    tuple[0] = numbers[0];
                    tuple[1] = count == 3 ? numbers[1] : 0f;
                    tuple[2] = count == 3 ? numbers[2] : 0f;
                    break;
                case SmilTransformKind.SkewX:
                case SmilTransformKind.SkewY:
                    tuple[0] = numbers[0];
                    break;
                case SmilTransformKind.Matrix:
                    for (int i = 0; i < MaxSmilTransformNumbers; i++) tuple[i] = numbers[i];
                    break;
            }
            for (int i = 0; i < stride; i++)
                if (!float.IsFinite(tuple[i])) return false;
            return true;
        }

        private static bool TryCreateSmilTransformMatrix(
            SmilTransformKind kind,
            float[] tuple,
            out SKMatrix matrix)
        {
            matrix = SKMatrix.Identity;
            switch (kind)
            {
                case SmilTransformKind.Translate:
                    matrix = SKMatrix.CreateTranslation(
                        SvgValues.ClampCoord(tuple[0]),
                        SvgValues.ClampCoord(tuple[1]));
                    break;
                case SmilTransformKind.Scale:
                    if (!float.IsFinite(tuple[0]) || !float.IsFinite(tuple[1])) return false;
                    matrix = SKMatrix.CreateScale(tuple[0], tuple[1]);
                    break;
                case SmilTransformKind.Rotate:
                    {
                        float radians = SvgValues.DegreesToRadians(tuple[0]);
                        if (!float.IsFinite(radians)) return false;
                        matrix = tuple[1] == 0f && tuple[2] == 0f
                            ? SKMatrix.CreateRotation(radians)
                            : SKMatrix.CreateRotation(
                                radians,
                                SvgValues.ClampCoord(tuple[1]),
                                SvgValues.ClampCoord(tuple[2]));
                        break;
                    }
                case SmilTransformKind.SkewX:
                case SmilTransformKind.SkewY:
                    {
                        float tangent = MathF.Tan(SvgValues.DegreesToRadians(tuple[0]));
                        if (!float.IsFinite(tangent)) return false;
                        matrix = kind == SmilTransformKind.SkewX
                            ? new SKMatrix(1f, tangent, 0f, 0f, 1f, 0f, 0f, 0f, 1f)
                            : new SKMatrix(1f, 0f, 0f, tangent, 1f, 0f, 0f, 0f, 1f);
                        break;
                    }
                case SmilTransformKind.Matrix:
                    matrix = new SKMatrix(
                        tuple[0], tuple[2], SvgValues.ClampCoord(tuple[4]),
                        tuple[1], tuple[3], SvgValues.ClampCoord(tuple[5]),
                        0f, 0f, 1f);
                    break;
                default:
                    return false;
            }
            if (!SvgValues.IsFinite(matrix)) return false;
            return SvgValues.TryNormalizeMatrix(matrix, out matrix);
        }

        private static bool TrySelectSmilTransformSegment(
            int count,
            float[] keyTimes,
            float progress,
            out int segment,
            out float local)
        {
            segment = count - 2;
            local = 0f;
            float start = 0f;
            float finish = 0f;
            for (int i = 0; i < count - 1; i++)
            {
                float segmentStart = keyTimes?[i] ?? (float)i / (count - 1);
                float segmentFinish = keyTimes?[i + 1] ?? (float)(i + 1) / (count - 1);
                if (progress > segmentFinish && i < count - 2) continue;
                segment = i;
                start = segmentStart;
                finish = segmentFinish;
                break;
            }
            if (!(finish > start)) return false;
            local = Math.Clamp((progress - start) / (finish - start), 0f, 1f);
            return true;
        }

        private bool TrySampleSmilTransformPaced(
            SmilTransformKind kind,
            float[] tuples,
            int count,
            float progress,
            out float[] tuple)
        {
            int stride = SmilTransformStride(kind);
            tuple = null;
            if (stride == 0) return false;
            tuple = new float[stride];
            for (int c = 0; c < stride; c++)
            {
                CheckDeadline();
                float total = 0f;
                for (int i = 1; i < count; i++)
                {
                    float delta = Math.Abs(
                        tuples[i * stride + c] - tuples[(i - 1) * stride + c]);
                    if (!float.IsFinite(delta)) return false;
                    total += delta;
                }
                if (!(total > 0f))
                {
                    tuple[c] = tuples[c];
                    continue;
                }
                float run = progress * total;
                float travelled = 0f;
                for (int i = 1; i < count; i++)
                {
                    float delta = Math.Abs(
                        tuples[i * stride + c] - tuples[(i - 1) * stride + c]);
                    if (run <= travelled + delta || i == count - 1)
                    {
                        float local = delta > 0f
                            ? Math.Clamp((run - travelled) / delta, 0f, 1f)
                            : 0f;
                        float head = tuples[(i - 1) * stride + c];
                        tuple[c] = head + (tuples[i * stride + c] - head) * local;
                        break;
                    }
                    travelled += delta;
                }
                if (!float.IsFinite(tuple[c])) return false;
            }
            return true;
        }

        private bool TrySampleSmilTransform(
            SmilTransformKind kind,
            float[] tuples,
            int count,
            float[] keyTimes,
            float[] splines,
            bool discrete,
            bool paced,
            float progress,
            out SKMatrix matrix)
        {
            matrix = SKMatrix.Identity;
            if (count <= 0) return false;
            int stride = SmilTransformStride(kind);
            if (stride == 0 || count > MaxSmilValueListEntries) return false;

            int selected;
            if (count == 1) selected = 0;
            else if (discrete)
            {
                if (keyTimes == null)
                {
                    int step = (int)MathF.Floor(progress * count);
                    selected = step < 0 ? 0 : Math.Min(step, count - 1);
                }
                else
                {
                    selected = 0;
                    for (int i = 1; i < keyTimes.Length && progress >= keyTimes[i]; i++)
                        selected = i;
                }
            }
            else if (paced)
            {
                if (!TrySampleSmilTransformPaced(kind, tuples, count, progress,
                        out float[] pacedTuple) ||
                    !TryCreateSmilTransformMatrix(kind, pacedTuple, out matrix))
                    return false;
                return true;
            }
            else
            {
                if (!TrySelectSmilTransformSegment(count, keyTimes, progress,
                        out int segment, out float local))
                    return false;
                if (splines != null)
                {
                    local = EaseSplineSegment(local,
                        splines[segment * 4], splines[segment * 4 + 1],
                        splines[segment * 4 + 2], splines[segment * 4 + 3]);
                }
                selected = segment;
                var blended = new float[stride];
                for (int c = 0; c < stride; c++)
                {
                    float head = tuples[selected * stride + c];
                    float tail = tuples[(selected + 1) * stride + c];
                    float mixed = head + (tail - head) * local;
                    if (!float.IsFinite(mixed)) return false;
                    blended[c] = mixed;
                }
                return TryCreateSmilTransformMatrix(kind, blended, out matrix);
            }

            var single = new float[stride];
            for (int c = 0; c < stride; c++) single[c] = tuples[selected * stride + c];
            return TryCreateSmilTransformMatrix(kind, single, out matrix);
        }

        private void ComposeSmilTransform(
            SvgElement target,
            SKMatrix sampled,
            bool additiveSum)
        {
            _animatedTransforms ??= new Dictionary<SvgElement, SKMatrix>();
            _animatedTransformReplacesBase ??= new HashSet<SvgElement>();
            if (additiveSum && _animatedTransforms.TryGetValue(target, out SKMatrix current))
            {
                if (!current.IsIdentity)
                {
                    if (!SvgValues.TryNormalizeMatrix(
                            SKMatrix.Concat(current, sampled), out SKMatrix combined))
                    {
                        RequireSmilFallback("animateTransform additive sum");
                        return;
                    }
                    sampled = combined;
                }
                _animatedTransforms[target] = sampled;
                return;
            }
            _animatedTransforms[target] = sampled;
            if (additiveSum) _animatedTransformReplacesBase.Remove(target);
            else _animatedTransformReplacesBase.Add(target);
        }

        private void RequireSmilFallback(string feature) =>
            _report.RequireFallback($"SVG {feature} requires compatibility fallback");
    }
}
