using System.Collections.Generic;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>Full CSS named color table (SVG 1.1 color keyword superset).</summary>
    internal static class SvgNamedColors
    {
        private static readonly Dictionary<string, SKColor> Table = Build();

        public static bool TryGet(string name, out SKColor color)
        {
            return Table.TryGetValue(name, out color);
        }

        private static Dictionary<string, SKColor> Build()
        {
            var t = new Dictionary<string, SKColor>(160, System.StringComparer.OrdinalIgnoreCase);

            void Add(string n, uint argb) =>
                t[n] = new SKColor(
                    (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

            // name, 0xAARRGGBB
            Add("aliceblue", 0xFFF0F8FF); Add("antiquewhite", 0xFFFAEBD7);
            Add("aqua", 0xFF00FFFF); Add("aquamarine", 0xFF7FFFD4);
            Add("azure", 0xFFF0FFFF); Add("beige", 0xFFF5F5DC);
            Add("bisque", 0xFFFFE4C4); Add("black", 0xFF000000);
            Add("blanchedalmond", 0xFFFFEBCD); Add("blue", 0xFF0000FF);
            Add("blueviolet", 0xFF8A2BE2); Add("brown", 0xFFA52A2A);
            Add("burlywood", 0xFFDEB887); Add("cadetblue", 0xFF5F9EA0);
            Add("chartreuse", 0xFF7FFF00); Add("chocolate", 0xFFD2691E);
            Add("coral", 0xFFFF7F50); Add("cornflowerblue", 0xFF6495ED);
            Add("cornsilk", 0xFFFFF8DC); Add("crimson", 0xFFDC143C);
            Add("cyan", 0xFF00FFFF); Add("darkblue", 0xFF00008B);
            Add("darkcyan", 0xFF008B8B); Add("darkgoldenrod", 0xFFB8860B);
            Add("darkgray", 0xFFA9A9A9); Add("darkgreen", 0xFF006400);
            Add("darkgrey", 0xFFA9A9A9); Add("darkkhaki", 0xFFBDB76B);
            Add("darkmagenta", 0xFF8B008B); Add("darkolivegreen", 0xFF556B2F);
            Add("darkorange", 0xFFFF8C00); Add("darkorchid", 0xFF9932CC);
            Add("darkred", 0xFF8B0000); Add("darksalmon", 0xFFE9967A);
            Add("darkseagreen", 0xFF8FBC8F); Add("darkslateblue", 0xFF483D8B);
            Add("darkslategray", 0xFF2F4F4F); Add("darkslategrey", 0xFF2F4F4F);
            Add("darkturquoise", 0xFF00CED1); Add("darkviolet", 0xFF9400D3);
            Add("deeppink", 0xFFFF1493); Add("deepskyblue", 0xFF00BFFF);
            Add("dimgray", 0xFF696969); Add("dimgrey", 0xFF696969);
            Add("dodgerblue", 0xFF1E90FF); Add("firebrick", 0xFFB22222);
            Add("floralwhite", 0xFFFFFAF0); Add("forestgreen", 0xFF228B22);
            Add("fuchsia", 0xFFFF00FF); Add("gainsboro", 0xFFDCDCDC);
            Add("ghostwhite", 0xFFF8F8FF); Add("gold", 0xFFFFD700);
            Add("goldenrod", 0xFFDAA520); Add("gray", 0xFF808080);
            Add("green", 0xFF008000); Add("greenyellow", 0xFFADFF2F);
            Add("grey", 0xFF808080); Add("honeydew", 0xFFF0FFF0);
            Add("hotpink", 0xFFFF69B4); Add("indianred", 0xFFCD5C5C);
            Add("indigo", 0xFF4B0082); Add("ivory", 0xFFFFFFF0);
            Add("khaki", 0xFFF0E68C); Add("lavender", 0xFFE6E6FA);
            Add("lavenderblush", 0xFFFFF0F5); Add("lawngreen", 0xFF7CFC00);
            Add("lemonchiffon", 0xFFFFFACD); Add("lightblue", 0xFFADD8E6);
            Add("lightcoral", 0xFFF08080); Add("lightcyan", 0xFFE0FFFF);
            Add("lightgoldenrodyellow", 0xFFFAFAD2); Add("lightgray", 0xFFD3D3D3);
            Add("lightgreen", 0xFF90EE90); Add("lightgrey", 0xFFD3D3D3);
            Add("lightpink", 0xFFFFB6C1); Add("lightsalmon", 0xFFFFA07A);
            Add("lightseagreen", 0xFF20B2AA); Add("lightskyblue", 0xFF87CEFA);
            Add("lightslategray", 0xFF778899); Add("lightslategrey", 0xFF778899);
            Add("lightsteelblue", 0xFFB0C4DE); Add("lightyellow", 0xFFFFFFE0);
            Add("lime", 0xFF00FF00); Add("limegreen", 0xFF32CD32);
            Add("linen", 0xFFFAF0E6); Add("magenta", 0xFFFF00FF);
            Add("maroon", 0xFF800000); Add("mediumaquamarine", 0xFF66CDAA);
            Add("mediumblue", 0xFF0000CD); Add("mediumorchid", 0xFFBA55D3);
            Add("mediumpurple", 0xFF9370DB); Add("mediumseagreen", 0xFF3CB371);
            Add("mediumslateblue", 0xFF7B68EE); Add("mediumspringgreen", 0xFF00FA9A);
            Add("mediumturquoise", 0xFF48D1CC); Add("mediumvioletred", 0xFFC71585);
            Add("midnightblue", 0xFF191970); Add("mintcream", 0xFFF5FFFA);
            Add("mistyrose", 0xFFFFE4E1); Add("moccasin", 0xFFFFE4B5);
            Add("navajowhite", 0xFFFFDEAD); Add("navy", 0xFF000080);
            Add("oldlace", 0xFFFDF5E6); Add("olive", 0xFF808000);
            Add("olivedrab", 0xFF6B8E23); Add("orange", 0xFFFFA500);
            Add("orangered", 0xFFFF4500); Add("orchid", 0xFFDA70D6);
            Add("palegoldenrod", 0xFFEEE8AA); Add("palegreen", 0xFF98FB98);
            Add("paleturquoise", 0xFFAFEEEE); Add("palevioletred", 0xFFDB7093);
            Add("papayawhip", 0xFFFFEFD5); Add("peachpuff", 0xFFFFDAB9);
            Add("peru", 0xFFCD853F); Add("pink", 0xFFFFC0CB);
            Add("plum", 0xFFDDA0DD); Add("powderblue", 0xFFB0E0E6);
            Add("purple", 0xFF800080); Add("rebeccapurple", 0xFF663399);
            Add("red", 0xFFFF0000); Add("rosybrown", 0xFFBC8F8F);
            Add("royalblue", 0xFF4169E1); Add("saddlebrown", 0xFF8B4513);
            Add("salmon", 0xFFFA8072); Add("sandybrown", 0xFFF4A460);
            Add("seagreen", 0xFF2E8B57); Add("seashell", 0xFFFFF5EE);
            Add("sienna", 0xFFA0522D); Add("silver", 0xFFC0C0C0);
            Add("skyblue", 0xFF87CEEB); Add("slateblue", 0xFF6A5ACD);
            Add("slategray", 0xFF708090); Add("slategrey", 0xFF708090);
            Add("snow", 0xFFFFFAFA); Add("springgreen", 0xFF00FF7F);
            Add("steelblue", 0xFF4682B4); Add("tan", 0xFFD2B48C);
            Add("teal", 0xFF008080); Add("thistle", 0xFFD8BFD8);
            Add("tomato", 0xFFFF6347); Add("turquoise", 0xFF40E0D0);
            Add("violet", 0xFFEE82EE); Add("wheat", 0xFFF5DEB3);
            Add("white", 0xFFFFFFFF); Add("whitesmoke", 0xFFF5F5F5);
            Add("yellow", 0xFFFFFF00); Add("yellowgreen", 0xFF9ACD32);

            return t;
        }
    }
}
