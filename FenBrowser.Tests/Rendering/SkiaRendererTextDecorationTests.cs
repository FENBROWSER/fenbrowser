using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Backends;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class SkiaRendererTextDecorationTests
    {
        [Fact]
        public void MeasureRenderedTextWidth_UsesRealFontMeasurementForLinkUnderline()
        {
            const string text = "Learn more";
            const float fontSize = 16f;

            float measured = InvokeMeasureRenderedTextWidth(text, fontSize, SKTypeface.Default);

            using var paint = new SKPaint
            {
                Typeface = SKTypeface.Default,
                TextSize = fontSize,
                IsAntialias = true
            };

            float expected = paint.MeasureText(text);

            Assert.True(measured > 0);
            Assert.InRange(measured, expected - 0.01f, expected + 0.01f);
        }

        [Fact]
        public void DrawText_UsesMeasuredWidthForUnderlineBounds()
        {
            var renderer = new SkiaRenderer();
            var backend = new HeadlessRenderBackend();
            const string text = "Learn more";
            const float fontSize = 16f;

            var node = new TextPaintNode
            {
                Bounds = new SKRect(0, 0, 200, 40),
                TextOrigin = new SKPoint(24, 32),
                FallbackText = text,
                FontSize = fontSize,
                Typeface = SKTypeface.Default,
                Color = SKColors.Blue,
                TextDecorations = new[] { "underline" }
            };

            var drawText = typeof(SkiaRenderer).GetMethod("DrawText", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(drawText);

            drawText!.Invoke(renderer, new object[] { backend, node });

            var underline = backend.CommandLog.Last(c => c.Name == "DrawRect");
            float underlineRight = ParseRectRight(underline.Args);
            float expectedRight = node.TextOrigin.X + InvokeMeasureRenderedTextWidth(text, fontSize, SKTypeface.Default);

            Assert.InRange(underlineRight, expectedRight - 0.01f, expectedRight + 0.01f);
        }

        private static float InvokeMeasureRenderedTextWidth(string text, float fontSize, SKTypeface typeface)
        {
            var method = typeof(SkiaRenderer).GetMethod("MeasureRenderedTextWidth", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object result = method!.Invoke(null, new object[] { text, fontSize, typeface });
            return Assert.IsType<float>(result);
        }

        private static float ParseRectRight(string args)
        {
            const string marker = "rect=";
            int start = args.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0);

            int open = args.IndexOf('{', start);
            int close = args.IndexOf('}', open + 1);
            Assert.True(open >= 0 && close > open);

            string rectText = args.Substring(open + 1, close - open - 1);
            string[] parts = rectText.Split(',');
            Assert.True(parts.Length >= 4);

            float left = ParseRectPart(parts[0], "Left");
            float width = ParseRectPart(parts[2], "Width");
            return left + width;
        }

        private static float ParseRectPart(string part, string label)
        {
            string prefix = label + "=";
            string trimmed = part.Trim();
            Assert.StartsWith(prefix, trimmed, StringComparison.Ordinal);
            return float.Parse(trimmed.Substring(prefix.Length), CultureInfo.InvariantCulture);
        }
    }
}
