using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>Outcome of a bounded CSS transform resolution.</summary>
    internal enum SvgCssTransformStatus
    {
        /// <summary>No transform value was supplied.</summary>
        None,
        /// <summary>The value resolved to the identity matrix (including "none").</summary>
        Identity,
        /// <summary>A concrete matrix (and origin pivot) was resolved.</summary>
        Matrix,
        /// <summary>Syntax or features outside the bounded subset; caller must
        /// route to compatibility fallback rather than guessing pixels.</summary>
        Unsupported
    }

    /// <summary>
    /// Bounded resolver for the CSS <c>transform</c>, <c>transform-origin</c> and
    /// <c>transform-box</c> properties on SVG elements. Pure and allocation-light:
    /// no document, media, or process-global CSS state is consulted, and anything
    /// outside the supported 2-D subset returns <see cref="SvgCssTransformStatus.Unsupported"/>
    /// so the render walk can require compatibility fallback instead of silently
    /// producing wrong geometry.
    ///
    /// Supported subset:
    /// - transform functions: matrix, translate, translateX/Y, scale, scaleX/Y,
    ///   rotate, skew, skewX/Y.
    /// - lengths: px/user units, absolute units and percentages through
    ///   <see cref="SvgCssLengthEvaluator"/> (so calc()/min()/max()/clamp() come
    ///   with the same nesting/operation ceilings as geometry).
    /// - angles: deg/grad/rad/turn plus unitless zero.
    /// - transform-origin: keywords and length-percentage components; a nonzero
    ///   z component is unsupported (no 3-D rendering).
    /// - transform-box: view-box (default) and fill-box. stroke-box/content-box/
    ///   border-box need stroke or CSS layout boxes that the isolated renderer
    ///   does not model.
    /// </summary>
    internal static class SvgCssTransform
    {
        private const int MaxValueChars = 4096;
        private const int MaxFunctions = 32;
        private const int MaxArgsPerFunction = 8;
        private const float DefaultFontSize = 16f;

        /// <summary>
        /// Resolves a CSS transform declaration chain against explicit context.
        /// Percentages resolve against the reference box implied by
        /// <paramref name="boxValue"/>: viewport dimensions for view-box, the
        /// shape's object bounding box for fill-box.
        /// </summary>
        public static SvgCssTransformStatus TryResolve(
            string transformValue,
            string originValue,
            string boxValue,
            float referenceWidth,
            float referenceHeight,
            float fontSize,
            float rootFontSize,
            SKRect? fillBox,
            out SKMatrix matrix,
            out SKPoint origin)
        {
            matrix = SKMatrix.Identity;
            origin = default;

            if (!TryResolveBox(boxValue, fillBox, out bool isFillBox))
            {
                return SvgCssTransformStatus.Unsupported;
            }
            if (isFillBox)
            {
                // Reference box origin: percentages and lengths offset from the
                // bounding box top-left instead of the user-space origin.
                referenceWidth = fillBox.Value.Width;
                referenceHeight = fillBox.Value.Height;
            }

            var originStatus = TryResolveOrigin(
                originValue,
                isFillBox ? fillBox.Value.Left : 0f,
                isFillBox ? fillBox.Value.Top : 0f,
                referenceWidth,
                referenceHeight,
                fontSize,
                rootFontSize,
                allowUserUnits: false,
                out origin);
            if (originStatus == SvgCssTransformStatus.Unsupported)
            {
                return SvgCssTransformStatus.Unsupported;
            }

            if (string.IsNullOrWhiteSpace(transformValue))
            {
                return SvgCssTransformStatus.None;
            }
            string trimmed = transformValue.Trim();
            if (trimmed.Length > MaxValueChars)
            {
                return SvgCssTransformStatus.Unsupported;
            }
            if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return SvgCssTransformStatus.Identity;
            }

            matrix = SKMatrix.Identity;
            int functions = 0;
            int position = 0;
            while (position < trimmed.Length)
            {
                while (position < trimmed.Length && char.IsWhiteSpace(trimmed[position])) position++;
                if (position >= trimmed.Length) break;

                int nameStart = position;
                while (position < trimmed.Length &&
                       (char.IsAsciiLetter(trimmed[position]) || trimmed[position] == '-'))
                {
                    position++;
                }
                if (position == nameStart || position >= trimmed.Length || trimmed[position] != '(')
                {
                    return SvgCssTransformStatus.Unsupported;
                }
                string name = trimmed.Substring(nameStart, position - nameStart);
                int argStart = ++position;
                int depth = 1;
                while (position < trimmed.Length && depth > 0)
                {
                    char c = trimmed[position];
                    if (c == '(') depth++;
                    else if (c == ')') depth--;
                    position++;
                }
                if (depth != 0)
                {
                    return SvgCssTransformStatus.Unsupported;
                }
                string args = trimmed.Substring(argStart, position - argStart - 1);

                if (++functions > MaxFunctions)
                {
                    return SvgCssTransformStatus.Unsupported;
                }
                if (!ApplyFunction(name, args, referenceWidth, referenceHeight,
                        fontSize, rootFontSize, ref matrix))
                {
                    return SvgCssTransformStatus.Unsupported;
                }
            }

            if (!matrix.IsIdentity && (origin.X != 0f || origin.Y != 0f))
            {
                matrix = SKMatrix.Concat(
                    SKMatrix.Concat(SKMatrix.CreateTranslation(origin.X, origin.Y), matrix),
                    SKMatrix.CreateTranslation(-origin.X, -origin.Y));
            }
            return matrix.IsIdentity
                ? SvgCssTransformStatus.Identity
                : SvgCssTransformStatus.Matrix;
        }

        private static bool TryResolveBox(string boxValue, SKRect? fillBox, out bool isFillBox)
        {
            isFillBox = false;
            if (string.IsNullOrWhiteSpace(boxValue)) return true;
            switch (boxValue.Trim().ToLowerInvariant())
            {
                case "view-box":
                    return true;
                case "fill-box":
                    if (!fillBox.HasValue ||
                        !IsFinite(fillBox.Value.Width) || !IsFinite(fillBox.Value.Height))
                        return false;
                    isFillBox = true;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Resolves the SVG <c>transform-origin</c> presentation attribute that
        /// accompanies a <c>transform</c> attribute list. Attribute syntax treats
        /// bare numbers as user units (unlike the CSS property, which requires
        /// explicit units); percentages resolve against the nearest viewport.
        /// </summary>
        public static bool TryResolveAttributeOrigin(
            string originValue,
            float referenceWidth,
            float referenceHeight,
            out SKPoint origin)
        {
            return TryResolveOrigin(
                originValue,
                0f,
                0f,
                referenceWidth,
                referenceHeight,
                DefaultFontSize,
                DefaultFontSize,
                allowUserUnits: true,
                out origin) != SvgCssTransformStatus.Unsupported;
        }

        private static SvgCssTransformStatus TryResolveOrigin(
            string originValue,
            float boxLeft,
            float boxTop,
            float referenceWidth,
            float referenceHeight,
            float fontSize,
            float rootFontSize,
            bool allowUserUnits,
            out SKPoint origin)
        {
            origin = new SKPoint(boxLeft, boxTop);
            if (string.IsNullOrWhiteSpace(originValue))
            {
                // Initial used value for SVG elements without an associated CSS
                // layout box: 0 0 relative to the reference box origin.
                return SvgCssTransformStatus.Identity;
            }

            string trimmed = originValue.Trim();
            if (trimmed.Length > MaxValueChars ||
                !TrySplitArgs(trimmed, out string[] parts) ||
                parts.Length == 0 || parts.Length > 3)
            {
                return SvgCssTransformStatus.Unsupported;
            }

            // Role assignment per css-transforms: horizontal keywords and bare
            // lengths bind to x, vertical keywords to y; "top left" normalizes
            // to "left top". A missing second component defaults y to center.
            float x, y;
            int consumed;
            if (IsVerticalKeyword(parts[0]))
            {
                if (!TryOriginAxis(parts[0], boxTop, referenceHeight, fontSize,
                        rootFontSize, allowUserUnits, out y))
                    return SvgCssTransformStatus.Unsupported;
                if (parts.Length > 1)
                {
                    if (IsVerticalKeyword(parts[1]) ||
                        !TryOriginAxis(parts[1], boxLeft, referenceWidth, fontSize,
                            rootFontSize, allowUserUnits, out x))
                        return SvgCssTransformStatus.Unsupported;
                    consumed = 2;
                }
                else
                {
                    if (!TryLength("50%", referenceWidth, fontSize, rootFontSize, out float xc))
                        return SvgCssTransformStatus.Unsupported;
                    x = boxLeft + xc;
                    consumed = 1;
                }
            }
            else
            {
                if (!TryOriginAxis(parts[0], boxLeft, referenceWidth, fontSize,
                        rootFontSize, allowUserUnits, out x))
                    return SvgCssTransformStatus.Unsupported;
                if (parts.Length > 1 && !IsHorizontalKeyword(parts[1]))
                {
                    if (!TryOriginAxis(parts[1], boxTop, referenceHeight, fontSize,
                            rootFontSize, allowUserUnits, out y))
                        return SvgCssTransformStatus.Unsupported;
                    consumed = 2;
                }
                else
                {
                    if (!TryLength("50%", referenceHeight, fontSize, rootFontSize, out float yc))
                        return SvgCssTransformStatus.Unsupported;
                    y = boxTop + yc;
                    consumed = 1;
                }
            }

            if (parts.Length > consumed)
            {
                // Optional z component must resolve to zero; any real depth is
                // unsupported because there is no 3-D rendering here.
                if (parts.Length != consumed + 1 ||
                    !TryLength(parts[consumed], 1f, fontSize, rootFontSize, out float z) ||
                    z != 0f)
                {
                    return SvgCssTransformStatus.Unsupported;
                }
            }

            origin = new SKPoint(SvgValues.ClampCoord(x), SvgValues.ClampCoord(y));
            return SvgCssTransformStatus.Matrix;
        }

        private static bool IsHorizontalKeyword(string part) =>
            part.Equals("left", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("center", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("right", StringComparison.OrdinalIgnoreCase);

        private static bool IsVerticalKeyword(string part) =>
            part.Equals("top", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("center", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("bottom", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Resolves one origin component on its own axis. Keywords map to
        /// 0/50/100% of that axis's reference size; anything else goes through
        /// the shared bounded length evaluator. Attribute mode additionally
        /// accepts bare numbers as user units via the SVG length parser.
        /// </summary>
        private static bool TryOriginAxis(
            string part,
            float boxOffset,
            float referenceSize,
            float fontSize,
            float rootFontSize,
            bool allowUserUnits,
            out float value)
        {
            value = 0f;
            switch (part.ToLowerInvariant())
            {
                case "left":
                case "top":
                    value = boxOffset;
                    return true;
                case "center":
                    value = boxOffset + 0.5f * referenceSize;
                    return true;
                case "right":
                case "bottom":
                    value = boxOffset + referenceSize;
                    return true;
            }
            if (allowUserUnits &&
                SvgValues.TryParseLength(part.AsSpan(), out float userValue, out var unit))
            {
                value = boxOffset +
                        SvgValues.ClampCoord(SvgValues.ResolveUnits(userValue, unit, fontSize, referenceSize));
                return true;
            }
            if (!TryLength(part, referenceSize, fontSize, rootFontSize, out float resolved))
            {
                return false;
            }
            value = boxOffset + resolved;
            return true;
        }

        private static bool ApplyFunction(
            string name,
            string args,
            float referenceWidth,
            float referenceHeight,
            float fontSize,
            float rootFontSize,
            ref SKMatrix matrix)
        {
            switch (name.ToLowerInvariant())
            {
                case "matrix":
                    {
                        if (!TrySplitArgs(args, out var parts) || parts.Length != 6) return false;
                        if (!TryNumber(parts[0], out float a) || !TryNumber(parts[1], out float b) ||
                            !TryNumber(parts[2], out float c) || !TryNumber(parts[3], out float d))
                            return false;
                        // e/f are <length-percentage>; plain numbers stay valid.
                        if (!TryLength(parts[4], referenceWidth, fontSize, rootFontSize, out float e) ||
                            !TryLength(parts[5], referenceHeight, fontSize, rootFontSize, out float f))
                            return false;
                        matrix = SKMatrix.Concat(matrix, new SKMatrix(
                            a, c, e,
                            b, d, f,
                            0f, 0f, 1f));
                        return true;
                    }
                case "translate":
                    {
                        if (!TrySplitArgs(args, out var parts) ||
                            parts.Length is (< 1 or > 2)) return false;
                        if (!TryLength(parts[0], referenceWidth, fontSize, rootFontSize, out float tx))
                            return false;
                        float ty = 0f;
                        if (parts.Length == 2 &&
                            !TryLength(parts[1], referenceHeight, fontSize, rootFontSize, out ty))
                            return false;
                        matrix = SKMatrix.Concat(matrix, SKMatrix.CreateTranslation(tx, ty));
                        return true;
                    }
                case "translatex":
                    {
                        if (!TrySplitArgs(args, out var parts) || parts.Length != 1 ||
                            !TryLength(parts[0], referenceWidth, fontSize, rootFontSize, out float tx))
                            return false;
                        matrix = SKMatrix.Concat(matrix, SKMatrix.CreateTranslation(tx, 0f));
                        return true;
                    }
                case "translatey":
                    {
                        if (!TrySplitArgs(args, out var parts) || parts.Length != 1 ||
                            !TryLength(parts[0], referenceHeight, fontSize, rootFontSize, out float ty))
                            return false;
                        matrix = SKMatrix.Concat(matrix, SKMatrix.CreateTranslation(0f, ty));
                        return true;
                    }
                case "scale":
                    {
                        if (!TrySplitArgs(args, out var parts) ||
                            parts.Length is (< 1 or > 2)) return false;
                        if (!TryNumber(parts[0], out float sx)) return false;
                        float sy = sx;
                        if (parts.Length == 2 && !TryNumber(parts[1], out sy)) return false;
                        matrix = SKMatrix.Concat(matrix, SKMatrix.CreateScale(sx, sy));
                        return true;
                    }
                case "scalex":
                case "scaley":
                    {
                        if (!TrySplitArgs(args, out var parts) || parts.Length != 1 ||
                            !TryNumber(parts[0], out float s))
                            return false;
                        matrix = SKMatrix.Concat(matrix, name.Equals("scalex", StringComparison.OrdinalIgnoreCase)
                            ? SKMatrix.CreateScale(s, 1f)
                            : SKMatrix.CreateScale(1f, s));
                        return true;
                    }
                case "rotate":
                    {
                        if (!TrySplitArgs(args, out var parts) || parts.Length != 1 ||
                            !TryAngle(parts[0], out float radians))
                            return false;
                        matrix = SKMatrix.Concat(matrix, RotationMatrix(radians));
                        return true;
                    }
                case "skew":
                    {
                        if (!TrySplitArgs(args, out var parts) ||
                            parts.Length is (< 1 or > 2)) return false;
                        if (!TryAngle(parts[0], out float ax)) return false;
                        float ay = 0f;
                        if (parts.Length == 2 && !TryAngle(parts[1], out ay)) return false;
                        matrix = SKMatrix.Concat(matrix, SkewMatrix(ax, ay));
                        return true;
                    }
                case "skewx":
                case "skewy":
                    {
                        if (!TrySplitArgs(args, out var parts) || parts.Length != 1 ||
                            !TryAngle(parts[0], out float angle))
                            return false;
                        matrix = SKMatrix.Concat(matrix,
                            name.Equals("skewx", StringComparison.OrdinalIgnoreCase)
                                ? SkewMatrix(angle, 0f)
                                : SkewMatrix(0f, angle));
                        return true;
                    }
                default:
                    // translateZ/translate3d/scale3d/rotate3d/matrix3d/perspective
                    // and any unknown function are outside the 2-D subset.
                    return false;
            }
        }

        private static SKMatrix RotationMatrix(float radians)
        {
            float cos = MathF.Cos(radians);
            float sin = MathF.Sin(radians);
            return new SKMatrix(cos, -sin, 0f, sin, cos, 0f, 0f, 0f, 1f);
        }

        private static SKMatrix SkewMatrix(float axRadians, float ayRadians)
        {
            float tanX = MathF.Tan(axRadians);
            float tanY = MathF.Tan(ayRadians);
            return new SKMatrix(1f, tanX, 0f, tanY, 1f, 0f, 0f, 0f, 1f);
        }

        /// <summary>Parses one length-percentage argument through the shared
        /// bounded evaluator (px, absolute units, percentages, calc()).</summary>
        private static bool TryLength(
            string raw,
            float percentReference,
            float fontSize,
            float rootFontSize,
            out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            return SvgCssLengthEvaluator.TryEvaluate(
                raw, percentReference, fontSize, rootFontSize, out value);
        }

        private static bool TryNumber(string raw, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string trimmed = raw.Trim();
            // A dimension token ("2px") or percentage is not a valid <number>.
            foreach (char c in trimmed)
            {
                if (!(char.IsAsciiDigit(c) || c == '+' || c == '-' || c == '.' ||
                      c == 'e' || c == 'E'))
                    return false;
            }
            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double parsed) || !double.IsFinite(parsed))
                return false;
            value = SvgValues.ClampCoord((float)parsed);
            return true;
        }

        private static bool TryAngle(string raw, out float radians)
        {
            radians = 0f;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string trimmed = raw.Trim();

            // Plain number: only unitless zero is a valid angle.
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double unitless))
            {
                if (unitless == 0d)
                {
                    radians = 0f;
                    return true;
                }
                return false;
            }

            int unitStart = trimmed.Length;
            while (unitStart > 0 && char.IsAsciiLetter(trimmed[unitStart - 1])) unitStart--;
            if (unitStart == 0 || unitStart == trimmed.Length) return false;
            string numberPart = trimmed.Substring(0, unitStart);
            string unit = trimmed.Substring(unitStart);
            if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out double amount) || !double.IsFinite(amount))
                return false;

            double converted = unit.ToLowerInvariant() switch
            {
                "deg" => amount * Math.PI / 180d,
                "grad" => amount * Math.PI / 200d,
                "rad" => amount,
                "turn" => amount * 2d * Math.PI,
                _ => double.NaN
            };
            if (double.IsNaN(converted) || !double.IsFinite(converted)) return false;
            radians = (float)converted;
            return true;
        }

        /// <summary>
        /// Splits function arguments on top-level commas and whitespace runs,
        /// respecting parenthesized calc()/min()/max()/clamp() nesting.
        /// </summary>
        private static bool TrySplitArgs(string args, out string[] parts)
        {
            parts = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(args)) return true;

            int depth = 0;
            var current = new StringBuilder(args.Length);
            var collected = new List<string>();
            void Flush()
            {
                if (current.Length > 0)
                {
                    collected.Add(current.ToString());
                    current.Clear();
                }
            }

            foreach (char c in args)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;

                if (depth == 0 && (c == ',' || char.IsWhiteSpace(c)))
                {
                    Flush();
                    if (collected.Count > MaxArgsPerFunction) return false;
                    continue;
                }
                current.Append(c);
                if (current.Length > MaxValueChars) return false;
            }
            Flush();
            if (collected.Count > MaxArgsPerFunction) return false;
            parts = collected.ToArray();
            return true;
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}

