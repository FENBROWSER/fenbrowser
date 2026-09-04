using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// Strict, allocation-conscious parsers for SVG attribute values.
    ///
    /// Security/robustness contract:
    /// - Every numeric result is finite by construction: overflow, NaN spellings
    ///   and exponent abuse fail the parse instead of reaching Skia (which throws
    ///   or misrenders on non-finite input).
    /// - Coordinates are clamped to <see cref="CoordClamp"/>; larger values are
    ///   visually meaningless and destabilize anti-aliasing math downstream.
    /// - All scanners are single-pass O(n); no regex anywhere in this module.
    /// </summary>
    internal static class SvgValues
    {
        public const float CoordClamp = 1e9f;

        public static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        public static float ClampCoord(float v)
        {
            if (!IsFinite(v)) return 0f;
            if (v > CoordClamp) return CoordClamp;
            if (v < -CoordClamp) return -CoordClamp;
            return v;
        }

        // -------------------------------------------------------------- numbers

        public static bool TryParseNumber(ReadOnlySpan<char> s, out float value)
        {
            value = 0f;
            int i = 0;
            int n = s.Length;

            while (i < n && char.IsWhiteSpace(s[i])) i++;
            bool negative = false;
            if (i < n && (s[i] == '+' || s[i] == '-'))
            {
                negative = s[i] == '-';
                i++;
            }

            long head = 0;
            bool anyDigit = false;
            while (i < n && s[i] >= '0' && s[i] <= '9')
            {
                anyDigit = true;
                if (head <= 0x001FFFFFFFFFFFFF)
                {
                    head = head * 10 + (s[i] - '0');
                }
                i++;
            }

            long frac = 0;
            long fracScale = 1;
            if (i < n && s[i] == '.')
            {
                i++;
                while (i < n && s[i] >= '0' && s[i] <= '9')
                {
                    anyDigit = true;
                    if (fracScale <= 100000000L)
                    {
                        frac = frac * 10 + (s[i] - '0');
                        fracScale *= 10;
                    }
                    i++;
                }
            }

            if (!anyDigit)
            {
                return false;
            }

            double expFactor = 1.0;
            if (i < n && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                bool expNegative = false;
                if (i < n && (s[i] == '+' || s[i] == '-'))
                {
                    expNegative = s[i] == '-';
                    i++;
                }
                if (i >= n || s[i] < '0' || s[i] > '9')
                {
                    return false;
                }
                int exp = 0;
                while (i < n && s[i] >= '0' && s[i] <= '9')
                {
                    exp = System.Math.Min(exp * 10 + (s[i] - '0'), 128);
                    i++;
                }
                expFactor = System.Math.Pow(10, expNegative ? -exp : exp);
            }

            while (i < n && char.IsWhiteSpace(s[i])) i++;
            if (i != n)
            {
                return false;
            }

            double d = head + frac / (double)fracScale;
            d *= expFactor;
            if (negative) d = -d;

            float f = (float)d;
            if (!IsFinite(f))
            {
                return false;
            }
            value = f;
            return true;
        }

        // -------------------------------------------------------------- lengths

        public enum SvgUnit { User, Px, Pt, Pc, Mm, Cm, In, Em, Ex, Percent }

        public static bool TryParseLength(ReadOnlySpan<char> s, out float value, out SvgUnit unit)
        {
            value = 0f;
            unit = SvgUnit.User;
            s = s.Trim();

            int cut = s.Length;
            while (cut > 0 && (char.IsLetter(s[cut - 1]) || s[cut - 1] == '%'))
            {
                cut--;
            }

            var suffix = s.Slice(cut);
            switch (suffix.Length)
            {
                case 0:
                    unit = SvgUnit.User;
                    break;
                case 1 when suffix[0] == '%':
                    unit = SvgUnit.Percent;
                    break;
                case 2:
                    unit = MapTwoCharUnit(suffix[0], suffix[1]);
                    if (unit == SvgUnit.User)
                    {
                        return false; // Unknown two-char suffix is a hard error.
                    }
                    break;
                default:
                    return false;
            }

            return TryParseNumber(s.Slice(0, cut), out value);
        }

        private static SvgUnit MapTwoCharUnit(char raw0, char raw1)
        {
            char c0 = LowerAscii(raw0);
            char c1 = LowerAscii(raw1);
            if (c0 == 'p' && c1 == 'x') return SvgUnit.Px;
            if (c0 == 'p' && c1 == 't') return SvgUnit.Pt;
            if (c0 == 'p' && c1 == 'c') return SvgUnit.Pc;
            if (c0 == 'm' && c1 == 'm') return SvgUnit.Mm;
            if (c0 == 'c' && c1 == 'm') return SvgUnit.Cm;
            if (c0 == 'i' && c1 == 'n') return SvgUnit.In;
            if (c0 == 'e' && c1 == 'm') return SvgUnit.Em;
            if (c0 == 'e' && c1 == 'x') return SvgUnit.Ex;
            return SvgUnit.User; // Unknown two-char suffix.
        }

        public static float ResolveUnits(float value, SvgUnit unit, float fontSize, float percentOf)
        {
            switch (unit)
            {
                case SvgUnit.Percent: return value * 0.01f * percentOf;
                case SvgUnit.Pt: return value * 96f / 72f;
                case SvgUnit.Pc: return value * 96f / 6f;
                case SvgUnit.Mm: return value * 96f / 25.4f;
                case SvgUnit.Cm: return value * 960f / 25.4f;
                case SvgUnit.In: return value * 96f;
                case SvgUnit.Em:
                case SvgUnit.Ex: return value * fontSize;
                default: return value;
            }
        }

        private static char LowerAscii(char c) => (char)(c | 0x20);

        // ---------------------------------------------------------------- paint

        public enum PaintKind { None, Color, ServerRef, CurrentColor, Unspecified }

        public static bool TryParseLocalReference(string raw, out string fragment)
        {
            fragment = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            ReadOnlySpan<char> value = raw.AsSpan().Trim();
            if (value.Length < 2 || value[0] != '#') return false;
            fragment = value.Slice(1).ToString();
            return true;
        }

        public static bool TryParsePaint(
            ReadOnlySpan<char> s,
            out PaintKind kind,
            out SKColor color,
            out string serverFragment,
            out string fallbackColorText)
        {
            kind = PaintKind.Unspecified;
            color = default;
            serverFragment = null;
            fallbackColorText = null;
            s = s.Trim();
            if (s.IsEmpty)
            {
                return false;
            }

            if (EqIgnoreCase(s, "none") || EqIgnoreCase(s, "transparent"))
            {
                kind = PaintKind.None;
                return true;
            }
            if (EqIgnoreCase(s, "inherit"))
            {
                kind = PaintKind.Unspecified;
                return true;
            }
            if (EqIgnoreCase(s, "currentColor"))
            {
                kind = PaintKind.CurrentColor;
                return true;
            }

            if (StartsWithIgnoreCase(s, "url("))
            {
                int close = LastIndexOf(s, ')');
                var inner = close > 4 ? s.Slice(4, close - 4).Trim() : ReadOnlySpan<char>.Empty;

                if (inner.Length >= 2 &&
                    ((inner[0] == '"' && inner[inner.Length - 1] == '"') ||
                     (inner[0] == '\'' && inner[inner.Length - 1] == '\'')))
                {
                    inner = inner.Slice(1, inner.Length - 2).Trim();
                }

                var tail = close > 0 && close + 1 <= s.Length ? s.Slice(close + 1).Trim() : ReadOnlySpan<char>.Empty;
                string fallback = tail.IsEmpty ? null : tail.ToString();

                // SECURITY: only same-document fragments are honored; there is
                // no fetch code path in this renderer by construction.
                if (inner.StartsWith("#".AsSpan()))
                {
                    serverFragment = inner.Slice(1).ToString();
                    kind = PaintKind.ServerRef;
                    fallbackColorText = fallback;
                    return true;
                }

                if (fallback != null && TryParseColor(tail, out color))
                {
                    kind = PaintKind.Color;
                    return true;
                }
                kind = PaintKind.None;
                return true;
            }

            if (TryParseColor(s, out color))
            {
                kind = PaintKind.Color;
                return true;
            }

            return false;
        }

        public static bool TryParseColor(ReadOnlySpan<char> s, out SKColor color)
        {
            color = default;
            s = s.Trim();
            if (s.IsEmpty) return false;

            if (s[0] == '#')
            {
                return TryParseHexColor(s, out color);
            }

            if (StartsWithIgnoreCase(s, "rgb"))
            {
                return TryParseRgbFunction(s, out color);
            }

            if (StartsWithIgnoreCase(s, "hsl"))
            {
                return TryParseHslFunction(s, out color);
            }

            return SvgNamedColors.TryGet(s.ToString(), out color);
        }

        private static bool TryParseHexColor(ReadOnlySpan<char> s, out SKColor color)
        {
            color = default;
            var hex = s.Slice(1);
            if (hex.Length != 3 && hex.Length != 4 && hex.Length != 6 && hex.Length != 8)
            {
                return false;
            }

            byte r, g, b, a = 255;
            if (hex.Length <= 4)
            {
                int rn = HexValue(hex[0]);
                int gn = HexValue(hex[1]);
                int bn = HexValue(hex[2]);
                int an = hex.Length == 4 ? HexValue(hex[3]) : 15;
                if (rn < 0 || gn < 0 || bn < 0 || an < 0)
                {
                    return false;
                }
                r = (byte)(rn * 17);
                g = (byte)(gn * 17);
                b = (byte)(bn * 17);
                a = (byte)(an * 17);
            }
            else
            {
                if (!TryHexByte(hex, 0, out r) ||
                    !TryHexByte(hex, 2, out g) ||
                    !TryHexByte(hex, 4, out b))
                {
                    return false;
                }
                if (hex.Length == 8 && !TryHexByte(hex, 6, out a))
                {
                    return false;
                }
            }
            color = new SKColor(r, g, b, a);
            return true;
        }

        private static bool TryHexByte(ReadOnlySpan<char> hex, int offset, out byte value)
        {
            int hi = HexValue(hex[offset]);
            int lo = HexValue(hex[offset + 1]);
            if (hi < 0 || lo < 0)
            {
                value = 0;
                return false;
            }
            value = (byte)((hi << 4) | lo);
            return true;
        }

        private static bool TryParseRgbFunction(ReadOnlySpan<char> s, out SKColor color)
        {
            color = default;
            int open = IndexOf(s, '(');
            if (open < 0) return false;

            var header = s.Slice(0, open).Trim();
            bool hasAlpha;
            if (EqIgnoreCase(header, "rgb")) hasAlpha = false;
            else if (EqIgnoreCase(header, "rgba")) hasAlpha = true;
            else return false;

            int close = LastIndexOf(s, ')');
            if (close < open) return false;

            var body = s.Slice(open + 1, close - open - 1);
            var tok = new Tokenizer(body);
            Span<byte> ch = stackalloc byte[4];
            int count = 0;
            while (tok.Next(out var arg))
            {
                if (count >= (hasAlpha ? 4 : 3))
                {
                    return false;
                }
                bool percent = arg.EndsWith("%".AsSpan());
                if (percent) arg = arg.Slice(0, arg.Length - 1);
                if (!TryParseNumber(arg, out float v)) return false;
                if (count == 3 && hasAlpha)
                {
                    ch[count++] = percent
                        ? PercentToByte(v)
                        : UnitIntervalToByte(v);
                }
                else
                {
                    if (v < 0) v = 0;
                    ch[count++] = percent
                        ? PercentToByte(v)
                        : (byte)(v > 255 ? 255 : System.Math.Round(v));
                }
            }

            if (count != (hasAlpha ? 4 : 3))
            {
                return false;
            }

            color = new SKColor(ch[0], ch[1], ch[2], hasAlpha ? ch[3] : (byte)255);
            return true;
        }

        private static bool TryParseHslFunction(ReadOnlySpan<char> s, out SKColor color)
        {
            color = default;
            int open = IndexOf(s, '(');
            int close = LastIndexOf(s, ')');
            if (open < 0 || close < open) return false;

            var header = s.Slice(0, open).Trim();
            bool hasAlpha;
            if (EqIgnoreCase(header, "hsl")) hasAlpha = false;
            else if (EqIgnoreCase(header, "hsla")) hasAlpha = true;
            else return false;

            var tok = new Tokenizer(s.Slice(open + 1, close - open - 1));
            Span<float> values = stackalloc float[4];
            int count = 0;
            while (tok.Next(out var arg))
            {
                if (count >= (hasAlpha ? 4 : 3)) return false;
                bool percent = arg.EndsWith("%".AsSpan());
                if (percent) arg = arg.Slice(0, arg.Length - 1);
                if (!TryParseNumber(arg, out float value)) return false;
                if ((count == 1 || count == 2) && !percent) return false;
                if (count == 3 && hasAlpha && percent) value /= 100f;
                values[count++] = value;
            }
            if (count != (hasAlpha ? 4 : 3)) return false;

            double hue = values[0] % 360d;
            if (hue < 0d) hue += 360d;
            double saturation = System.Math.Clamp(values[1] / 100d, 0d, 1d);
            double lightness = System.Math.Clamp(values[2] / 100d, 0d, 1d);
            double chroma = (1d - System.Math.Abs(2d * lightness - 1d)) * saturation;
            double h = hue / 60d;
            double x = chroma * (1d - System.Math.Abs(h % 2d - 1d));
            double r1 = 0d, g1 = 0d, b1 = 0d;
            if (h < 1d) { r1 = chroma; g1 = x; }
            else if (h < 2d) { r1 = x; g1 = chroma; }
            else if (h < 3d) { g1 = chroma; b1 = x; }
            else if (h < 4d) { g1 = x; b1 = chroma; }
            else if (h < 5d) { r1 = x; b1 = chroma; }
            else { r1 = chroma; b1 = x; }
            double m = lightness - chroma / 2d;

            color = new SKColor(
                UnitIntervalToByte((float)(r1 + m)),
                UnitIntervalToByte((float)(g1 + m)),
                UnitIntervalToByte((float)(b1 + m)),
                hasAlpha ? UnitIntervalToByte(values[3]) : (byte)255);
            return true;
        }

        /// <summary>Deterministic percentage -> byte conversion in double space.</summary>
        private static byte PercentToByte(double percent)
        {
            if (percent >= 100) return 255;
            double scaled = percent * 255d / 100d;
            return (byte)(scaled - System.Math.Floor(scaled) >= 0.5
                ? System.Math.Ceiling(scaled)
                : System.Math.Floor(scaled));
        }

        private static byte UnitIntervalToByte(float value)
        {
            value = System.Math.Clamp(value, 0f, 1f);
            return (byte)System.Math.Round(value * 255f);
        }

        // ----------------------------------------------------------- transform

        public static bool TryParseTransformList(ReadOnlySpan<char> s, out SKMatrix matrix)
        {
            matrix = SKMatrix.Identity;
            s = s.Trim();
            if (s.IsEmpty)
            {
                return true;
            }

            int i = 0;
            int n = s.Length;
            while (i < n)
            {
                while (i < n && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
                if (i >= n) break;

                int nameStart = i;
                while (i < n && char.IsLetter(s[i])) i++;
                if (i == nameStart) return false;
                var fn = s.Slice(nameStart, i - nameStart);

                while (i < n && char.IsWhiteSpace(s[i])) i++;
                if (i >= n || s[i] != '(') return false;
                i++;
                int argStart = i;
                while (i < n && s[i] != ')') i++;
                if (i >= n) return false;
                var args = s.Slice(argStart, i - argStart);
                i++;

                if (!ApplyTransformFunction(fn, args, ref matrix))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool ApplyTransformFunction(ReadOnlySpan<char> fn, ReadOnlySpan<char> args, ref SKMatrix m)
        {
            var tok = new Tokenizer(args);
            Span<float> nums = stackalloc float[8];
            int count = 0;
            while (tok.Next(out var arg))
            {
                if (count >= nums.Length)
                {
                    return false;
                }
                if (!TryParseNumber(arg, out nums[count++]))
                {
                    return false;
                }
            }
            if (count == 0)
            {
                return false;
            }

            int n = count;

            // Zero-allocation dispatch: span compares instead of
            // ToString()+ToLowerInvariant() (two heap strings per function).
            SKMatrix t;
            if (EqIgnoreCase(fn, "matrix"))
            {
                if (n != 6) return false;
                t = new SKMatrix(
                    nums[0], nums[2], ClampCoord(nums[4]),
                    nums[1], nums[3], ClampCoord(nums[5]),
                    0f, 0f, 1f);
            }
            else if (EqIgnoreCase(fn, "translate"))
            {
                if (n != 1 && n != 2) return false;
                t = SKMatrix.CreateTranslation(
                    ClampCoord(nums[0]),
                    n == 2 ? ClampCoord(nums[1]) : 0f);
            }
            else if (EqIgnoreCase(fn, "scale"))
            {
                if (n != 1 && n != 2) return false;
                if (!IsFinite(nums[0]) || (n == 2 && !IsFinite(nums[1]))) return false;
                t = SKMatrix.CreateScale(nums[0], n == 2 ? nums[1] : nums[0]);
            }
            else if (EqIgnoreCase(fn, "rotate"))
            {
                if (n != 1 && n != 3) return false;
                if (n == 3)
                {
                    t = SKMatrix.CreateRotation(
                        DegreesToRadians(nums[0]),
                        ClampCoord(nums[1]),
                        ClampCoord(nums[2]));
                }
                else
                {
                    t = SKMatrix.CreateRotation(DegreesToRadians(nums[0]));
                }
            }
            else if (EqIgnoreCase(fn, "skewx"))
            {
                if (n != 1) return false;
                t = CreateSkew(DegreesToRadians(nums[0]), 0f);
            }
            else if (EqIgnoreCase(fn, "skewy"))
            {
                if (n != 1) return false;
                t = CreateSkew(0f, DegreesToRadians(nums[0]));
            }
            else
            {
                return false; // Unknown function invalidates the list (spec).
            }

            m = SKMatrix.Concat(m, t);
            return true;
        }

        public static float DegreesToRadians(float degrees)
        {
            return degrees * ((float)System.Math.PI / 180f);
        }

        private static SKMatrix CreateSkew(float radiansX, float radiansY)
        {
            float tx = (float)System.Math.Tan(radiansX);
            float ty = (float)System.Math.Tan(radiansY);
            if (!IsFinite(tx)) tx = 0f;
            if (!IsFinite(ty)) ty = 0f;
            return new SKMatrix(1f, tx, 0f, ty, 1f, 0f, 0f, 0f, 1f);
        }

        // ---------------------------------------------------------- list utils

        public ref struct Tokenizer
        {
            private ReadOnlySpan<char> _s;
            private int _i;

            public Tokenizer(ReadOnlySpan<char> s)
            {
                _s = s;
                _i = 0;
            }

            public bool IsEmpty => _i >= _s.Length;

            public bool Next(out ReadOnlySpan<char> token)
            {
                while (_i < _s.Length && (char.IsWhiteSpace(_s[_i]) || _s[_i] == ','))
                {
                    _i++;
                }
                if (_i >= _s.Length)
                {
                    token = default;
                    return false;
                }

                int start = _i;
                while (_i < _s.Length && !char.IsWhiteSpace(_s[_i]) && _s[_i] != ',')
                {
                    _i++;
                }
                token = _s.Slice(start, _i - start);
                return true;
            }
        }

        public static Tokenizer CreateTokenizer(ReadOnlySpan<char> s) => new Tokenizer(s);

        // ------------------------------------------------------------ helpers

        private static bool EqIgnoreCase(ReadOnlySpan<char> s, string other)
        {
            return s.Equals(other.AsSpan(), System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool StartsWithIgnoreCase(ReadOnlySpan<char> s, string prefix)
        {
            return s.StartsWith(prefix.AsSpan(), System.StringComparison.OrdinalIgnoreCase);
        }

        private static int IndexOf(ReadOnlySpan<char> s, char c) => s.IndexOf(c);

        private static int LastIndexOf(ReadOnlySpan<char> s, char c) => s.LastIndexOf(c);

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
