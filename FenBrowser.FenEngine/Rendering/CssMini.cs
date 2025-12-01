using System;
using Avalonia.Media;

namespace FenBrowser.FenEngine.Rendering
{
    public static class CssMini
    {
        public static Avalonia.Media.Color? ParseColor(string value) => CssParser.ParseColor(value);
        public static object Parse(string css, int sourceIndex) => null;
    }
}