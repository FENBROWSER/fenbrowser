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

        private static void ApplyViewportTransform(
            SKCanvas canvas,
            ViewportContext viewport,
            bool hasViewBox,
            float vbX, float vbY, float vbW, float vbH,
            string parText)
        {
            if (!hasViewBox)
            {
                return;
            }

            ParsePreserveAspectRatio(parText, out ParAlign align, out ParMeet meet);

            if (align == ParAlign.None)
            {
                // Stretched to fill: independent scale factors, no letterboxing.
                canvas.Scale(viewport.Width / vbW, viewport.Height / vbH);
                canvas.Translate(-vbX, -vbY);
                return;
            }

            float scale = meet == ParMeet.Slice
                ? System.Math.Max(viewport.Width / vbW, viewport.Height / vbH)
                : System.Math.Min(viewport.Width / vbW, viewport.Height / vbH);

            float tx = 0f;
            float ty = 0f;
            float leftoverX = viewport.Width - vbW * scale;
            float leftoverY = viewport.Height - vbH * scale;

            if ((align & ParAlign.XMid) != 0) tx = leftoverX / 2f;
            else if ((align & ParAlign.XMax) != 0) tx = leftoverX;

            if ((align & ParAlign.YMid) != 0) ty = leftoverY / 2f;
            else if ((align & ParAlign.YMax) != 0) ty = leftoverY;

            canvas.Translate(tx, ty);
            canvas.Scale(scale, scale);
            canvas.Translate(-vbX, -vbY);
        }

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

        private static bool TryParseViewBox(string text, out float x, out float y, out float w, out float h)
        {
            x = y = w = h = 0f;
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
                    return false;
                }
                vb[count++] = num;
            }
            if (count != 4)
            {
                return false;
            }

            x = vb[0];
            y = vb[1];
            w = vb[2];
            h = vb[3];

            // Non-positive dimensions disable the viewport scaling entirely
            // (spec: element renders nothing scaled; never divide by zero).
            if (w <= 0f || h <= 0f || !SvgValues.IsFinite(w) || !SvgValues.IsFinite(h))
            {
                return false;
            }
            return true;
        }

        private float ResolveViewportLength(string raw, float parentDim)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 0f;
            }
            if (!SvgValues.TryParseLength(raw.AsSpan(), out float value, out var unit))
            {
                return 0f;
            }
            if (value < 0f)
            {
                return 0f; // Negative viewport lengths are ignored (not errors).
            }
            if (unit == SvgValues.SvgUnit.Percent)
            {
                return parentDim > 0f ? value * 0.01f * parentDim : 0f;
            }
            if (unit == SvgValues.SvgUnit.Em || unit == SvgValues.SvgUnit.Ex)
            {
                return 0f; // Font-relative intrinsic sizes unsupported at viewport level.
            }
            return SvgValues.ResolveUnits(value, unit, DefaultFontSize, 1f);
        }

    }
}
