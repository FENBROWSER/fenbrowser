using System;
using FenBrowser.FenEngine.Layout;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// Resolves SVG text through the browser font system. Headless Linux builds
    /// without a system font manager may supply one trusted process-level font
    /// file through FEN_SVG_FONT_PATH. SVG source can never select a file path.
    /// </summary>
    internal static class SvgTypefaceResolver
    {
        private static readonly Lazy<SKTypeface> ConfiguredFallback =
            new(LoadConfiguredFallback, isThreadSafe: true);

        public static SKTypeface Resolve(
            string family,
            string text,
            int weight,
            SKFontStyleSlant slant)
        {
            var configured = ConfiguredFallback.Value;
            if (configured != null && Supports(configured, text))
            {
                return configured;
            }

            var resolved = TextLayoutHelper.ResolveTypeface(family, text, weight, slant);
            return Supports(resolved, text) ? resolved : null;
        }

        private static SKTypeface LoadConfiguredFallback()
        {
            string path = Environment.GetEnvironmentVariable(
                Adapters.SvgRendererConfiguration.FontFallbackPathEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            try
            {
                return SKTypeface.FromFile(path);
            }
            catch
            {
                return null;
            }
        }

        private static bool Supports(SKTypeface typeface, string text)
        {
            if (typeface == null) return false;
            using var font = new SKFont(typeface);
            return font.ContainsGlyphs(text);
        }
    }
}
