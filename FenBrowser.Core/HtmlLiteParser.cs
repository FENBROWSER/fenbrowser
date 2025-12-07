using System;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Net;

namespace FenBrowser.Core
{
    /// <summary>
    /// Extremely small, fault-tolerant HTML-ish parser that produces a LiteElement tree.
    /// Optimized for low allocations and stack safety on UWP/WP8.1.
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
            "math", "script", "style" // SVG handled normally to allow structure parsing
        };

        public HtmlLiteParser(string html)
        {
            _html = html ?? "";
            _n = _html.Length;
            _i = 0;
        }

        public LiteElement Parse()
        {
            var doc = new LiteElement("#document");
            var stack = new Stack<LiteElement>();
            stack.Push(doc);

            while (!Eof())
            {
                if (Peek() == '<')
                {
                    // Comment: <!-- ... -->
                    if (_i + 4 <= _n && _html.Substring(_i, 4) == "<!--")
                    {
                        var end = _html.IndexOf("-->", _i + 4, StringComparison.Ordinal);
                        if (end >= 0) { _i = end + 3; continue; }
                        _i = _n; break; // malformed, bail out
                    }

                    // Declaration / DOCTYPE: <! ... >
                    if (_i + 2 <= _n && _html[_i + 1] == '!')
                    {
                        _i += 2;
                        SkipDeclaration();
                        continue;
                    }

                    // End tag: </tag>
                    if (_i + 2 <= _n && _html[_i + 1] == '/')
                    {
                        _i += 2;
                        var endName = ReadTagNameLower();
                        while (!Eof() && Peek() != '>') _i++;
                        if (!Eof()) _i++;
                        while (stack.Count > 1 && !string.Equals(stack.Peek().Tag, endName, StringComparison.OrdinalIgnoreCase))
                            stack.Pop();
                        if (stack.Count > 1) stack.Pop();
                        continue;
                    }

                    // Start tag
                    _i++; // consume '<'
                    var tag = ReadTagNameLower();
                    var elem = new LiteElement(tag);

                    // Read attributes
                    while (!Eof())
                    {
                        SkipWs();
                        if (Peek() == '/' || Peek() == '>') break;
                        string lowerName, value, originalName, rawValue;
                        ReadAttribute(out lowerName, out value, out originalName, out rawValue);
                        if (!string.IsNullOrEmpty(lowerName)) elem.SetAttributeInternal(lowerName, originalName ?? lowerName, value, rawValue);
                    }

                    // Handle end of tag
                    bool selfClosing = false;
                    if (Peek() == '/')
                    {
                        selfClosing = true;
                        _i++;
                    }
                    if (Peek() == '>') _i++;

                    // HTML5 implicit tag closing: close <p> when certain block elements are encountered
                    if (TagsThatCloseP.Contains(tag))
                    {
                        while (stack.Count > 1 && string.Equals(stack.Peek().Tag, "p", StringComparison.OrdinalIgnoreCase))
                        {
                            stack.Pop();
                        }
                    }
                    
                    // Additional implicit closing rules for list items
                    if (tag == "li")
                    {
                        // Close any open <li> in the current list
                        while (stack.Count > 1 && string.Equals(stack.Peek().Tag, "li", StringComparison.OrdinalIgnoreCase))
                        {
                            stack.Pop();
                        }
                    }
                    
                    // Close <dt> or <dd> when opening another <dt> or <dd>
                    if (tag == "dt" || tag == "dd")
                    {
                        while (stack.Count > 1)
                        {
                            var current = stack.Peek().Tag?.ToLowerInvariant();
                            if (current == "dt" || current == "dd")
                                stack.Pop();
                            else
                                break;
                        }
                    }
                    
                    // Table elements auto-close rules
                    if (tag == "tr")
                    {
                        // Close any open <td>, <th>, or <tr>
                        while (stack.Count > 1)
                        {
                            var current = stack.Peek().Tag?.ToLowerInvariant();
                            if (current == "td" || current == "th" || current == "tr")
                                stack.Pop();
                            else
                                break;
                        }
                    }
                    if (tag == "td" || tag == "th")
                    {
                        // Close any open <td> or <th>
                        while (stack.Count > 1)
                        {
                            var current = stack.Peek().Tag?.ToLowerInvariant();
                            if (current == "td" || current == "th")
                                stack.Pop();
                            else
                                break;
                        }
                    }

                    // Add to parent
                    if (stack.Count > 0)
                    {
                        stack.Peek().Append(elem);
                    }

                    // Push if container
                    if (!selfClosing && !IsVoid(tag))
                    {
                        stack.Push(elem);

                        // Handle raw text elements (script, style)
                        if (ForeignContentTags.Contains(tag))
                        {
                            var rawBody = ReadRawElementBody(tag);
                            if (!string.IsNullOrEmpty(rawBody))
                                elem.Append(new LiteElement("#text") { Text = rawBody });
                            stack.Pop(); // Popped because ReadRawElementBody consumes the closing tag
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

        private void SkipDeclaration()
        {
            while (!Eof() && Peek() != '>') _i++;
            if (!Eof()) _i++;
        }

        private void SkipUntil(string terminal)
        {
            var idx = _html.IndexOf(terminal, _i, StringComparison.Ordinal);
            _i = idx >= 0 ? idx + terminal.Length : _n;
        }

        private string ReadUntil(string terminal)
        {
            var idx = _html.IndexOf(terminal, _i, StringComparison.Ordinal);
            if (idx < 0)
            {
                var s = _html.Substring(_i);
                _i = _n;
                return s;
            }
            var result = _html.Substring(_i, idx - _i);
            _i = idx + terminal.Length;
            return result;
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

                // Verify it's a real tag boundary (e.g. not </scriptVar>)
                var afterTag = idx + close.Length;
                if (afterTag < _n)
                {
                    char c = _html[afterTag];
                    if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    {
                        start = afterTag; // False alarm, keep searching
                        continue;
                    }
                }

                var result = _html.Substring(_i, idx - _i);
                _i = idx;

                // consume "</tag" ... up to '>'
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

            // Boolean attribute
            value = lowerName;
            rawValue = originalName;
        }

        private string ReadAttrName(out string lowerName)
        {
            var start = _i;
            while (!Eof())
            {
                var c = Peek();
                if (char.IsWhiteSpace(c) || c == '=' || c == '>' || c == '/' || c == '\0') break;
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
                while (!Eof() && Peek() != q) _i++;
                var s = _html.Substring(st, Math.Max(0, _i - st));
                rawValue = s;
                if (!Eof()) _i++; 
                return DecodeEntities(s);
            }

            var start = _i;
            while (!Eof())
            {
                var ch = Peek();
                if (char.IsWhiteSpace(ch) || ch == '>' || ch == '/' || ch == '\0') break;
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

        private bool TryMatch(string s)
        {
            if (_i + s.Length > _n) return false;
            for (int k = 0; k < s.Length; k++)
                if (_html[_i + k] != s[k]) return false;
            _i += s.Length;
            return true;
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

        // -------------------- Entities --------------------

        private static string DecodeEntities(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return WebUtility.HtmlDecode(s);
        }

        // -------------------- Utility --------------------

        private static void AppendText(LiteElement parent, string raw)
        {
            if (parent == null || string.IsNullOrEmpty(raw)) return;
            var last = parent.Children.Count > 0 ? parent.Children[parent.Children.Count - 1] : null;
            if (last != null && last.IsText)
                last.Text += raw;
            else
                parent.Append(new LiteElement("#text") { Text = raw });
        }
    }
}