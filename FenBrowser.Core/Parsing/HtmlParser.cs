using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Parsing
{
    public class HtmlParser
    {
        private readonly string _html;

        public HtmlParser(string html)
        {
            _html = html;
        }

        public LiteElement Parse()
        {
            var builder = new HtmlTreeBuilder(_html);
            var doc = builder.Build();
            return doc;
        }
        
        public static bool IsVoid(string tag)
        {
            // Void elements from HTML5 spec
            // area, base, br, col, embed, hr, img, input, link, meta, source, track, wbr
            if (string.IsNullOrEmpty(tag)) return false;
            var t = tag.ToLowerInvariant();
            return t == "area" || t == "base" || t == "br" || t == "col" || t == "embed" ||
                   t == "hr" || t == "img" || t == "input" || t == "link" || t == "meta" ||
                   t == "source" || t == "track" || t == "wbr" ||
                   // FIX: Treat SVG common shapes as void to prevent nesting if not self-closed in raw XML
                   t == "path" || t == "rect" || t == "circle" || t == "line" || t == "polyline" || 
                   t == "polygon" || t == "ellipse" || t == "stop" || t == "use" || t == "image";
        }
    }
}
