using System;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
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
            "math", "script", "style" // SVG handled normally to allow structure parsing
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
                        SkipDeclaration(); // TODO: Parse doctype properly if needed
                        continue;
                    }

                    // End tag: </tag>
                    if (_i + 2 <= _n && _html[_i + 1] == '/')
                    {
                        _i += 2;
                        var endName = ReadTagNameLower();
                        while (!Eof() && Peek() != '>') _i++;
                        if (!Eof()) _i++;

                        // FenBrowser Mod: Adoption Agency Algorithm Integration
                        if (FormattingElements.Contains(endName))
                        {
                            // FenLogger.Debug($"[HtmlLiteParser] Triggering Adoption Agency for </{endName}>");
                            RunAdoptionAgency(endName, stack);
                            continue;
                        }

                        // Close elements up to matching tag
                        // Note: Document root (#document) never matches tag name, so stack > 1 check preserves root.
                        while (stack.Count > 1 && !string.Equals(stack.Peek().NodeName, endName, StringComparison.OrdinalIgnoreCase))
                            stack.Pop();
                        if (stack.Count > 1) stack.Pop(); // Pop the matching element
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
                             // Using standard SetAttribute for now as SetAttributeInternal was mostly for casing preservation which we support
                             elem.SetAttribute(originalName ?? lowerName, value);
                             
                             // DEBUG: Log SVG path attributes
                             if (tag == "path" || tag == "svg")
                             {
                                 try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_parser.txt", $"[PARSER] {tag}.{lowerName}={value?.Substring(0, Math.Min(value?.Length ?? 0, 50))}\r\n"); } catch {}
                             }
                        }
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
                        while (stack.Count > 1 && string.Equals(stack.Peek().NodeName, "p", StringComparison.OrdinalIgnoreCase))
                        {
                            stack.Pop();
                        }
                    }
                    
                    // Additional implicit closing rules
                    if (tag == "li")
                    {
                         while (stack.Count > 1 && string.Equals(stack.Peek().NodeName, "li", StringComparison.OrdinalIgnoreCase)) stack.Pop();
                    }
                    if (tag == "dt" || tag == "dd")
                    {
                        while (stack.Count > 1)
                        {
                            var current = stack.Peek().NodeName.ToLowerInvariant();
                            if (current == "dt" || current == "dd") stack.Pop();
                            else break;
                        }
                    }
                    if (tag == "tr")
                    {
                        while (stack.Count > 1)
                        {
                            var current = stack.Peek().NodeName.ToLowerInvariant();
                            if (current == "td" || current == "th" || current == "tr") stack.Pop();
                            else break;
                        }
                    }
                    if (tag == "td" || tag == "th")
                    {
                        while (stack.Count > 1)
                        {
                            var current = stack.Peek().NodeName.ToLowerInvariant();
                            if (current == "td" || current == "th") stack.Pop();
                            else break;
                        }
                    }

                    // Add to parent
                    if (stack.Count > 0)
                    {
                        var parent = stack.Peek();
                        // Foster Parenting logic (simplified)
                        if (stack.Count > 1 && IsTableStructure(parent.NodeName) && !IsTableValidTag(tag, parent.NodeName))
                        {
                            // Try to hoist out of table
                            if ((parent.NodeName.Equals("table", StringComparison.OrdinalIgnoreCase) || 
                                 parent.NodeName.Equals("tbody", StringComparison.OrdinalIgnoreCase) ||
                                 parent.NodeName.Equals("tr", StringComparison.OrdinalIgnoreCase)) && IsBlockElement(tag))
                            {
                                // Append to table itself? No, before table. But we don't have access to Stack[n-1] easily.
                                // For safely, just append to current parent.
                                parent.AppendChild(elem);
                            }
                            else
                            {
                                parent.AppendChild(elem);
                            }
                        }
                        else
                        {
                            parent.AppendChild(elem);
                        }
                    }

                    // Force void elements to self-close
                    if (!selfClosing && IsVoid(tag)) selfClosing = true;

                    // Push if container
                    if (!selfClosing)
                    {
                        if (FormattingElements.Contains(tag))
                        {
                            PushFormattingElement(elem);
                        }
                        
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

        private void SkipDeclaration()
        {
            while (!Eof() && Peek() != '>') _i++;
            if (!Eof()) _i++;
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

                // Verify it's a real tag boundary
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
                _i++;
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
            
                var s_start = st;
                while (!Eof())
                {
                    var cc = Peek();
                    if (cc == q) break;
                    if (cc == '>') break; // Recovery
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

        private char Peek() { return _i < _n ? _html[_i] : '\0'; }
        private bool Eof() { return _i >= _n; }

        public static bool IsVoid(string tag)
        {
            switch (tag)
            {
                case "area": case "base": case "br": case "col": case "embed":
                case "hr": case "img": case "input": case "link": case "meta":
                case "param": case "source": case "track": case "wbr":
                case "path": case "rect": case "circle": case "line": case "polyline": 
                case "polygon": case "ellipse": case "stop": case "use": case "image":
                    return true;
                default:
                    return false;
            }
        }

        private static string DecodeEntities(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return WebUtility.HtmlDecode(s);
        }

        private static void AppendText(Node parent, string raw)
        {
            if (parent == null || string.IsNullOrEmpty(raw)) return;
            var last = parent.Children.Count > 0 ? parent.Children[parent.Children.Count - 1] : null;
            if (last is Text t)
                t.Data += raw;
            else
                parent.AppendChild(new Text(raw));
        }
        
        // ---- Helpers for Foster Parenting ----
        private static bool IsBlockElement(string tag)
        {
            // Simple subset of block elements
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

        // -------------------- Adoption Agency Algorithm --------------------

        private void RunAdoptionAgency(string endTag, Stack<Node> stack)
        {
            const int outerLoopLimit = 8;
            for (int outerLoop = 0; outerLoop < outerLoopLimit; outerLoop++)
            {
                // Find formatting element
                int formattingIndex = -1;
                Element formattingElement = null;
                for (int i = _activeFormattingElements.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_activeFormattingElements[i].TagName, endTag, StringComparison.OrdinalIgnoreCase))
                    {
                        formattingIndex = i;
                        formattingElement = _activeFormattingElements[i];
                        break;
                    }
                }

                if (formattingElement == null)
                {
                    while (stack.Count > 1 && !string.Equals(stack.Peek().NodeName, endTag, StringComparison.OrdinalIgnoreCase))
                        stack.Pop();
                    if (stack.Count > 1) stack.Pop();
                    return;
                }

                // Check if in stack
                var stackList = new List<Node>(stack);
                stackList.Reverse();
                int stackIndex = stackList.FindIndex(e => ReferenceEquals(e, formattingElement));
                
                if (stackIndex < 0)
                {
                    _activeFormattingElements.RemoveAt(formattingIndex);
                    return;
                }

                // Find furthest block
                Element furthestBlock = null;
                for (int i = stackIndex + 1; i < stackList.Count; i++)
                {
                    // Treat any element that is "Special" as a block for this purpose
                    if (stackList[i] is Element el && SpecialElements.Contains(el.TagName))
                    {
                        furthestBlock = el;
                        break;
                    }
                }

                if (furthestBlock == null)
                {
                    while (stack.Count > 0 && !ReferenceEquals(stack.Peek(), formattingElement))
                        stack.Pop();
                    if (stack.Count > 0) stack.Pop();
                    _activeFormattingElements.RemoveAt(formattingIndex);
                    return;
                }

                // Cloning and Reparenting
                var commonAncestor = stackIndex > 0 ? stackList[stackIndex - 1] : stackList[0];
                var clone = formattingElement.ShallowClone();
                
                foreach (var child in new List<Node>(furthestBlock.Children))
                {
                    child.Remove(); // Logic handles parent/child relation update
                    clone.AppendChild(child);
                }
                
                furthestBlock.AppendChild(clone);
                
                if (formattingIndex < _activeFormattingElements.Count)
                {
                    _activeFormattingElements[formattingIndex] = clone;
                }

                // Pop until formatting element
                while (stack.Count > 0 && !ReferenceEquals(stack.Peek(), formattingElement))
                    stack.Pop();
                if (stack.Count > 0) stack.Pop();
                
                return;
            }
        }

        private void PushFormattingElement(Element element)
        {
            int count = 0;
            for (int i = _activeFormattingElements.Count - 1; i >= 0; i--)
            {
                var el = _activeFormattingElements[i];
                if (string.Equals(el.TagName, element.TagName, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                    if (count >= 3)
                    {
                        _activeFormattingElements.RemoveAt(i);
                        break;
                    }
                }
            }
            _activeFormattingElements.Add(element);
        }
    }
}
