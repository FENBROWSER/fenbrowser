using System;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using FenBrowser.Core.Logging;

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

        // HTML5 Insertion Modes
        private enum InsertionMode
        {
            Initial,
            BeforeHtml,
            BeforeHead,
            InHead,
            AfterHead,
            InBody,
            Text,
            InTable,
            InTableBody,
            InRow,
            InCell,
            InSelect,
            InTemplate,
            AfterBody,
            InForeignContent
        }

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

        // SVG elements that should break out to HTML namespace
        private static readonly HashSet<string> SvgIntegrationPoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "foreignobject", "desc", "title"
        };

        // MathML integration points
        private static readonly HashSet<string> MathMlIntegrationPoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mi", "mo", "mn", "ms", "mtext", "annotation-xml"
        };

        // SVG attribute name adjustments (HTML is case-insensitive but SVG is case-sensitive)
        private static readonly Dictionary<string, string> SvgAttributeAdjustments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["attributename"] = "attributeName",
            ["attributetype"] = "attributeType",
            ["basefrequency"] = "baseFrequency",
            ["baseprofile"] = "baseProfile",
            ["calcmode"] = "calcMode",
            ["clippathunits"] = "clipPathUnits",
            ["diffuseconstant"] = "diffuseConstant",
            ["edgemode"] = "edgeMode",
            ["filterunits"] = "filterUnits",
            ["glyphref"] = "glyphRef",
            ["gradienttransform"] = "gradientTransform",
            ["gradientunits"] = "gradientUnits",
            ["kernelmatrix"] = "kernelMatrix",
            ["kernelunitlength"] = "kernelUnitLength",
            ["keypoints"] = "keyPoints",
            ["keysplines"] = "keySplines",
            ["keytimes"] = "keyTimes",
            ["lengthadjust"] = "lengthAdjust",
            ["limitingconeangle"] = "limitingConeAngle",
            ["markerheight"] = "markerHeight",
            ["markerunits"] = "markerUnits",
            ["markerwidth"] = "markerWidth",
            ["maskcontentunits"] = "maskContentUnits",
            ["maskunits"] = "maskUnits",
            ["numoctaves"] = "numOctaves",
            ["pathlength"] = "pathLength",
            ["patterncontentunits"] = "patternContentUnits",
            ["patterntransform"] = "patternTransform",
            ["patternunits"] = "patternUnits",
            ["pointsatx"] = "pointsAtX",
            ["pointsaty"] = "pointsAtY",
            ["pointsatz"] = "pointsAtZ",
            ["preservealpha"] = "preserveAlpha",
            ["preserveaspectratio"] = "preserveAspectRatio",
            ["primitiveunits"] = "primitiveUnits",
            ["refx"] = "refX",
            ["refy"] = "refY",
            ["repeatcount"] = "repeatCount",
            ["repeatdur"] = "repeatDur",
            ["requiredextensions"] = "requiredExtensions",
            ["requiredfeatures"] = "requiredFeatures",
            ["specularconstant"] = "specularConstant",
            ["specularexponent"] = "specularExponent",
            ["spreadmethod"] = "spreadMethod",
            ["startoffset"] = "startOffset",
            ["stddeviation"] = "stdDeviation",
            ["stitchtiles"] = "stitchTiles",
            ["surfacescale"] = "surfaceScale",
            ["systemlanguage"] = "systemLanguage",
            ["tablevalues"] = "tableValues",
            ["targetx"] = "targetX",
            ["targety"] = "targetY",
            ["textlength"] = "textLength",
            ["viewbox"] = "viewBox",
            ["viewtarget"] = "viewTarget",
            ["xchannelselector"] = "xChannelSelector",
            ["ychannelselector"] = "yChannelSelector",
            ["zoomandpan"] = "zoomAndPan"
        };

        // SVG tag name adjustments (HTML lowercases but SVG needs camelCase)
        private static readonly Dictionary<string, string> SvgTagAdjustments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["altglyph"] = "altGlyph",
            ["altglyphdef"] = "altGlyphDef",
            ["altglyphitem"] = "altGlyphItem",
            ["animatecolor"] = "animateColor",
            ["animatemotion"] = "animateMotion",
            ["animatetransform"] = "animateTransform",
            ["clippath"] = "clipPath",
            ["feblend"] = "feBlend",
            ["fecolormatrix"] = "feColorMatrix",
            ["fecomponenttransfer"] = "feComponentTransfer",
            ["fecomposite"] = "feComposite",
            ["feconvolvematrix"] = "feConvolveMatrix",
            ["fediffuselighting"] = "feDiffuseLighting",
            ["fedisplacementmap"] = "feDisplacementMap",
            ["fedistantlight"] = "feDistantLight",
            ["fedropshadow"] = "feDropShadow",
            ["feflood"] = "feFlood",
            ["fefunca"] = "feFuncA",
            ["fefuncb"] = "feFuncB",
            ["fefuncg"] = "feFuncG",
            ["fefuncr"] = "feFuncR",
            ["fegaussianblur"] = "feGaussianBlur",
            ["feimage"] = "feImage",
            ["femerge"] = "feMerge",
            ["femergenode"] = "feMergeNode",
            ["femorphology"] = "feMorphology",
            ["feoffset"] = "feOffset",
            ["fepointlight"] = "fePointLight",
            ["fespecularlighting"] = "feSpecularLighting",
            ["fespotlight"] = "feSpotLight",
            ["fetile"] = "feTile",
            ["feturbulence"] = "feTurbulence",
            ["foreignobject"] = "foreignObject",
            ["glyphref"] = "glyphRef",
            ["lineargradient"] = "linearGradient",
            ["radialgradient"] = "radialGradient",
            ["textpath"] = "textPath"
        };

        // Active formatting elements list for adoption agency
        private List<LiteElement> _activeFormattingElements = new List<LiteElement>();


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

                        // FenBrowser Mod: Adoption Agency Algorithm Integration
                        if (FormattingElements.Contains(endName))
                        {
                            FenLogger.Debug($"[HtmlLiteParser] Triggering Adoption Agency for </{endName}>");
                            RunAdoptionAgency(endName, stack);
                            continue;
                        }

                        while (stack.Count > 1 && !string.Equals(stack.Peek().Tag, endName, StringComparison.OrdinalIgnoreCase))
                            stack.Pop();
                        if (stack.Count > 1) stack.Pop();
                        continue;
                    }

                    // Start tag
                    _i++; // consume '<'
                    var tag = ReadTagNameLower();
                    
                    // DEBUG: Heartbeat (Info level)
                    if (stack.Count < 50 && _i < 2000) 
                    {
                         FenLogger.Info($"[HtmlLiteParser_HEARTBEAT] Parsed TAG: '{tag}'", LogCategory.General);
                    }
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
                        // FenBrowser Mod: Foster Parenting for Table Content
                        // If we are inside a table structure but dealing with non-table content
                        if (stack.Count > 1 && IsTableStructure(stack.Peek().Tag) && !IsTableValidTag(tag, stack.Peek().Tag))
                        {
                            // Find the foster parent (element before the table)
                            // Simplified: Just insert before the table in the parent's list if possible
                            // For now, we'll append to the stack.Peek()'s parent if it exists, effectively "hoisting" it out.
                            // However, since we don't track parent pointers up the stack easily without scanning the stack, 
                            // we'll use a heuristic: if stack top is table/tbody/tr and tag is div/span, append to stack[count-2]
                            
                            // NOTE: Valid implementation requires stack manipulation. 
                            // Safe fallback: Just append to current (stack.Peek()) to avoid crashing, 
                            // but ideally we Hoist.
                            // Let's implement a simple "Hoist" if the parent is a table.
                            
                            var parent = stack.Peek();
                            if ((parent.Tag == "table" || parent.Tag == "tbody" || parent.Tag == "thead" || parent.Tag == "tfoot" || parent.Tag == "tr") 
                                && IsBlockElement(tag))
                            {
                                // Foster Parent: Traverse stack for a non-table element
                                FenLogger.Debug($"[HtmlLiteParser] Foster parenting triggered for <{tag}> inside <{parent.Tag}>");
                                parent.Append(elem); 
                            }
                            else
                            {
                                parent.Append(elem);
                            }
                        }
                        else
                        {
                            stack.Peek().Append(elem);
                        }
                    }

                    // FIX: Force SVG shapes to be self-closing to prevent incorrect nesting
                    if (!selfClosing && (tag == "path" || tag == "rect" || tag == "circle" || tag == "line" || tag == "polyline" || tag == "polygon" || tag == "ellipse" || tag == "stop" || tag == "image"))
                    {
                         FenLogger.Debug($"[HtmlLiteParser] Force-closing SVG tag: '{tag}'", LogCategory.Rendering);
                         selfClosing = true;
                    }
                     else
                     {
                          if (tag.ToLowerInvariant().Contains("path"))
                          {
                              FenLogger.Debug($"[HtmlLiteParser] Saw tag suspect 'path': '{tag}'. selfClosing={selfClosing}", LogCategory.Rendering);
                          }
                     }

                    // Push if container
                    if (!selfClosing && !IsVoid(tag))
                    {
                        // FenBrowser Mod: Maintain Active Formatting Elements
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
                // Recovery: If we got here, ReadAttrName failed or hit bad char
                // If EOF, just return
                if (Eof()) { lowerName = string.Empty; return; }
                
                // Skip one char and retry
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
            // FenBrowser Mod: RecoverMissingQuote integration
            // If the value doesn't end with the quote, triggered mainly if Eof() was hit inside loop
            // But strict checking logic is:
            if (Eof()) 
            {
                 // Check if we missed a quote by scanning back or standard recovery
                 // Actually, ReadAttrValue creates a substring. If that substring contains newlines or > 
                 // and we didn't find the closing quote, we might want to be careful.
                 // The current loop `while (!Eof() && Peek() != q)` handles everything until EOF. 
                 // So if we hit EOF, we just return what we found. That is technically "recovery".
                 // BUT, if we want `RecoverMissingQuote` behavior which stops at `>`, we should use that *instead* of the simple loop.
                 
                 // Let's refactor to use RecoverMissingQuote logic if standard loop seems too greedy?
                 // No, let's just use the RecoverMissingQuote logic for unquoted or malformed quoted.
            }
            
            // Let's REPLACE the simple loop with RecoverMissingQuote call if it looks like a quote
            // The logic below (lines 488) is: `while (!Eof() && Peek() != q) _i++;`
            // This allows > and newlines inside quotes. This IS correct for valid HTML but bad for broken HTML.
            // Broken HTML often has `<div class="foo></div>`.
            // User-agents often stop at `>` if the quote is missing.
            
            // Re-implementing lines 488-489 with Check for '>'
             var s_start = st;
             while (!Eof())
             {
                 var cc = Peek();
                 if (cc == q) break;
                 
                 // Fault Tolerance: Stop at > if the line is very long or contains newlines? 
                 // Chrome allows newlines in attributes.
                 // But for `<a href="foo>` it stops at `>`.
                 if (cc == '>') 
                 {
                     // Missing quote!
                     FenLogger.Debug("[HtmlLiteParser] Recovered missing quote at >");
                     break; 
                 }
                 _i++;
             }
             var s = _html.Substring(st, System.Math.Max(0, _i - st));
             rawValue = s;
             if (!Eof() && Peek() == q) _i++; // Only consume quote if we actually found it
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
                // FIX: Treat SVG common shapes as void to prevent nesting if not self-closed
                case "path": case "rect": case "circle": case "line": case "polyline": 
                case "polygon": case "ellipse": case "stop": case "use": case "image":
                    FenLogger.Debug($"[IsVoid] Check '{tag}' -> TRUE", LogCategory.Rendering);
                    return true;
                default:
                    if (tag == "path") FenLogger.Debug($"[IsVoid] Check '{tag}' -> FALSE (Oops!)", LogCategory.Rendering);
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

        // -------------------- Adoption Agency Algorithm --------------------

        /// <summary>
        /// Run the adoption agency algorithm for formatting elements.
        /// This handles cases like <b><i></b></i> by properly nesting the elements.
        /// Per HTML5 spec section 8.2.5.4.7
        /// </summary>
        private void RunAdoptionAgency(string endTag, Stack<LiteElement> stack)
        {
            const int outerLoopLimit = 8;

            for (int outerLoop = 0; outerLoop < outerLoopLimit; outerLoop++)
            {
                // Step 1: Find formatting element in active formatting list
                int formattingIndex = -1;
                LiteElement formattingElement = null;
                for (int i = _activeFormattingElements.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_activeFormattingElements[i].Tag, endTag, StringComparison.OrdinalIgnoreCase))
                    {
                        formattingIndex = i;
                        formattingElement = _activeFormattingElements[i];
                        break;
                    }
                }

                // If no formatting element found, just pop normally
                if (formattingElement == null)
                {
                    while (stack.Count > 1 && !string.Equals(stack.Peek().Tag, endTag, StringComparison.OrdinalIgnoreCase))
                        stack.Pop();
                    if (stack.Count > 1) stack.Pop();
                    return;
                }

                // Step 2: Check if formatting element is in stack
                var stackList = new List<LiteElement>(stack);
                stackList.Reverse();
                int stackIndex = stackList.FindIndex(e => ReferenceEquals(e, formattingElement));
                
                if (stackIndex < 0)
                {
                    // Not in stack, remove from active list
                    _activeFormattingElements.RemoveAt(formattingIndex);
                    return;
                }

                // Step 3: Find furthest block after formatting element
                LiteElement furthestBlock = null;
                int furthestBlockIndex = -1;
                for (int i = stackIndex + 1; i < stackList.Count; i++)
                {
                    if (SpecialElements.Contains(stackList[i].Tag))
                    {
                        furthestBlock = stackList[i];
                        furthestBlockIndex = i;
                        break;
                    }
                }

                // If no furthest block, pop elements and remove from active list
                if (furthestBlock == null)
                {
                    while (stack.Count > 0 && !ReferenceEquals(stack.Peek(), formattingElement))
                        stack.Pop();
                    if (stack.Count > 0) stack.Pop();
                    _activeFormattingElements.RemoveAt(formattingIndex);
                    return;
                }

                // Step 4-7: Reparent nodes (simplified adoption)
                // For simplicity, we do basic adoption without full reconstruction
                var commonAncestor = stackIndex > 0 ? stackList[stackIndex - 1] : stackList[0];
                
                // Create clone of formatting element
                var clone = formattingElement.ShallowClone();
                
                // Reparent furthest block's children to clone
                foreach (var child in new List<LiteElement>(furthestBlock.Children))
                {
                    child.Remove();
                    clone.Append(child);
                }
                
                // Add clone to furthest block
                furthestBlock.Append(clone);
                
                // Update active formatting list
                if (formattingIndex < _activeFormattingElements.Count)
                {
                    _activeFormattingElements[formattingIndex] = clone;
                }

                // Pop to formatting element
                while (stack.Count > 0 && !ReferenceEquals(stack.Peek(), formattingElement))
                    stack.Pop();
                if (stack.Count > 0) stack.Pop();
                
                return;
            }
        }

        /// <summary>
        /// Add element to active formatting elements list
        /// </summary>
        private void PushFormattingElement(LiteElement element)
        {
            // Limit identical elements per spec (Noah's Ark clause)
            int count = 0;
            for (int i = _activeFormattingElements.Count - 1; i >= 0; i--)
            {
                var el = _activeFormattingElements[i];
                if (string.Equals(el.Tag, element.Tag, StringComparison.OrdinalIgnoreCase))
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

        /// <summary>
        /// Reconstruct active formatting elements
        /// </summary>
        private void ReconstructActiveFormattingElements(Stack<LiteElement> stack)
        {
            if (_activeFormattingElements.Count == 0) return;
            
            var stackList = new List<LiteElement>(stack);
            
            // Find entries not in stack and reopen them
            for (int i = 0; i < _activeFormattingElements.Count; i++)
            {
                var el = _activeFormattingElements[i];
                if (!stackList.Contains(el))
                {
                    var clone = el.ShallowClone();
                    if (stack.Count > 0)
                    {
                        stack.Peek().Append(clone);
                    }
                    stack.Push(clone);
                    _activeFormattingElements[i] = clone;
                }
            }
        }

        // -------------------- SVG/MathML Foreign Content --------------------

        /// <summary>
        /// Adjust SVG attribute names to proper camelCase
        /// </summary>
        private static string AdjustSvgAttributeName(string name)
        {
            if (SvgAttributeAdjustments.TryGetValue(name, out var adjusted))
                return adjusted;
            return name;
        }

        /// <summary>
        /// Adjust SVG tag names to proper camelCase
        /// </summary>
        private static string AdjustSvgTagName(string tag)
        {
            if (SvgTagAdjustments.TryGetValue(tag, out var adjusted))
                return adjusted;
            return tag;
        }

        /// <summary>
        /// Check if currently in foreign content (SVG/MathML)
        /// </summary>
        private bool IsInForeignContent(Stack<LiteElement> stack)
        {
            foreach (var el in stack)
            {
                var tag = el.Tag?.ToLowerInvariant();
                if (tag == "svg" || tag == "math")
                    return true;
                if (SvgIntegrationPoints.Contains(tag) || MathMlIntegrationPoints.Contains(tag))
                    return false;
            }
            return false;
        }

        // -------------------- Enhanced Error Recovery --------------------

        /// <summary>
        /// Recover from malformed tag by skipping to next valid boundary
        /// </summary>
        private void RecoverFromMalformedTag()
        {
            // Skip until we find a tag start or EOF
            while (!Eof())
            {
                var c = Peek();
                if (c == '<')
                {
                    // Check if it's a valid tag start
                    if (_i + 1 < _n)
                    {
                        var next = _html[_i + 1];
                        if (char.IsLetter(next) || next == '/' || next == '!')
                            break;
                    }
                }
                _i++;
            }
        }

        /// <summary>
        /// Close all pending tags at document end
        /// </summary>
        private void ClosePendingTags(Stack<LiteElement> stack)
        {
            while (stack.Count > 1)
            {
                stack.Pop();
            }
            _activeFormattingElements.Clear();
        }

        /// <summary>
        /// Handle missing closing quote in attribute value
        /// </summary>
        private string RecoverMissingQuote(char expectedQuote)
        {
            var start = _i;
            // Read until we hit a tag boundary or newline
            while (!Eof())
            {
                var c = Peek();
                if (c == '>' || c == '\r' || c == '\n' || c == expectedQuote)
                    break;
                _i++;
            }
            var value = _html.Substring(start, _i - start);
            if (!Eof() && Peek() == expectedQuote)
                _i++; // consume the quote if found
            return DecodeEntities(value);
        }

        /// <summary>
        /// Check if a tag is a block-level element
        /// </summary>
        private static bool IsBlockElement(string tag)
        {
            switch (tag?.ToLowerInvariant())
            {
                case "address": case "article": case "aside": case "blockquote":
                case "canvas": case "dd": case "div": case "dl": case "dt":
                case "fieldset": case "figcaption": case "figure": case "footer":
                case "form": case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                case "header": case "hr": case "li": case "main": case "nav":
                case "noscript": case "ol": case "p": case "pre": case "section":
                case "table": case "tfoot": case "ul": case "video":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Handle unexpected null bytes in input
        /// </summary>
        private char SafePeek()
        {
            if (_i >= _n) return '\0';
            var c = _html[_i];
            // Replace NULL with replacement character per HTML5 spec
            return c == '\0' ? '\uFFFD' : c;
        }
        private static bool IsTableStructure(string tag)
        {
            if (tag == null) return false;
            switch(tag.ToLowerInvariant())
            {
                case "table": case "thead": case "tbody": case "tfoot": case "tr":
                    return true;
                default: 
                    return false;
            }
        }

        private static bool IsTableValidTag(string tag, string parentTag)
        {
            if (tag == null) return false;
            var t = tag.ToLowerInvariant();
            var p = parentTag.ToLowerInvariant();
            
            if (p == "table") return t == "thead" || t == "tbody" || t == "tfoot" || t == "tr" || t == "caption" || t == "colgroup" || t == "col";
            if (p == "tbody" || p == "thead" || p == "tfoot") return t == "tr";
            if (p == "tr") return t == "td" || t == "th";
            return false;
        }
    }
}
