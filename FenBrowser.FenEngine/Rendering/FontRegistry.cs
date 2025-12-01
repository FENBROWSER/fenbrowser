using System;
using System.Collections.Generic;
using Avalonia.Media;

namespace FenBrowser.FenEngine.Rendering
{
    // Minimal registry to map CSS font-family names to XAML FontFamily instances
    internal static class FontRegistry
    {
        private static readonly Dictionary<string, FontFamily> _map = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);

        public static void Register(string familyName, string uri)
        {
            if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(uri)) return;
            try { _map[familyName.Trim().Trim('\"', '\'')] = new FontFamily(uri); } catch { }
        }

        public static FontFamily TryResolve(string familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName)) return null;
            FontFamily ff; return _map.TryGetValue(familyName.Trim().Trim('\"', '\''), out ff) ? ff : null;
        }
    }
}

