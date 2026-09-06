using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        private static readonly HashSet<string> SettableSmilAttributes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "x", "y", "width", "height", "cx", "cy", "r", "rx", "ry",
                "x1", "y1", "x2", "y2", "d", "points", "transform",
                "fill", "stroke", "stroke-width", "fill-opacity", "stroke-opacity",
                "opacity", "visibility", "display", "color"
            };

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
                for (int i = element.Children.Count - 1; i >= 0; i--)
                    pending.Push(element.Children[i]);
            }
        }

        private void ApplyAnimateSnapshot(SvgElement animation)
        {
            SvgElement target = animation.Parent;
            string attributeName = animation.GetAttribute("attributeName")?.Trim();
            string from = animation.GetAttribute("from");
            string to = animation.GetAttribute("to");
            string attributeType = animation.GetAttribute("attributeType")?.Trim();
            if (target == null || string.IsNullOrEmpty(attributeName) ||
                !SettableSmilAttributes.Contains(attributeName) || from == null || to == null ||
                animation.GetAttribute("values") != null || animation.GetAttribute("by") != null ||
                animation.GetAttribute("keyTimes") != null ||
                animation.GetAttribute("keySplines") != null ||
                !IsDefaultMotionAttribute(animation, "additive", "replace") ||
                !IsDefaultMotionAttribute(animation, "accumulate", "none") ||
                (!string.IsNullOrEmpty(attributeType) &&
                 !attributeType.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                 !attributeType.Equals("CSS", StringComparison.OrdinalIgnoreCase) &&
                 !attributeType.Equals("XML", StringComparison.OrdinalIgnoreCase)))
            {
                RequireSmilFallback("animate values or target");
                return;
            }

            string durationRaw = animation.GetAttribute("dur")?.Trim();
            if (string.IsNullOrEmpty(durationRaw) ||
                !TryParseClockSeconds(durationRaw, out double duration) || duration <= 0d ||
                animation.GetAttribute("repeatCount") != null)
            {
                RequireSmilFallback("animate duration or repetition");
                return;
            }
            if (!TryResolveSetInterval(animation, out double begin, out double end,
                    out bool indefiniteEnd, out bool freeze))
                return;

            bool active = _documentTimeSeconds >= begin &&
                (indefiniteEnd || _documentTimeSeconds < end);
            bool frozen = freeze && !indefiniteEnd && _documentTimeSeconds >= end;
            if (!active && !frozen) return;

            float progress = frozen
                ? 1f
                : (float)Math.Clamp((_documentTimeSeconds - begin) / duration, 0d, 1d);
            string calcMode = animation.GetAttribute("calcMode")?.Trim();
            if (string.IsNullOrEmpty(calcMode) ||
                calcMode.Equals("linear", StringComparison.OrdinalIgnoreCase) ||
                calcMode.Equals("paced", StringComparison.OrdinalIgnoreCase))
            {
                // Linear interpolation below.
            }
            else if (calcMode.Equals("discrete", StringComparison.OrdinalIgnoreCase))
            {
                progress = progress < 1f ? 0f : 1f;
            }
            else
            {
                RequireSmilFallback("animate calculation mode");
                return;
            }

            if (!TryInterpolateSmilValue(attributeName, from, to, progress, out string value))
            {
                RequireSmilFallback("animate value interpolation");
                return;
            }
            target.AnimatedProperties ??=
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            target.AnimatedProperties[attributeName] = value;
        }

        private static bool TryInterpolateSmilValue(
            string attributeName,
            string from,
            string to,
            float progress,
            out string value)
        {
            value = null;
            if (attributeName.Equals("fill", StringComparison.OrdinalIgnoreCase) ||
                attributeName.Equals("stroke", StringComparison.OrdinalIgnoreCase) ||
                attributeName.Equals("color", StringComparison.OrdinalIgnoreCase))
            {
                if (!SvgValues.TryParseColor(from.AsSpan(), out var start) ||
                    !SvgValues.TryParseColor(to.AsSpan(), out var finish))
                    return false;
                byte Lerp(byte a, byte b) => (byte)Math.Clamp(
                    (int)MathF.Round(a + (b - a) * progress), 0, 255);
                value = $"#{Lerp(start.Red, finish.Red):x2}{Lerp(start.Green, finish.Green):x2}" +
                    $"{Lerp(start.Blue, finish.Blue):x2}{Lerp(start.Alpha, finish.Alpha):x2}";
                return true;
            }

            if (!SvgValues.TryParseLength(from.AsSpan(), out float startValue, out var startUnit) ||
                !SvgValues.TryParseLength(to.AsSpan(), out float endValue, out var endUnit) ||
                startUnit != endUnit)
                return false;
            float interpolated = startValue + (endValue - startValue) * progress;
            if (!float.IsFinite(interpolated)) return false;
            value = interpolated.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                SmilUnitSuffix(startUnit);
            return true;
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

        private void ApplySetSnapshot(SvgElement animation)
        {
            SvgElement target = animation.Parent;
            string attributeName = animation.GetAttribute("attributeName")?.Trim();
            string value = animation.GetAttribute("to");
            string attributeType = animation.GetAttribute("attributeType")?.Trim();
            if (target == null || string.IsNullOrEmpty(attributeName) ||
                !SettableSmilAttributes.Contains(attributeName) || value == null ||
                (!string.IsNullOrEmpty(attributeType) &&
                 !attributeType.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                 !attributeType.Equals("CSS", StringComparison.OrdinalIgnoreCase) &&
                 !attributeType.Equals("XML", StringComparison.OrdinalIgnoreCase)))
            {
                RequireSmilFallback("set target or attribute");
                return;
            }

            if (!TryResolveSetInterval(animation, out double begin, out double end,
                    out bool indefiniteEnd, out bool freeze))
                return;

            bool active = _documentTimeSeconds >= begin &&
                (indefiniteEnd || _documentTimeSeconds < end);
            bool frozen = freeze && !indefiniteEnd && _documentTimeSeconds >= end;
            if (!active && !frozen) return;

            target.AnimatedProperties ??=
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            target.AnimatedProperties[attributeName] = value;
        }

        private bool TryResolveSetInterval(
            SvgElement animation,
            out double begin,
            out double end,
            out bool indefiniteEnd,
            out bool freeze)
        {
            begin = 0d;
            end = 0d;
            indefiniteEnd = false;
            freeze = false;

            string beginRaw = animation.GetAttribute("begin")?.Trim();
            if (!string.IsNullOrEmpty(beginRaw))
            {
                if (beginRaw.Equals("indefinite", StringComparison.OrdinalIgnoreCase))
                {
                    RequireSmilFallback("event-based set begin");
                    return false;
                }
                if (beginRaw.IndexOf(';') >= 0 ||
                    !TryParseClockSeconds(beginRaw, out begin))
                {
                    RequireSmilFallback("set begin timing");
                    return false;
                }
            }

            string fill = animation.GetAttribute("fill")?.Trim();
            if (string.IsNullOrEmpty(fill) || fill.Equals("remove", StringComparison.OrdinalIgnoreCase))
                freeze = false;
            else if (fill.Equals("freeze", StringComparison.OrdinalIgnoreCase))
                freeze = true;
            else
            {
                RequireSmilFallback("set fill mode");
                return false;
            }

            double duration = double.PositiveInfinity;
            string durationRaw = animation.GetAttribute("dur")?.Trim();
            if (!string.IsNullOrEmpty(durationRaw) &&
                !durationRaw.Equals("indefinite", StringComparison.OrdinalIgnoreCase) &&
                (!TryParseClockSeconds(durationRaw, out duration) || duration <= 0d))
            {
                RequireSmilFallback("set duration");
                return false;
            }

            double repeatCount = 1d;
            string repeatRaw = animation.GetAttribute("repeatCount")?.Trim();
            if (!string.IsNullOrEmpty(repeatRaw))
            {
                if (repeatRaw.Equals("indefinite", StringComparison.OrdinalIgnoreCase))
                    repeatCount = double.PositiveInfinity;
                else if (!double.TryParse(
                             repeatRaw, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture,
                             out repeatCount) || !double.IsFinite(repeatCount) || repeatCount <= 0d)
                {
                    RequireSmilFallback("set repeat count");
                    return false;
                }
            }

            double activeDuration = duration * repeatCount;
            indefiniteEnd = double.IsPositiveInfinity(activeDuration);
            end = indefiniteEnd ? double.PositiveInfinity : begin + activeDuration;

            string endRaw = animation.GetAttribute("end")?.Trim();
            if (!string.IsNullOrEmpty(endRaw))
            {
                if (endRaw.IndexOf(';') >= 0 ||
                    !TryParseClockSeconds(endRaw, out double explicitEnd))
                {
                    RequireSmilFallback("set end timing");
                    return false;
                }
                if (explicitEnd < end)
                {
                    end = explicitEnd;
                    indefiniteEnd = false;
                }
            }

            if (!IsDefaultMotionAttribute(animation, "restart", "always") ||
                animation.GetAttribute("min") != null ||
                animation.GetAttribute("max") != null ||
                animation.GetAttribute("repeatDur") != null)
            {
                RequireSmilFallback("advanced set timing");
                return false;
            }
            return true;
        }

        private void RequireSmilFallback(string feature) =>
            _report.RequireFallback($"SVG {feature} requires compatibility fallback");
    }
}
