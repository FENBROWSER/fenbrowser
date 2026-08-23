using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// SVG path &lt;data&gt; parser producing an <see cref="SKPath"/>.
    ///
    /// Coverage: M/m L/l H/h V/v C/c S/s Q/q T/t A/a B/b Z/z including implicit
    /// command repetition (M->L), scientific notation, comma-or-space separators.
    ///
    /// Hardening:
    /// - <see cref="MaxSegments"/> bounds output size regardless of input length;
    ///   beyond budget the path is truncated and reported (never unbounded).
    /// - Every coordinate passes <see cref="SvgValues.ClampCoord"/> so non-finite
    ///   or astronomically large values never reach Skia.
    /// - Arc converter implements endpoint-to-center parameterization (spec F.6.5)
    ///   with explicit guards: zero radii -> line, coincident endpoints -> skip,
    ///   out-of-range radii -> spec scaling, guarded sqrt denominator.
    /// - Malformed data stops consumption at first bad token (browser-style:
    ///   render the parsed prefix rather than failing the document).
    /// </summary>
    internal static class SvgPathParser
    {
        public const int MaxSegments = 65536;

        public static bool TryBuildPath(
            ReadOnlySpan<char> d,
            out SKPath path,
            SvgParseReport report,
            System.Action budgetCheck = null)
        {
            using var builder = new SKPathBuilder();
            ParseInto(builder, d, report, budgetCheck);
            path = builder.Detach();
            return true;
        }

        private static bool ParseInto(
            SKPathBuilder path,
            ReadOnlySpan<char> d,
            SvgParseReport report,
            System.Action budgetCheck)
        {
            var scan = new Scanner(d);
            float curX = 0f, curY = 0f;
            float subStartX = 0f, subStartY = 0f;
            float lastCubicCtrlX = 0f, lastCubicCtrlY = 0f;
            float lastQuadCtrlX = 0f, lastQuadCtrlY = 0f;
            float bearingDegrees = 0f;
            char prevCmd = '\0';
            int segments = 0;

            void AddSeg()
            {
                segments++;
                if ((segments & 0xFF) == 0)
                {
                    budgetCheck?.Invoke();
                }
                if (segments > MaxSegments)
                {
                    Truncate(report);
                }
            }

            void ResolvePair(ref float x, ref float y, bool isRelative)
            {
                if (!isRelative)
                {
                    x = Clamp(x);
                    y = Clamp(y);
                    return;
                }
                float normalizedBearing = bearingDegrees % 360f;
                if (normalizedBearing < 0f) normalizedBearing += 360f;
                float dx = x;
                float dy = y;
                if (System.MathF.Abs(normalizedBearing) <= 0.0001f ||
                    System.MathF.Abs(normalizedBearing - 360f) <= 0.0001f)
                {
                    x = Clamp(curX + dx);
                    y = Clamp(curY + dy);
                    return;
                }
                if (System.MathF.Abs(normalizedBearing - 90f) <= 0.0001f)
                {
                    x = Clamp(curX - dy);
                    y = Clamp(curY + dx);
                    return;
                }
                if (System.MathF.Abs(normalizedBearing - 180f) <= 0.0001f)
                {
                    x = Clamp(curX - dx);
                    y = Clamp(curY - dy);
                    return;
                }
                if (System.MathF.Abs(normalizedBearing - 270f) <= 0.0001f)
                {
                    x = Clamp(curX + dy);
                    y = Clamp(curY - dx);
                    return;
                }
                double radians = SvgValues.DegreesToRadians(normalizedBearing);
                double cosine = System.Math.Cos(radians);
                double sine = System.Math.Sin(radians);
                x = Clamp(curX + (float)(cosine * dx - sine * dy));
                y = Clamp(curY + (float)(sine * dx + cosine * dy));
            }

            while (!scan.Eof)
            {
                scan.SkipWsp();
                if (scan.Eof || report.TruncatedPathData)
                {
                    break;
                }

                char cmd;
                char peeked = scan.Peek;
                if (!IsVerb(peeked))
                {
                    if (prevCmd == '\0')
                    {
                        return false; // Path data must begin with a verb.
                    }
                    if (prevCmd is 'Z' or 'z')
                    {
                        return true; // Close-path cannot be repeated with numbers.
                    }
                    cmd = prevCmd;
                    // Implicit repeat after M is L per spec.
                    cmd = cmd switch
                    {
                        'M' => 'L',
                        'm' => 'l',
                        _ => cmd
                    };
                }
                else
                {
                    cmd = scan.Take();
                    scan.SkipWsp();
                }

                bool relative = char.IsLower(cmd);
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                    case 'L':
                        {
                            bool move = char.ToUpperInvariant(cmd) == 'M';
                            bool firstPair = true;
                            do
                            {
                                if (!scan.TryReadNumber(out float x) ||
                                    !scan.TryReadNumber(out float y))
                                {
                                    return true;
                                }
                                ResolvePair(ref x, ref y, relative);
                                if (move && firstPair)
                                {
                                    path.MoveTo(x, y);
                                    subStartX = x;
                                    subStartY = y;
                                }
                                else
                                {
                                    path.LineTo(x, y);
                                }
                                curX = x;
                                curY = y;
                                AddSeg();
                                firstPair = false;
                            }
                            while (!report.TruncatedPathData && scan.MoreNumbersAhead());

                            prevCmd = relative ? 'l' : 'L';
                            break;
                        }
                    case 'H':
                        {
                            while (!report.TruncatedPathData && scan.TryReadNumber(out float x))
                            {
                                float y = 0f;
                                if (relative) ResolvePair(ref x, ref y, true);
                                else { x = Clamp(x); y = curY; }
                                path.LineTo(x, y);
                                curX = x;
                                curY = y;
                                AddSeg();
                            }
                            prevCmd = relative ? 'h' : 'H';
                            break;
                        }
                    case 'V':
                        {
                            while (!report.TruncatedPathData && scan.TryReadNumber(out float y))
                            {
                                float x = 0f;
                                if (relative) ResolvePair(ref x, ref y, true);
                                else { x = curX; y = Clamp(y); }
                                path.LineTo(x, y);
                                curX = x;
                                curY = y;
                                AddSeg();
                            }
                            prevCmd = relative ? 'v' : 'V';
                            break;
                        }
                    case 'C':
                        {
                            do
                            {
                                if (!scan.TryReadNumber(out float c1x) ||
                                    !scan.TryReadNumber(out float c1y) ||
                                    !scan.TryReadNumber(out float c2x) ||
                                    !scan.TryReadNumber(out float c2y) ||
                                    !scan.TryReadNumber(out float ex) ||
                                    !scan.TryReadNumber(out float ey))
                                {
                                    return true;
                                }
                                ResolvePair(ref c1x, ref c1y, relative);
                                ResolvePair(ref c2x, ref c2y, relative);
                                ResolvePair(ref ex, ref ey, relative);
                                path.CubicTo(c1x, c1y, c2x, c2y, ex, ey);
                                lastCubicCtrlX = c2x;
                                lastCubicCtrlY = c2y;
                                curX = ex;
                                curY = ey;
                                AddSeg();
                            }
                            while (!report.TruncatedPathData && scan.MoreNumbersAhead());
                            prevCmd = relative ? 'c' : 'C';
                            break;
                        }
                    case 'S':
                        {
                            do
                            {
                                if (!scan.TryReadNumber(out float c2x) ||
                                    !scan.TryReadNumber(out float c2y) ||
                                    !scan.TryReadNumber(out float ex) ||
                                    !scan.TryReadNumber(out float ey))
                                {
                                    return true;
                                }
                                float c1x;
                                float c1y;
                                bool reflect = prevCmd is 'C' or 'S' or 'c' or 's';
                                if (reflect)
                                {
                                    c1x = Clamp(2f * curX - lastCubicCtrlX);
                                    c1y = Clamp(2f * curY - lastCubicCtrlY);
                                }
                                else
                                {
                                    c1x = curX;
                                    c1y = curY;
                                }
                                ResolvePair(ref c2x, ref c2y, relative);
                                ResolvePair(ref ex, ref ey, relative);
                                path.CubicTo(c1x, c1y, c2x, c2y, ex, ey);
                                lastCubicCtrlX = c2x;
                                lastCubicCtrlY = c2y;
                                curX = ex;
                                curY = ey;
                                AddSeg();
                            }
                            while (!report.TruncatedPathData && scan.MoreNumbersAhead());
                            prevCmd = relative ? 's' : 'S';
                            break;
                        }
                    case 'Q':
                        {
                            do
                            {
                                if (!scan.TryReadNumber(out float qx) ||
                                    !scan.TryReadNumber(out float qy) ||
                                    !scan.TryReadNumber(out float ex) ||
                                    !scan.TryReadNumber(out float ey))
                                {
                                    return true;
                                }
                                ResolvePair(ref qx, ref qy, relative);
                                ResolvePair(ref ex, ref ey, relative);
                                path.QuadTo(qx, qy, ex, ey);
                                lastQuadCtrlX = qx;
                                lastQuadCtrlY = qy;
                                curX = ex;
                                curY = ey;
                                AddSeg();
                            }
                            while (!report.TruncatedPathData && scan.MoreNumbersAhead());
                            prevCmd = relative ? 'q' : 'Q';
                            break;
                        }
                    case 'T':
                        {
                            do
                            {
                                if (!scan.TryReadNumber(out float ex) ||
                                    !scan.TryReadNumber(out float ey))
                                {
                                    return true;
                                }
                                float qx;
                                float qy;
                                bool reflect = prevCmd is 'Q' or 'T' or 'q' or 't';
                                if (reflect)
                                {
                                    qx = Clamp(2f * curX - lastQuadCtrlX);
                                    qy = Clamp(2f * curY - lastQuadCtrlY);
                                }
                                else
                                {
                                    qx = curX;
                                    qy = curY;
                                }
                                ResolvePair(ref ex, ref ey, relative);
                                path.QuadTo(qx, qy, ex, ey);
                                lastQuadCtrlX = qx;
                                lastQuadCtrlY = qy;
                                curX = ex;
                                curY = ey;
                                AddSeg();
                            }
                            while (!report.TruncatedPathData && scan.MoreNumbersAhead());
                            prevCmd = relative ? 't' : 'T';
                            break;
                        }
                    case 'A':
                        {
                            do
                            {
                                if (!scan.TryReadNumber(out float rx) ||
                                    !scan.TryReadNumber(out float ry) ||
                                    !scan.TryReadNumber(out float rotationDeg) ||
                                    !scan.TryReadFlag(out bool largeArc) ||
                                    !scan.TryReadFlag(out bool sweep) ||
                                    !scan.TryReadNumber(out float ex) ||
                                    !scan.TryReadNumber(out float ey))
                                {
                                    return true;
                                }
                                ResolvePair(ref ex, ref ey, relative);
                                float endAbsX = ex;
                                float endAbsY = ey;
                                AppendArc(
                                    path,
                                    Clamp(rx), Clamp(ry),
                                    relative ? rotationDeg + bearingDegrees : rotationDeg,
                                    largeArc, sweep,
                                    curX, curY, endAbsX, endAbsY,
                                    AddSeg);
                                curX = endAbsX;
                                curY = endAbsY;
                            }
                            while (!report.TruncatedPathData && scan.MoreNumbersAhead());
                            prevCmd = relative ? 'a' : 'A';
                            break;
                        }
                    case 'B':
                        {
                            while (!report.TruncatedPathData && scan.TryReadNumber(out float angle))
                            {
                                bearingDegrees = Clamp(relative ? bearingDegrees + angle : angle);
                                AddSeg();
                            }
                            prevCmd = relative ? 'b' : 'B';
                            break;
                        }
                    case 'Z':
                        path.Close();
                        curX = subStartX;
                        curY = subStartY;
                        AddSeg();
                        prevCmd = 'Z';
                        break;
                    default:
                        return true; // Unknown verb: render prefix parsed so far.
                }
            }

            return true;
        }

        private static void Truncate(SvgParseReport report)
        {
            if (!report.TruncatedPathData)
            {
                report.TruncatedPathData = true;
                report.Warn("path data truncated at segment budget");
            }
        }

        private static float Clamp(float v) => SvgValues.ClampCoord(v);

        // ------------------------------------------------------------------ arc

        private static void AppendArc(
            SKPathBuilder path,
            float rx, float ry, float rotationDegrees,
            bool largeArc, bool sweep,
            float x1, float y1, float x2, float y2,
            System.Action addSegment)
        {
            rx = System.MathF.Abs(rx);
            ry = System.MathF.Abs(ry);
            if (rx <= 0f || ry <= 0f)
            {
                // Spec F.6.2: zero radii degrade the arc to a straight line.
                path.LineTo(x2, y2);
                addSegment();
                return;
            }

            if (x1 == x2 && y1 == y2)
            {
                return; // Spec F.6.5.1: coincident endpoints omit the arc.
            }

            path.ArcTo(
                rx,
                ry,
                rotationDegrees,
                largeArc ? SKPathArcSize.Large : SKPathArcSize.Small,
                sweep ? SKPathDirection.Clockwise : SKPathDirection.CounterClockwise,
                x2,
                y2);
            addSegment();
        }

        // ------------------------------------------------------------- scanner

        private static bool IsVerb(char c)
        {
            switch (c)
            {
                case 'M': case 'm': case 'L': case 'l': case 'H': case 'h':
                case 'V': case 'v': case 'C': case 'c': case 'S': case 's':
                case 'Q': case 'q': case 'T': case 't': case 'A': case 'a':
                case 'B': case 'b':
                case 'Z': case 'z':
                    return true;
                default:
                    return false;
            }
        }

        private ref struct Scanner
        {
            private readonly ReadOnlySpan<char> _s;
            private int _i;

            public Scanner(ReadOnlySpan<char> s)
            {
                _s = s;
            }

            public bool Eof => _i >= _s.Length;

            public char Peek => _s[_i];

            public char Take()
            {
                char c = _s[_i];
                _i++;
                return c;
            }

            public void SkipWsp()
            {
                while (_i < _s.Length && IsWs(_s[_i]))
                {
                    _i++;
                }
            }

            public bool TryReadNumber(out float value)
            {
                SkipCommaWsp();
                value = 0f;
                int n = _s.Length;
                int i = _i;

                bool negative = false;
                if (i < n && (_s[i] == '+' || _s[i] == '-'))
                {
                    negative = _s[i] == '-';
                    i++;
                }

                long head = 0;
                bool anyDigit = false;
                while (i < n && _s[i] >= '0' && _s[i] <= '9')
                {
                    anyDigit = true;
                    if (head <= 0x001FFFFFFFFFFFFF)
                    {
                        head = head * 10 + (_s[i] - '0');
                    }
                    i++;
                }

                long frac = 0;
                long fracScale = 1;
                if (i < n && _s[i] == '.')
                {
                    i++;
                    while (i < n && _s[i] >= '0' && _s[i] <= '9')
                    {
                        anyDigit = true;
                        if (fracScale <= 100000000L)
                        {
                            frac = frac * 10 + (_s[i] - '0');
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
                if (i < n && (_s[i] == 'e' || _s[i] == 'E'))
                {
                    i++;
                    bool expNeg = false;
                    if (i < n && (_s[i] == '+' || _s[i] == '-'))
                    {
                        expNeg = _s[i] == '-';
                        i++;
                    }
                    if (i >= n || _s[i] < '0' || _s[i] > '9')
                    {
                        return false;
                    }
                    int exp = 0;
                    while (i < n && _s[i] >= '0' && _s[i] <= '9')
                    {
                        exp = System.Math.Min(exp * 10 + (_s[i] - '0'), 128);
                        i++;
                    }
                    expFactor = System.Math.Pow(10, expNeg ? -exp : exp);
                }

                double d = head + frac / (double)fracScale;
                d *= expFactor;
                if (negative) d = -d;

                float f = (float)d;
                if (!SvgValues.IsFinite(f))
                {
                    return false;
                }

                _i = i;
                value = f;
                return true;
            }

            /// <summary>Arc flags are single 0/1 characters.</summary>
            public bool TryReadFlag(out bool flag)
            {
                SkipCommaWsp();
                flag = false;
                if (Eof)
                {
                    return false;
                }
                char c = Peek;
                if (c == '0') { flag = false; }
                else if (c == '1') { flag = true; }
                else return false;
                _i++;
                return true;
            }

            public bool MoreNumbersAhead()
            {
                int save = _i;
                SkipCommaWsp();
                int probe = _i;
                _i = save;

                if (probe >= _s.Length)
                {
                    return false;
                }
                char c = _s[probe];
                return char.IsDigit(c) || c == '-' || c == '+' || c == '.';
            }

            private void SkipCommaWsp()
            {
                while (_i < _s.Length && (IsWs(_s[_i]) || _s[_i] == ','))
                {
                    _i++;
                }
            }

            private static bool IsWs(char c)
            {
                return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v';
            }
        }
    }
}
