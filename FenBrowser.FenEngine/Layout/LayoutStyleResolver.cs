using System;
using FenBrowser.Core.Css;

namespace FenBrowser.FenEngine.Layout
{
    internal static class LayoutStyleResolver
    {
        public static string GetEffectivePosition(CssComputed style)
        {
            if (style == null)
            {
                return null;
            }

            var position = style.Position;
            if (string.IsNullOrWhiteSpace(position) &&
                style.Map != null &&
                style.Map.TryGetValue("position", out var mappedPosition))
            {
                position = mappedPosition;
            }

            return string.IsNullOrWhiteSpace(position)
                ? null
                : position.Trim().ToLowerInvariant();
        }
    }
}
