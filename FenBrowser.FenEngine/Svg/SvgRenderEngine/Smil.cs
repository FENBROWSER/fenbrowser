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
                for (int i = element.Children.Count - 1; i >= 0; i--)
                    pending.Push(element.Children[i]);
            }
        }

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
