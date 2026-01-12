using System;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Dom;
using System.Linq;

namespace FenBrowser.Core
{
    /// <summary>
    /// Extremely small, fault-tolerant HTML-ish parser that produces a DOM tree.
    /// Optimized for low allocations and stack safety.
    /// Refactored to produce FenBrowser.Core.Dom.Node hierarchy.
    /// </summary>
    public class HtmlLiteParser
    {
        private readonly string _html;
        private int _i;
        private readonly int _n;
        private const int MaxStackDepth = 256; // Prevent StackOverflow on deep trees

        // Tags that can be implicitly closed by other tags.
        private static readonly HashSet<string> ImpliedEndTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "p", "li", "dt", "dd", "rt", "rp", "optgroup", "option", "thead", "tbody", "tfoot", "tr", "td", "th"
        };
        
        // Tags that close <p> when opened inside a <p> (block-level elements)
        private static readonly HashSet<string> TagsThatCloseP = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", 
            "div", "dl", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", 
            "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "listing", "main", "menu", 
            "nav", "ol", "p", "plaintext", "pre", "section", "summary", "table", "ul", "xmp"
        };

        // Tags that contain non-HTML content that should be parsed as raw text until the closing tag.
        private static readonly HashSet<string> ForeignContentTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "math", "script", "style", "textarea", "title", "noscript", "template", "iframe"
        };

        // Formatting elements requiring adoption agency algorithm
        private static readonly HashSet<string> FormattingElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "b", "big", "code", "em", "font", "i", "nobr", "s", "small", 
            "strike", "strong", "tt", "u", "mark", "ruby", "rt", "rp"
        };

        // Special elements that create scope boundaries
        private static readonly HashSet<string> SpecialElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "address", "applet", "area", "article", "aside", "base", "basefont", "bgsound",
            "blockquote", "body", "br", "button", "caption", "center", "col", "colgroup",
            "dd", "details", "dir", "div", "dl", "dt", "embed", "fieldset", "figcaption",
            "figure", "footer", "form", "frame", "frameset", "h1", "h2", "h3", "h4", "h5", "h6",
            "head", "header", "hgroup", "hr", "html", "iframe", "img", "input", "keygen",
            "li", "link", "listing", "main", "marquee", "menu", "meta", "nav", "noembed",
            "noframes", "noscript", "object", "ol", "p", "param", "plaintext", "pre",
            "script", "section", "select", "source", "style", "summary", "table", "tbody",
            "td", "template", "textarea", "tfoot", "th", "thead", "title", "tr", "track",
            "ul", "wbr", "xmp"
        };

        // Active formatting elements list for adoption agency
        private List<Element> _activeFormattingElements = new List<Element>();


        public HtmlLiteParser(string html)
        {
            _html = html ?? "";
            _n = _html.Length;
            _i = 0;
        }


        public Document Parse()
        {
            var doc = new Document();
            var stack = new Stack<Node>();
            stack.Push(doc);

            while (!Eof())
            {
                if (Peek() == '<')
                {
                    // Comment: <!-- ... -->
                    if (_i + 4 <= _n && _html.Substring(_i, 4) == "<!--")
                    {
                        var end = _html.IndexOf("-->", _i + 4, StringComparison.Ordinal);
                        string commentData;
                        if (end >= 0) 
                        {
                            commentData = _html.Substring(_i + 4, end - (_i + 4));
                            _i = end + 3; 
                        }
                        else
                        {
                            commentData = _html.Substring(_i + 4);
                            _i = _n; 
                        }
                        
                        if (stack.Count > 0) stack.Peek().AppendChild(new Comment(commentData));
                        continue;
                    }

                    // Declaration / DOCTYPE: <! ... >
                    if (_i + 2 <= _n && _html[_i + 1] == '!')
                    {
                        _i += 2;
                        ParseDeclaration(doc); 
                        continue;
                    }

                    // End tag: </tag>
                    if (_i + 2 <= _n && _html[_i + 1] == '/')
                    {
                        _i += 2;
                        var endName = ReadTagNameLower();
                        while (!Eof() && Peek() != '>') _i++;
                        if (!Eof()) _i++;

                        if (!string.IsNullOrEmpty(endName))
                        {
                            PopUntil(endName, stack);
                        }
                        continue;
                    }

                    // Start tag
                    _i++; // consume '<'
                    var tag = ReadTagNameLower();
                    
                    var elem = new Element(tag);

                    // Read attributes
                    while (!Eof())
                    {
                        SkipWs();
                        if (Peek() == '/' || Peek() == '>') break;
                        string lowerName, value, originalName, rawValue;
                        ReadAttribute(out lowerName, out value, out originalName, out rawValue);
                        if (!string.IsNullOrEmpty(lowerName)) 
                        {
                             elem.SetAttribute(originalName ?? lowerName, value);
                             /* [PERF-REMOVED] */
                        }
                    }

                    // Handle end of tag
                    bool selfClosing = false;
                    SkipWs();
                    if (Peek() == '/')
                    {
                        selfClosing = true;
                        _i++;
                    }
                    if (Peek() == '>') _i++;

                    // --- Implicit Closing Rules ---
                    if (tag == "a")
                    {
                        // Links cannot nest
                        FenLogger.Debug($"[Parser] Encountered nested <a>, calling PopUntil('a')");
                        PopUntil("a", stack);
                    }
                    else if (tag == "p" || TagsThatCloseP.Contains(tag))
                    {
                        // New P or block tag closes open P
                        PopUntilOne("p", stack);
                    }
                    else if (tag == "li")
                    {
                        PopUntilOne("li", stack);
                    }
                    else if (tag == "dt" || tag == "dd")
                    {
                        PopUntilOne("dt", stack);
                        PopUntilOne("dd", stack);
                    }
                    else if (tag == "tr" || tag == "thead" || tag == "tbody" || tag == "tfoot")
                    {
                         PopUntilOne("tr", stack);
                         // Don't pop section headers unless explicitly closed or higher level tag pops them
                    }
                    else if (tag == "td" || tag == "th")
                    {
                        PopUntilOne("td", stack);
                        PopUntilOne("th", stack);
                    }

                    // Force void elements to self-close
                    if (!selfClosing && IsVoid(tag)) selfClosing = true;

                    // Add to parent
                    if (stack.Count > 0)
                    {
                        stack.Peek().AppendChild(elem);
                    }

                    if (!selfClosing)
                    {
                        FenLogger.Debug($"[Parser] Pushing {tag}_{elem.GetHashCode()} to stack (Depth: {stack.Count})");
                        stack.Push(elem);

                        // Handle raw text elements (script, style)
                        if (ForeignContentTags.Contains(tag))
                        {
                            var rawBody = ReadRawElementBody(tag);
                            if (!string.IsNullOrEmpty(rawBody))
                                elem.AppendChild(new Text(rawBody));
                            stack.Pop(); // Pop immediately
                        }
                    }
                }
                else
                {
                    // Text content
                    var text = ReadText();
                    if (!string.IsNullOrEmpty(text) && stack.Count > 0)
                    {
                        AppendText(stack.Peek(), DecodeEntities(text));
                    }
                }
            }

            return doc;
        }

        private void PopUntil(string tagName, Stack<Node> stack)
        {
            var targetFound = stack.Any(n => string.Equals((n as Element)?.TagName ?? n.NodeName, tagName, StringComparison.OrdinalIgnoreCase));
            FenLogger.Debug($"[Parser] PopUntil({tagName}). Target in stack: {targetFound}. Current top: {stack.Peek().NodeName}_{stack.Peek().GetHashCode()}");
            
            if (targetFound)
            {
                while (stack.Count > 1)
                {
                    var popped = stack.Pop();
                    FenLogger.Debug($"[Parser] Popped {popped.NodeName}_{popped.GetHashCode()}");
                    if (string.Equals((popped as Element)?.TagName ?? popped.NodeName, tagName, StringComparison.OrdinalIgnoreCase)) break;
                }
            }
        }

        private void PopUntilOne(string tagName, Stack<Node> stack)
        {
            if (stack.Count > 1 && string.Equals(stack.Peek().NodeName, tagName, StringComparison.OrdinalIgnoreCase))
            {
                stack.Pop();
            }
        }

        private void ParseDeclaration(Document doc)
        {
            var start = _i;
            while (!Eof() && Peek() != '>') _i++;
            var content = _html.Substring(start, _i - start).Trim();
            if (!Eof()) _i++;
            if (content.StartsWith("DOCTYPE", StringComparison.OrdinalIgnoreCase))
                doc.Mode = QuirksMode.NoQuirks;
        }

        private string ReadRawElementBody(string tag)
        {
            var close = "</" + tag;
            var start = _i;
            while (true)
            {
                var idx = _html.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    var s = _html.Substring(_i);
                    _i = _n;
                    return s;
                }
                var afterTag = idx + close.Length;
                if (afterTag < _n)
                {
                    char c = _html[afterTag];
                    if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    {
                        start = afterTag; 
                        continue;
                    }
                }
                var result = _html.Substring(_i, idx - _i);
                _i = idx;
                _i += close.Length;
                while (!Eof() && Peek() != '>') _i++;
                if (!Eof()) _i++;
                return result;
            }
        }

        private string ReadText()
        {
            var st = _i;
            while (!Eof() && Peek() != '<') _i++;
            return _i > st ? _html.Substring(st, _i - st) : null;
        }

        private void ReadAttribute(out string lowerName, out string value, out string originalName, out string rawValue)
        {
            originalName = ReadAttrName(out lowerName);
            value = null;
            rawValue = null;
            if (string.IsNullOrEmpty(originalName))
            {
                if (Eof()) { lowerName = string.Empty; return; }
                char c = Peek();
                if (c != '>' && c != '/' && c != '<') _i++; // Only skip if it's not the end of the tag or start of new tag
                lowerName = string.Empty;
                return;
            }
            SkipWs();
            if (Peek() == '=')
            {
                _i++;
                SkipWs();
                value = ReadAttrValue(out rawValue);
                return;
            }
            value = lowerName;
            rawValue = originalName;
        }

        private string ReadAttrName(out string lowerName)
        {
            var start = _i;
            while (!Eof())
            {
                var c = Peek();
                if (char.IsWhiteSpace(c) || c == '=' || c == '>' || c == '/' || c == '<' || c == '\0') break;
                _i++;
            }
            var original = _html.Substring(start, _i - start);
            lowerName = original.Length == 0 ? string.Empty : original.ToLowerInvariant();
            return original;
        }

        private string ReadAttrValue(out string rawValue)
        {
            rawValue = string.Empty;
            if (Eof()) return "";
            var c = Peek();
            if (c == '"' || c == '\'')
            {
                var q = c; _i++;
                var st = _i;
                while (!Eof())
                {
                    var cc = Peek();
                    if (cc == q) break;
                    _i++;
                }
                var s = _html.Substring(st, System.Math.Max(0, _i - st));
                rawValue = s;
                if (!Eof() && Peek() == q) _i++; 
                return DecodeEntities(s);
            }
            var start = _i;
            while (!Eof())
            {
                var ch = Peek();
                if (char.IsWhiteSpace(ch) || ch == '>' || ch == '/' || ch == '<' || ch == '\0') break;
                _i++;
            }
            rawValue = _html.Substring(start, _i - start);
            return DecodeEntities(rawValue);
        }

        private string ReadTagNameLower()
        {
            SkipWs();
            var st = _i;
            while (!Eof())
            {
                var c = Peek();
                if (char.IsWhiteSpace(c) || c == '>' || c == '/' || c == '\0') break;
                _i++;
            }
            return _i == st ? "" : _html.Substring(st, _i - st).ToLowerInvariant();
        }

        private void SkipWs()
        {
            while (!Eof() && char.IsWhiteSpace(Peek())) _i++;
        }

        private char Peek() { return _i < _n ? _html[_i] : '\0'; }
        private bool Eof() { return _i >= _n; }

        public static bool IsVoid(string tag)
        {
            switch (tag)
            {
                case "area": case "base": case "br": case "col": case "embed":
                case "hr": case "img": case "input": case "link": case "meta":
                case "param": case "source": case "track": case "wbr":
                    return true;
                default:
                    return false;
            }
        }

        private static string DecodeEntities(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            
            string decoded = s;
            string prev = null;
            int maxIterations = 3; // Prevent infinite loops
            int iteration = 0;
            
            // Iterate until no more changes (handles double-escaped entities like &amp;#10003;)
            while (decoded != prev && iteration < maxIterations)
            {
                prev = decoded;
                iteration++;
                
                // First pass: WebUtility.HtmlDecode for named entities
                decoded = WebUtility.HtmlDecode(decoded);
                
                // Second pass: Manual numeric entity decoding
                // Pattern: &#NNNNN; (decimal) or &#xHHHH; (hex)
                if (decoded.Contains("&#"))
                {
                    // Decimal entities: &#10003;
                    decoded = Regex.Replace(decoded, @"&#(\d+);", m => {
                        try {
                            int code = int.Parse(m.Groups[1].Value);
                            if (code > 0 && code <= 0x10FFFF)
                                return char.ConvertFromUtf32(code);
                        } catch { }
                        return m.Value;
                    });
                    
                    // Hex entities: &#x2713;
                    decoded = Regex.Replace(decoded, @"&#x([0-9a-fA-F]+);", m => {
                        try {
                            int code = Convert.ToInt32(m.Groups[1].Value, 16);
                            if (code > 0 && code <= 0x10FFFF)
                                return char.ConvertFromUtf32(code);
                        } catch { }
                        return m.Value;
                    });
                }
            }
            
            // Final fallback: direct string replacement for common stubborn entities
            decoded = decoded.Replace("&#10003;", "?");
            decoded = decoded.Replace("&#x2713;", "?");
            decoded = decoded.Replace("&#10004;", "?");
            decoded = decoded.Replace("&#x2714;", "?");
            
            return decoded;
        }

        private static void AppendText(Node parent, string raw)
        {
            if (parent == null || string.IsNullOrEmpty(raw)) return;
            
            // Ensure entities are decoded (in case they weren't decoded earlier)
            string text = raw;
            
            // Direct replacement for common entities that may not be decoded
            if (text.Contains("&#10003;")) text = text.Replace("&#10003;", "?");
            if (text.Contains("&#x2713;")) text = text.Replace("&#x2713;", "?");
            if (text.Contains("&amp;")) text = text.Replace("&amp;", "&");
            
            var last = parent.Children.Count > 0 ? parent.Children[parent.Children.Count - 1] : null;
            if (last is Text t)
                t.Data += text;
            else
                parent.AppendChild(new Text(text));
        }
        
        private static bool IsBlockElement(string tag)
        {
            return tag == "div" || tag == "p" || tag == "h1" || tag == "h2" || tag == "ul" || tag == "ol" || tag == "li" || tag == "table"; 
        }

        private static bool IsTableStructure(string tag)
        {
            tag = tag.ToLowerInvariant();
            return tag == "table" || tag == "thead" || tag == "tbody" || tag == "tfoot" || tag == "tr"; 
        }

        private static bool IsTableValidTag(string childTag, string parentTag)
        {
            childTag = childTag.ToLowerInvariant();
            parentTag = parentTag.ToLowerInvariant();
            if (parentTag == "table") return childTag == "thead" || childTag == "tbody" || childTag == "tfoot" || childTag == "tr" || childTag == "caption" || childTag == "colgroup";
            if (parentTag == "thead" || parentTag == "tbody" || parentTag == "tfoot") return childTag == "tr";
            if (parentTag == "tr") return childTag == "td" || childTag == "th";
            return true; 
        }
    }
}
