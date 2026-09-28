using System.Collections.Generic;
using System.Diagnostics;
using SkiaSharp;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {

        private readonly struct ViewportContext
        {
            public ViewportContext(float w, float h)
            {
                Width = w;
                Height = h;
            }

            public float Width { get; }
            public float Height { get; }
        }

        private const float MaxViewportScale = 1_000_000f;

        private bool ApplyViewportTransform(
            SKCanvas canvas,
            ViewportContext viewport,
            bool hasViewBox,
            float vbX, float vbY, float vbW, float vbH,
            string parText)
        {
            if (!TryComputeViewportMatrix(
                    viewport, hasViewBox, vbX, vbY, vbW, vbH, parText, out SKMatrix viewportMatrix))
            {
                return false;
            }

            SKMatrix current = canvas.TotalMatrix;
            if (!IsBoundedViewportMatrix(viewportMatrix) ||
                !SvgValues.IsFinite(current) ||
                !SvgValues.IsFinite(SKMatrix.Concat(current, viewportMatrix)))
            {
                _report.RequireFallback("SVG viewport transform is not finite and bounded");
                return false;
            }
            if (!viewportMatrix.IsIdentity)
                canvas.Concat(viewportMatrix);
            return true;
        }

        /// <summary>
        /// The viewBox-to-viewport mapping (SVG 2 §8.2 "equivalent transform of an SVG
        /// viewport"), shared by drawing and by object bounding box resolution so both
        /// place viewport content identically. Identity without a viewBox or for an
        /// empty viewport.
        /// </summary>
        private bool TryComputeViewportMatrix(
            ViewportContext viewport,
            bool hasViewBox,
            float vbX, float vbY, float vbW, float vbH,
            string parText,
            out SKMatrix viewportMatrix)
        {
            viewportMatrix = SKMatrix.Identity;
            if (!hasViewBox)
            {
                return true;
            }
            if (!SvgValues.IsFinite(viewport.Width) || !SvgValues.IsFinite(viewport.Height) ||
                !SvgValues.IsFinite(vbX) || !SvgValues.IsFinite(vbY) ||
                !SvgValues.IsFinite(vbW) || !SvgValues.IsFinite(vbH) ||
                System.Math.Abs(vbX) > SvgValues.CoordClamp ||
                System.Math.Abs(vbY) > SvgValues.CoordClamp ||
                viewport.Width < 0f || viewport.Height < 0f || vbW <= 0f || vbH <= 0f)
            {
                _report.RequireFallback("SVG viewport transform is not finite and bounded");
                return false;
            }
            if (viewport.Width <= 0f || viewport.Height <= 0f)
                return true;

            float scaleX = viewport.Width / vbW;
            float scaleY = viewport.Height / vbH;
            if (!SvgValues.IsFinite(scaleX) || !SvgValues.IsFinite(scaleY) ||
                scaleX <= 0f || scaleY <= 0f ||
                System.Math.Abs(scaleX) > MaxViewportScale ||
                System.Math.Abs(scaleY) > MaxViewportScale)
            {
                _report.RequireFallback("SVG viewport transform scale exceeds the bounded limit");
                return false;
            }

            ParsePreserveAspectRatio(parText, out ParAlign align, out ParMeet meet);
            if (align == ParAlign.None)
            {
                if (!SvgValues.IsFinite(-vbX) || !SvgValues.IsFinite(-vbY) ||
                    System.Math.Abs(-vbX) > SvgValues.CoordClamp ||
                    System.Math.Abs(-vbY) > SvgValues.CoordClamp)
                {
                    _report.RequireFallback("SVG viewport transform composition is not bounded");
                    return false;
                }
                viewportMatrix = SKMatrix.Concat(
                    SKMatrix.CreateScale(scaleX, scaleY),
                    SKMatrix.CreateTranslation(-vbX, -vbY));
            }
            else
            {
                float scale = meet == ParMeet.Slice
                    ? System.Math.Max(scaleX, scaleY)
                    : System.Math.Min(scaleX, scaleY);
                float leftoverX = viewport.Width - vbW * scale;
                float leftoverY = viewport.Height - vbH * scale;
                if (!SvgValues.IsFinite(scale) || System.Math.Abs(scale) > MaxViewportScale ||
                    !SvgValues.IsFinite(leftoverX) || !SvgValues.IsFinite(leftoverY) ||
                    System.Math.Abs(leftoverX) > SvgValues.CoordClamp ||
                    System.Math.Abs(leftoverY) > SvgValues.CoordClamp)
                {
                    _report.RequireFallback("SVG viewport transform composition is not finite and bounded");
                    return false;
                }

                float tx = 0f;
                float ty = 0f;
                if ((align & ParAlign.XMid) != 0) tx = leftoverX / 2f;
                else if ((align & ParAlign.XMax) != 0) tx = leftoverX;
                if ((align & ParAlign.YMid) != 0) ty = leftoverY / 2f;
                else if ((align & ParAlign.YMax) != 0) ty = leftoverY;

                if (!SvgValues.IsFinite(tx) || !SvgValues.IsFinite(ty) ||
                    System.Math.Abs(tx) > SvgValues.CoordClamp ||
                    System.Math.Abs(ty) > SvgValues.CoordClamp)
                {
                    _report.RequireFallback("SVG viewport transform composition is not bounded");
                    return false;
                }
                viewportMatrix = SKMatrix.Concat(
                    SKMatrix.CreateTranslation(tx, ty),
                    SKMatrix.Concat(
                        SKMatrix.CreateScale(scale, scale),
                        SKMatrix.CreateTranslation(-vbX, -vbY)));
            }
            return true;
        }

        private static bool IsBoundedViewportMatrix(SKMatrix matrix) =>
            SvgValues.IsFinite(matrix) &&
            System.Math.Abs(matrix.ScaleX) <= MaxViewportScale &&
            System.Math.Abs(matrix.SkewX) <= MaxViewportScale &&
            System.Math.Abs(matrix.ScaleY) <= MaxViewportScale &&
            System.Math.Abs(matrix.SkewY) <= MaxViewportScale &&
            System.Math.Abs(matrix.TransX) <= SvgValues.CoordClamp &&
            System.Math.Abs(matrix.TransY) <= SvgValues.CoordClamp;

        [System.Flags]
        private enum ParAlign { XMin = 0, XMid = 1, XMax = 2, YMin = 0, YMid = 4, YMax = 8, None = 16 }

        private enum ParMeet { Meet, Slice }

        private static void ParsePreserveAspectRatio(string text, out ParAlign align, out ParMeet meet)
        {
            // Defaults per spec: xMidYMid meet. Malformed input falls back to
            // these defaults rather than failing the document.
            align = ParAlign.XMid | ParAlign.YMid;
            meet = ParMeet.Meet;

            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            // P0.5: allocation-free tokenization; case-insensitive keywords.
            // Alignment grammar is exactly 8 chars: 'x' + Min|Mid|Max + 'y' +
            // Min|Mid|Max. The previous 3+3 slice could never match and
            // silently pinned every alignment to the default.
            var tok = SvgValues.CreateTokenizer(text.AsSpan());
            while (tok.Next(out var raw))
            {
                if (raw.Equals("none".AsSpan(), System.StringComparison.OrdinalIgnoreCase))
                {
                    align = ParAlign.None;
                    continue;
                }
                if (raw.Equals("meet".AsSpan(), System.StringComparison.OrdinalIgnoreCase))
                {
                    meet = ParMeet.Meet;
                    continue;
                }
                if (raw.Equals("slice".AsSpan(), System.StringComparison.OrdinalIgnoreCase))
                {
                    meet = ParMeet.Slice;
                    continue;
                }
                if (raw.Length != 8)
                {
                    continue; // Malformed token: keep current state.
                }

                char hAxis = LowerAscii(raw[0]);
                char vAxis = LowerAscii(raw[4]); // "xMin|yMin": y prefix at idx 4
                if (hAxis != 'x' || vAxis != 'y')
                {
                    continue;
                }

                if (TryMapParComponent(raw.Slice(1, 3), isHorizontal: true, out var horizontal) &&
                    TryMapParComponent(raw.Slice(5, 3), isHorizontal: false, out var vertical))
                {
                    align = horizontal | vertical;
                }
            }
        }

        private static char LowerAscii(char c) => (char)(c | 0x20);

        private static bool TryMapParComponent(ReadOnlySpan<char> word, bool isHorizontal, out ParAlign value)
        {
            if (word.Equals("min".AsSpan(), System.StringComparison.OrdinalIgnoreCase))
            {
                value = 0;
                return true;
            }
            if (word.Equals("mid".AsSpan(), System.StringComparison.OrdinalIgnoreCase))
            {
                value = isHorizontal ? ParAlign.XMid : ParAlign.YMid;
                return true;
            }
            if (word.Equals("max".AsSpan(), System.StringComparison.OrdinalIgnoreCase))
            {
                value = isHorizontal ? ParAlign.XMax : ParAlign.YMax;
                return true;
            }

            value = 0;
            return false;
        }

        private static bool TryParseViewBox(
            string text,
            out float x,
            out float y,
            out float w,
            out float h,
            out bool disablesRendering)
        {
            x = y = w = h = 0f;
            disablesRendering = false;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var tok = SvgValues.CreateTokenizer(text.AsSpan());
            Span<float> vb = stackalloc float[4];
            int count = 0;
            while (tok.Next(out var token))
            {
                if (count >= 4 || !SvgValues.TryParseNumber(token, out float num))
                {
                    disablesRendering = true;
                    return false;
                }
                vb[count++] = num;
            }
            if (count != 4)
            {
                disablesRendering = true;
                return false;
            }

            x = vb[0];
            y = vb[1];
            w = vb[2];
            h = vb[3];

            if (!SvgValues.IsFinite(x) || !SvgValues.IsFinite(y) ||
                !SvgValues.IsFinite(w) || !SvgValues.IsFinite(h))
            {
                disablesRendering = true;
                return false;
            }
            if (w <= 0f || h <= 0f)
            {
                disablesRendering = true;
                return false;
            }
            return true;
        }

        private bool TryResolveViewportLength(string raw, float parentDim, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            // Viewport-relative units are sized against the host viewport, which a
            // standalone SVG render does not have. Resolving them against this
            // document's own viewport would invent an extent, so the document is
            // routed to compatibility fallback instead.
            if (SvgCssLengthEvaluator.HasViewportUnitDimension(raw))
            {
                _report.RequireFallback(
                    $"SVG viewport geometry property '{raw.Trim()}' requires a host viewport");
                return false;
            }
            if (!SvgValues.TryParseLength(raw.AsSpan(), out float parsed, out var unit) ||
                !SvgValues.IsFinite(parsed) || parsed < 0f)
                return false;
            if (unit == SvgValues.SvgUnit.Percent)
            {
                if (parentDim > 0f)
                    value = parsed * 0.01f * parentDim;
                else if (parsed == 0f)
                    value = 0f;
                else
                    return false;
            }
            else if (unit == SvgValues.SvgUnit.Em || unit == SvgValues.SvgUnit.Ex)
                return false;
            else
                value = SvgValues.ResolveUnits(parsed, unit, DefaultFontSize, 1f);
            return SvgValues.IsFinite(value) && value >= 0f;
        }

    }
}
