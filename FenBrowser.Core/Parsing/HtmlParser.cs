using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Parsing
{
    public class HtmlParser : IHtmlParser
    {
        private readonly string _html;

        private readonly Uri _baseUri;
        private readonly Network.ResourcePrefetcher _prefetcher;
        private readonly ParserSecurityPolicy _securityPolicy;

        public HtmlParser(string html, Uri baseUri = null, Network.ResourcePrefetcher prefetcher = null, ParserSecurityPolicy securityPolicy = null)
        {
            _html = html;
            _baseUri = baseUri ?? new Uri("about:blank");
            _prefetcher = prefetcher;
            _securityPolicy = securityPolicy?.Clone() ?? ParserSecurityPolicy.Default;
        }

        public Document Parse()
        {
            return Parse(_html);
        }

        public Document Parse(string html)
        {
            // Phase 2.2: Speculative Preload Scanning
            if (_prefetcher != null)
            {
                var scanner = new PreloadScanner(html, _baseUri, _prefetcher);
                // Fire and forget, don't block parsing
                scanner.ScanAsync();
            }

            var builder = new HtmlTreeBuilder(html)
            {
                MaxTokenizerEmissions = _securityPolicy.HtmlMaxTokenEmissions,
                MaxOpenElementsDepth = _securityPolicy.HtmlMaxOpenElementsDepth
            };
            var doc = builder.Build();
            doc.URL = _baseUri.AbsoluteUri;
            doc.BaseURI = _baseUri.AbsoluteUri; // Ensure doc knows its base
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

