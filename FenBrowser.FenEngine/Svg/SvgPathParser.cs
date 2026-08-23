using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// SVG path &lt;data&gt; parser producing an <see cref="SKPath"/>.
    ///
    /// Coverage: M/m L/l H/h V/v C/c S/s Q/q T/t A/a Z/z including implicit
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

        public static bool TryBuildPath(ReadOnlySpan<char> d, out SKPath path, SvgParseReport report)
        {
            using var builder = new SKPathBuilder();
            ParseInto(builder, d, report);
            path = builder.Detach();
            return true;
        }

        private static bool ParseInto(SKPathBuilder path, ReadOnlySpan<char> d, SvgParseReport report)
        {
            var scan = new Scanner(d);
            float curX = 0f, curY = 0f;
            float subStartX = 0f, subStartY = 0f;
            float lastCubicCtrlX = 0f, lastCubicCtrlY = 0f;
            float lastQuadCtrlX = 0f, lastQuadCtrlY = 0f;
            char prevCmd = '\0';
            int segments = 0;

            void AddSeg()
            {
                segments++;
                if (segments > MaxSegments)
                {
                    Truncate(report);
                }
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
                            do
                            {
                                if (!scan.TryReadNumber(out float x) ||
                                    !scan.TryReadNumber(out float y))
                                {
                                    return true;
                                }
                                x = Clamp(relative ? curX + x : x);
                                y = Clamp(relative ? curY + y : y);
                                if (move)
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
                            }
                            while (!report.TruncatedPathData && scan.MoreNumbersAhead());

                            prevCmd = move ? (relative ? 'm' : 'M') : (relative ? 'l' : 'L');
                            break;
                        }
                    case 'H':
                        {
                            while (!report.TruncatedPathData && scan.TryReadNumber(out float x))
                            {
                                x = Clamp(x);
                                path.LineTo(x, curY);
                                curX = x;
                                AddSeg();
                            }
                            prevCmd = relative ? 'h' : 'H';
                            break;
                        }
                    case 'V':
                        {
                            while (!report.TruncatedPathData && scan.TryReadNumber(out float y))
                            {
                                y = Clamp(y);
                                path.LineTo(curX, y);
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
                                c1x = Clamp(relative ? curX + c1x : c1x);
                                c1y = Clamp(relative ? curY + c1y : c1y);
                                c2x = Clamp(relative ? curX + c2x : c2x);
                                c2y = Clamp(relative ? curY + c2y : c2y);
                                ex = Clamp(relative ? curX + ex : ex);
                                ey = Clamp(relative ? curY + ey : ey);
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
                                c2x = Clamp(relative ? curX + c2x : c2x);
                                c2y = Clamp(relative ? curY + c2y : c2y);
                                ex = Clamp(relative ? curX + ex : ex);
                                ey = Clamp(relative ? curY + ey : ey);
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
                                qx = Clamp(relative ? curX + qx : qx);
                                qy = Clamp(relative ? curY + qy : qy);
                                ex = Clamp(relative ? curX + ex : ex);
                                ey = Clamp(relative ? curY + ey : ey);
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
                                ex = Clamp(relative ? curX + ex : ex);
                                ey = Clamp(relative ? curY + ey : ey);
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
                                float endAbsX = Clamp(relative ? curX + ex : ex);
                                float endAbsY = Clamp(relative ? curY + ey : ey);
                                AppendArc(
                                    path,
                                    Clamp(rx), Clamp(ry), rotationDeg,
                                    largeArc, sweep,
                                    curX, curY, endAbsX, endAbsY,
                                    ref segments, report, AddSeg);
                                curX = endAbsX;
                                curY = endAbsY;
                            }
                            while (!report.TruncatedPathData && scan.MoreNumbersAhead());
                            prevCmd = relative ? 'a' : 'A';
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
            ref int segments,
            SvgParseReport report,
            System.Action addSegment)
        {
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

            double phi = SvgValues.DegreesToRadians(rotationDegrees % 360f);
            double cosPhi = System.Math.Cos(phi);
            double sinPhi = System.Math.Sin(phi);

            double chordDx = (x1 - x2) / 2.0;
            double chordDy = (y1 - y2) / 2.0;
            double x1p = cosPhi * chordDx + sinPhi * chordDy;
            double y1p = -sinPhi * chordDx + cosPhi * chordDy;

            double rxSq = rx * rx;
            double rySq = ry * ry;
            double px = x1p * x1p;
            double py = y1p * y1p;

            // Radii scaling when too small to span the chord (spec F.6.6).
            double lambda = px / rxSq + py / rySq;
            if (lambda > 1.0)
            {
                double scale = System.Math.Sqrt(lambda);
                rx = (float)(rx * scale);
                ry = (float)(ry * scale);
                rxSq = rx * rx;
                rySq = ry * ry;
            }

            double denom = rxSq * py + rySq * px;
            if (denom <= double.Epsilon)
            {
                path.LineTo(x2, y2);
                addSegment();
                return;
            }

            double numerator = rxSq * rySq - denom;
            double root = System.Math.Sqrt(System.Math.Max(numerator / denom, 0.0));
            double sign = largeArc != sweep ? 1.0 : -1.0;
            double cxp = sign * root * (rx * y1p / ry);
            double cyp = sign * root * -(ry * x1p / rx);

            double cx = cosPhi * cxp - sinPhi * cyp + (x1 + x2) / 2.0;
            double cy = sinPhi * cxp + cosPhi * cyp + (y1 + y2) / 2.0;

            double ux = (x1p - cxp) / rx;
            double uy = (y1p - cyp) / ry;
            double vx = (-x1p - cxp) / rx;
            double vy = (-y1p - cyp) / ry;

            double theta1 = AngleBetween(1.0, 0.0, ux, uy);
            double delta = AngleBetween(ux, uy, vx, vy);
            if (!sweep && delta > 0.0) delta -= 2.0 * System.Math.PI;
            else if (sweep && delta < 0.0) delta += 2.0 * System.Math.PI;

            int pieceCount = (int)System.Math.Ceiling(System.Math.Abs(delta) / (System.Math.PI / 2.0));
            if (pieceCount < 1) pieceCount = 1;
            double deltaPerPiece = delta / pieceCount;
            double t = 4.0 / 3.0 * System.Math.Tan(deltaPerPiece / 4.0);

            double th = theta1;
            float ax = x1;
            float ay = y1;
            for (int i = 0; i < pieceCount; i++)
            {
                double thNext = th + deltaPerPiece;
                double cosTh = System.Math.Cos(th);
                double sinTh = System.Math.Sin(th);
                double cosThN = System.Math.Cos(thNext);
                double sinThN = System.Math.Sin(thNext);

                double ex = cx + rx * (cosPhi * cosThN - sinPhi * sinThN);
                double ey = cy + ry * (sinPhi * cosThN + cosPhi * sinThN);

                double dx1 = -rx * (cosPhi * sinTh + sinPhi * cosTh);
                double dy1 = -ry * (sinPhi * sinTh - cosPhi * cosTh);
                double dx2 = -rx * (cosPhi * sinThN + sinPhi * cosThN);
                double dy2 = -ry * (sinPhi * sinThN - cosPhi * cosThN);

                float c1x = Clamp(ax + (float)(t * dx1));
                float c1y = Clamp(ay + (float)(t * dy1));
                float c2x = Clamp((float)(ex - t * dx2));
                float c2y = Clamp((float)(ey - t * dy2));
                float endX = Clamp((float)ex);
                float endY = Clamp((float)ey);

                path.CubicTo(c1x, c1y, c2x, c2y, endX, endY);
                addSegment();

                ax = endX;
                ay = endY;
                th = thNext;
            }
        }

        private static double AngleBetween(double ux, double uy, double vx, double vy)
        {
            double dot = ux * vx + uy * vy;
            double len = System.Math.Sqrt(ux * ux + uy * uy) * System.Math.Sqrt(vx * vx + vy * vy);
            if (len <= double.Epsilon)
            {
                return 0.0;
            }
            double clamped = System.Math.Clamp(dot / len, -1.0, 1.0);
            double angle = System.Math.Acos(clamped);
            if (ux * vy - uy * vx < 0.0)
            {
                angle = -angle;
            }
            return angle;
        }

        // ------------------------------------------------------------- scanner

        private static bool IsVerb(char c)
        {
            switch (c)
            {
                case 'M': case 'm': case 'L': case 'l': case 'H': case 'h':
                case 'V': case 'v': case 'C': case 'c': case 'S': case 's':
                case 'Q': case 'q': case 'T': case 't': case 'A': case 'a':
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
