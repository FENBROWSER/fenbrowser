using System.Collections.Generic;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// Hand-written, sandboxed parser for the XML subset required by standalone SVG.
    ///
    /// Security contract (all enforced DURING the single scan, never afterwards):
    /// 1. DOCTYPE declarations are rejected outright. The parser implements no
    ///    custom-entity machinery at all, which makes entity-expansion attacks
    ///    (billion laughs / XXE) structurally impossible rather than merely limited.
    /// 2. Entities resolve through a fixed whitelist plus numeric character
    ///    references whose values are range-checked to valid Unicode scalars.
    ///    Anything malformed degrades to literal text or U+FFFD - it never throws.
    /// 3. Element count, nesting depth, attributes-per-element, attribute-value
    ///    length and id length budgets are checked while building the tree.
    /// 4. The tree is built with an EXPLICIT STACK (no recursion), so nesting
    ///    attacks cannot overflow the process stack regardless of caller limits.
    /// 5. Malformed input recovers browser-style (first attribute wins, unmatched
    ///    close tags ignored, EOF truncates) and records warnings instead of
    ///    failing unless a hard budget was exceeded.
    /// </summary>
    internal static class SvgMarkupParser
    {
        // Independent hard ceiling regardless of caller-supplied limits.
        internal const int HardMaxDepth = 512;
        internal const int MaxAttributesPerElement = 256;
        internal const int MaxAttributeValueChars = 256 * 1024;
        internal const int MaxIdChars = 512;

        // Subtrees consumed raw and discarded: no rendering use in v1 and keeping
        // their content out of the tree removes CSS/script parsing surface.
        private static readonly HashSet<string> IgnoredSubtrees = new HashSet<string>
        {
            "style", "script", "title", "desc", "metadata"
        };

        public static bool TryParse(
            string source,
            SvgRenderLimits limits,
            out SvgParsedDocument document,
            out string fatalReason)
        {
            document = null;
            fatalReason = null;

            // One report instance is shared by the scan state and the resulting
            // document, so warnings/flags recorded while parsing are visible to
            // consumers (they previously vanished into a throwaway copy).
            var documentReport = new SvgParseReport();
            var state = new ParseState(source, documentReport);
            int effectiveDepthLimit = System.Math.Min(
                limits.MaxRecursionDepth > 0 ? limits.MaxRecursionDepth : int.MaxValue,
                HardMaxDepth);

            // --- Prolog + root discovery: scan forward through junk (stray
            // text, comments, PIs, unmatched close tags) until a real start
            // tag appears. Browser-style recovery, bounded by input length. ---
            SvgElement root = null;
            while (!state.Eof)
            {
                SkipWhitespace(state);
                if (state.Eof)
                {
                    break;
                }

                if (!state.Match('<'))
                {
                    // Stray character before the root: skip it.
                    state.Pos++;
                    continue;
                }

                if (state.Peek() == '?' || state.Peek() == '!')
                {
                    if (PeekWordAfterLtBang(state, "DOCTYPE"))
                    {
                        state.Report.SawDoctype = true;
                        fatalReason = "DOCTYPE declarations are rejected by the SVG sandbox";
                        return false;
                    }
                    SkipBangOrPi(state);
                    continue;
                }

                if (state.Peek() == '/')
                {
                    // Unmatched close tag before any root: skip past it.
                    state.Pos++;
                    while (!state.Eof && IsNameChar(state.Peek()))
                    {
                        state.Pos++;
                    }
                    SkipWhitespace(state);
                    state.Match('>');
                    continue;
                }

                var candidate = TryReadStartTag(state, allowEmptyName: true);
                if (candidate != null)
                {
                    root = candidate;
                    break;
                }
                // Invalid start-tag attempt: cursor advanced; keep scanning.
            }

            if (root == null)
            {
                fatalReason = "SVG has no root element";
                return false;
            }
            if (!string.Equals(root.Name, "svg", System.StringComparison.Ordinal))
            {
                fatalReason = $"SVG root element must be 'svg', got '{root.Name}'";
                return false;
            }

            var doc = new SvgParsedDocument(documentReport) { Root = root };
            doc.ElementsById = new Dictionary<string, SvgElement>(System.StringComparer.Ordinal);
            RegisterElement(doc, root);

            var openStack = new List<SvgElement> { root };
            state.Report.ElementCount = 1;
            EnforceElementBudget(state, limits);

            // --- Body: iterative descent. ---
            while (!state.Eof && openStack.Count > 0)
            {
                SkipContentNoise(state);

                if (state.Eof)
                {
                    break;
                }

                if (state.Match('/'))
                {
                    // Close tag: "</" was detected by SkipContentNoise consuming '<'.
                    ReadCloseTag(state, openStack);
                    continue;
                }

                if (state.Peek() == '!')
                {
                    if (PeekWordAfterLtBang(state, "DOCTYPE"))
                    {
                        state.Report.SawDoctype = true;
                        fatalReason = "DOCTYPE declarations are rejected by the SVG sandbox";
                        return false;
                    }
                    SkipBangOrPi(state);
                    continue;
                }

                if (state.Peek() == '?')
                {
                    SkipBangOrPi(state);
                    continue;
                }

                var child = TryReadStartTag(state, allowEmptyName: true);
                if (child == null)
                {
                    continue; // Recovered as text/noise; position advanced.
                }

                state.Report.ElementCount++;
                EnforceElementBudget(state, limits);
                CountFilters(state, child, limits);
                RegisterElement(doc, child);

                if (IgnoredSubtrees.Contains(child.Name))
                {
                    SkipIgnoredSubtree(state, child.Name);
                    openStack[openStack.Count - 1].Children.Add(child);
                    continue;
                }

                if (child.IsSelfClosing)
                {
                    openStack[openStack.Count - 1].Children.Add(child);
                    continue;
                }

                int depth = openStack.Count + 1;
                if (depth > effectiveDepthLimit)
                {
                    fatalReason =
                        $"SVG nesting depth ({depth}) exceeds limit ({effectiveDepthLimit})";
                    return false;
                }

                openStack[openStack.Count - 1].Children.Add(child);
                openStack.Add(child);
            }

            document = doc;
            return true;
        }

        // ---------------------------------------------------------------- state

        private sealed class ParseState
        {
            public readonly string Source;
            public int Pos;
            public readonly SvgParseReport Report;
            public int FilterCount;

            public ParseState(string source, SvgParseReport report)
            {
                Source = source ?? string.Empty;
                Report = report;
            }

            public bool Eof => Pos >= Source.Length;

            public char Peek() => Pos < Source.Length ? Source[Pos] : '\0';

            public bool Match(char c)
            {
                if (Pos < Source.Length && Source[Pos] == c)
                {
                    Pos++;
                    return true;
                }
                return false;
            }

            public bool MatchTwo(char c)
            {
                if (Pos + 1 < Source.Length && Source[Pos] == c && Source[Pos + 1] == c)
                {
                    Pos += 2;
                    return true;
                }
                return false;
            }
        }

        private static void EnforceElementBudget(ParseState state, SvgRenderLimits limits)
        {
            if (state.Report.ElementCount > limits.MaxElementCount)
            {
                throw new SvgSandboxViolationException(
                    $"SVG element count ({state.Report.ElementCount}) exceeds limit ({limits.MaxElementCount})");
            }

            // Filter counting mirrors the legacy pre-scan so limit messages stay
            // byte-compatible during the migration window.
        }

        // ------------------------------------------------------------- elements

        private static void CountFilters(ParseState state, SvgElement element, SvgRenderLimits limits)
        {
            if (string.Equals(element.Name, "filter", System.StringComparison.Ordinal))
            {
                state.FilterCount++;
                if (state.FilterCount > limits.MaxFilterCount)
                {
                    throw new SvgSandboxViolationException(
                        $"SVG filter count ({state.FilterCount}) exceeds limit ({limits.MaxFilterCount})");
                }
            }
        }

        private static SvgElement TryReadStartTag(ParseState state, bool allowEmptyName)
        {
            // Caller guarantees the cursor sits AFTER an already-consumed '<'
            // (both prolog and body paths consume it before calling).
            SkipWhitespace(state);

            int nameStart = state.Pos;
            if (!IsNameStartChar(state.Peek()))
            {
                // Not markup - recover by treating '<' as literal text.
                if (!allowEmptyName)
                {
                    // Prolog path: garbage before root.
                    SkipUntil(state, '>');
                }
                else
                {
                    state.Pos = System.Math.Min(nameStart + 1, state.Source.Length);
                }
                return null;
            }

            state.Pos++;
            while (!state.Eof && IsNameChar(state.Peek()))
            {
                state.Pos++;
            }
            string name = state.Source.Substring(nameStart, state.Pos - nameStart);

            var element = new SvgElement { Name = name };
            var attributes = new List<KeyValuePair<string, string>>();
            var seenNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            bool closed = false;

            while (!state.Eof)
            {
                SkipWhitespace(state);
                char c = state.Peek();

                if (c == '>')
                {
                    state.Pos++;
                    closed = true;
                    break;
                }

                if (c == '/')
                {
                    state.Pos++;
                    if (state.Match('>'))
                    {
                        element.IsSelfClosing = true;
                        closed = true;
                        break;
                    }
                    continue; // Stray '/': ignore.
                }

                if (c == '\0')
                {
                    break; // EOF mid-tag.
                }

                ReadAttribute(state, element, attributes, seenNames);
            }

            if (!closed)
            {
                // EOF before the tag terminated: the element is incomplete and
                // is DROPPED entirely (browser-style recovery), never added.
                return null;
            }

            element.Attributes = attributes.ToArray();
            return element;
        }

        private static void ReadAttribute(
            ParseState state,
            SvgElement element,
            List<KeyValuePair<string, string>> attributes,
            HashSet<string> seenNames)
        {
            int nameStart = state.Pos;
            while (!state.Eof)
            {
                char c = state.Peek();
                if (char.IsWhiteSpace(c) || c == '=' || c == '>' || c == '/' || c == '"')
                {
                    break;
                }
                state.Pos++;
            }

            if (state.Pos == nameStart)
            {
                // No consumable name (e.g. '=' with no name): skip one char to
                // guarantee progress.
                state.Pos++;
                return;
            }

            string attrName = state.Source.Substring(nameStart, state.Pos - nameStart);

            SkipWhitespace(state);
            string value = string.Empty;
            if (state.Match('='))
            {
                SkipWhitespace(state);
                char q = state.Peek();
                if (q == '"' || q == '\'')
                {
                    state.Pos++;
                    value = ReadQuotedValue(state, q);
                }
                else
                {
                    value = ReadUnquotedValue(state);
                }
            }

            if (attributes.Count >= MaxAttributesPerElement)
            {
                state.Report.Warn("attribute-per-element budget exceeded; extras dropped");
                return;
            }
            if (value.Length > MaxAttributeValueChars)
            {
                value = value.Substring(0, MaxAttributeValueChars);
                state.Report.Warn("attribute value truncated over length budget");
            }

            if (seenNames.Add(attrName))
            {
                attributes.Add(new KeyValuePair<string, string>(attrName, value));
                if (string.Equals(attrName, "id", System.StringComparison.OrdinalIgnoreCase))
                {
                    // Gate the id VALUE length (F7): oversized ids are ignored as
                    // keys and never echoed into warnings beyond a short prefix.
                    if (value.Length <= MaxIdChars)
                    {
                        element.IdAttribute = value;
                    }
                    else
                    {
                        state.Report.Warn("oversized id value ignored (length budget)");
                    }
                }
            }
            else
            {
                state.Report.SawDuplicateAttribute = true;
                state.Report.Warn($"duplicate attribute '{attrName}' ignored (first wins)");
            }
        }

        private static string ReadQuotedValue(ParseState state, char quote)
        {
            // Fast path: no entities - slice directly.
            int start = state.Pos;
            int i = state.Pos;
            string s = state.Source;
            while (i < s.Length && s[i] != quote)
            {
                i++;
            }

            if (i >= s.Length)
            {
                state.Pos = i;
                return s.Substring(start); // Unterminated quote: take rest.
            }

            int segmentLength = i - start;
            bool hasEntity = segmentLength >= 1 && s.IndexOf('&', start, segmentLength) >= 0;
            if (!hasEntity)
            {
                state.Pos = i + 1;
                return s.Substring(start, segmentLength);
            }

            // Slow path: expand entities character by character.
            var sb = new System.Text.StringBuilder(segmentLength);
            state.Pos = start;
            while (!state.Eof && state.Peek() != quote)
            {
                if (state.Peek() == '&')
                {
                    ExpandEntity(state, sb);
                }
                else
                {
                    sb.Append(state.Source[state.Pos]);
                    state.Pos++;
                }
            }
            state.Pos++; // Closing quote (or EOF-safe increment past end).
            return sb.ToString();
        }

        private static string ReadUnquotedValue(ParseState state)
        {
            int start = state.Pos;
            while (!state.Eof)
            {
                char c = state.Peek();
                if (char.IsWhiteSpace(c) || c == '>')
                {
                    break;
                }
                state.Pos++;
            }
            return state.Source.Substring(start, state.Pos - start);
        }

        private static void ReadCloseTag(ParseState state, List<SvgElement> openStack)
        {
            // Current char is the one after "</".
            int nameStart = state.Pos;
            while (!state.Eof && IsNameChar(state.Peek()))
            {
                state.Pos++;
            }
            string name = state.Source.Substring(nameStart, state.Pos - nameStart);
            SkipWhitespace(state);
            state.Match('>');
            state.Match('/'); // Tolerate "</foo/>".

            if (name.Length == 0)
            {
                return; // "</>" - ignore.
            }

            for (int i = openStack.Count - 1; i >= 0; i--)
            {
                if (string.Equals(openStack[i].Name, name, System.StringComparison.Ordinal))
                {
                    openStack.RemoveRange(i, openStack.Count - i);
                    return;
                }
            }
            // Unmatched close tag: ignore (browser-style recovery).
        }

        // -------------------------------------------------------- raw skipping

        private static void SkipIgnoredSubtree(ParseState state, string name)
        {
            // Consume everything until the matching close tag of `name`,
            // honoring nested same-name opens. Content is never interpreted.
            int depth = 1;
            while (!state.Eof)
            {
                char c = state.Peek();
                if (c != '<')
                {
                    state.Pos++;
                    continue;
                }

                // Close candidate: "</name".
                state.Pos++; // consume '<'
                if (state.Peek() == '/')
                {
                    state.Pos++; // skip '/'
                    int nameStart = state.Pos;
                    while (!state.Eof && IsNameChar(state.Peek()))
                    {
                        state.Pos++;
                    }
                    string closed = state.Source.Substring(nameStart, state.Pos - nameStart);
                    SkipWhitespace(state);
                    state.Match('>');
                    if (string.Equals(closed, name, System.StringComparison.Ordinal))
                    {
                        depth--;
                        if (depth == 0)
                        {
                            return;
                        }
                    }
                    continue;
                }

                // Open candidate: cursor already sits after '<' (consumed above).
                if (IsNameStartChar(state.Peek()))
                {
                    int nameStart = state.Pos;
                    while (!state.Eof && IsNameChar(state.Peek()))
                    {
                        state.Pos++;
                    }
                    string opened = state.Source.Substring(nameStart, state.Pos - nameStart);
                    bool selfClosing = ScanPastTagEnd(state);
                    if (!selfClosing && string.Equals(opened, name, System.StringComparison.Ordinal))
                    {
                        depth++;
                    }
                    continue;
                }

                if (state.Peek() == '!' )
                {
                    SkipBangOrPi(state);
                    continue;
                }
                if (state.Peek() == '?')
                {
                    SkipBangOrPi(state);
                    continue;
                }

                // Literal '<': already advanced.
            }
        }

        /// <summary>Consumes attributes up to and including '>' or "/>"; returns true if self-closing.</summary>
        private static bool ScanPastTagEnd(ParseState state)
        {
            while (!state.Eof)
            {
                char c = state.Peek();
                if (c == '>')
                {
                    state.Pos++;
                    return false;
                }
                if (c == '"')
                {
                    state.Pos++;
                    while (!state.Eof && state.Peek() != '"')
                    {
                        state.Pos++;
                    }
                    state.Pos++; // closing quote or EOF
                    continue;
                }
                if (c == '\'')
                {
                    state.Pos++;
                    while (!state.Eof && state.Peek() != '\'')
                    {
                        state.Pos++;
                    }
                    state.Pos++;
                    continue;
                }
                if (c == '/')
                {
                    state.Pos++;
                    if (state.Match('>'))
                    {
                        return true;
                    }
                    continue;
                }
                state.Pos++;
            }
            return false;
        }

        private static void SkipContentNoise(ParseState state)
        {
            // Advances past text content and the leading '<' of the next markup,
            // leaving the cursor on the character AFTER '<'.
            while (!state.Eof)
            {
                char c = state.Peek();
                if (c == '<')
                {
                    state.Pos++;
                    if (!state.Eof && state.Peek() == '/')
                    {
                        state.Pos++;
                        // Mark close-tag by leaving '/' visible to caller loop.
                        // We rewound semantics: caller sees '/' as Peek().
                        state.Pos--;
                    }
                    return;
                }
                state.Pos++; // Text content: discarded (v1 renders no text).
            }
        }

        private static void SkipBangOrPi(ParseState state)
        {
            // Cursor is after '<'. Handles <!-- -->, <![CDATA[ ]]>, <!...>, <?...?>.
            if (state.Eof)
            {
                return;
            }

            if (state.Peek() == '?')
            {
                SkipUntil(state, '>');
                return;
            }

            if (state.Peek() != '!')
            {
                return;
            }

            state.Pos++; // '!'

            if (state.MatchTwo('-'))
            {
                // Comment: consume through "-->" tolerating EOF.
                while (!state.Eof)
                {
                    if (state.Peek() == '-' &&
                        state.Pos + 1 < state.Source.Length &&
                        state.Source[state.Pos + 1] == '-')
                    {
                        state.Pos += 2; // consume "--"
                        state.Match('>'); // consume optional '>'
                        return;
                    }
                    state.Pos++;
                }
                return;
            }

            const string cdataOpen = "[CDATA[";
            if (PosMatches(state, cdataOpen))
            {
                state.Pos += cdataOpen.Length;
                const string cdataClose = "]]>";
                int idx = state.Source.IndexOf(cdataClose, state.Pos, System.StringComparison.Ordinal);
                state.Pos = idx < 0 ? state.Source.Length : idx + cdataClose.Length;
                return;
            }

            // Any other <!...>: consume to '>'.
            SkipUntil(state, '>');
        }

        private static bool PosMatches(ParseState state, string word)
        {
            if (state.Pos + word.Length > state.Source.Length)
            {
                return false;
            }
            return string.CompareOrdinal(state.Source, state.Pos, word, 0, word.Length) == 0;
        }

        private static void SkipUntil(ParseState state, char terminator, char secondTerminator = '\0')
        {
            while (!state.Eof)
            {
                char c = state.Peek();
                if (c == terminator || (secondTerminator != '\0' && c == secondTerminator))
                {
                    state.Pos++;
                    return;
                }
                state.Pos++;
            }
        }

        private static bool PeekWord(ParseState state, string word)
        {
            if (state.Pos + word.Length > state.Source.Length)
            {
                return false;
            }
            return string.CompareOrdinal(state.Source, state.Pos, word, 0, word.Length) == 0;
        }

        private static bool PeekWordAfterLtBang(ParseState state, string word)
        {
            // Cursor is after '<'; verify "!WORD". Case-insensitive so lowercase
            // <!doctype> cannot bypass the DOCTYPE policy (F6).
            if (state.Pos + 1 + word.Length > state.Source.Length)
            {
                return false;
            }
            if (state.Source[state.Pos] != '!')
            {
                return false;
            }
            return string.Compare(
                state.Source,
                state.Pos + 1,
                word,
                0,
                word.Length,
                System.StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static void SkipWhitespace(ParseState state)
        {
            while (!state.Eof && char.IsWhiteSpace(state.Source[state.Pos]))
            {
                state.Pos++;
            }
        }

        // -------------------------------------------------------------- entities

        private static void ExpandEntity(ParseState state, System.Text.StringBuilder sb)
        {
            // Cursor at '&'.
            string s = state.Source;
            int ampPos = state.Pos;

            const int maxEntityName = 32;
            int semi = s.IndexOf(';', ampPos, System.Math.Min(maxEntityName + 1, s.Length - ampPos));
            if (semi < 0)
            {
                sb.Append('&');
                state.Pos = ampPos + 1;
                return;
            }

            int bodyStart = ampPos + 1;
            int bodyLen = semi - bodyStart;
            if (bodyLen == 0 || bodyLen > maxEntityName)
            {
                sb.Append('&');
                state.Pos = ampPos + 1;
                return;
            }

            switch (bodyLen)
            {
                case 3:
                    if (s[bodyStart] == 'a' && s[bodyStart + 1] == 'm' && s[bodyStart + 2] == 'p')
                    { sb.Append('&'); state.Pos = semi + 1; return; }
                    if (s[bodyStart] == 'l' && s[bodyStart + 1] == 't' )
                    { sb.Append('<'); state.Pos = semi + 1; return; }
                    break;
                case 2:
                    if (s[bodyStart] == 'g' && s[bodyStart + 1] == 't')
                    { sb.Append('>'); state.Pos = semi + 1; return; }
                    break;
                case 4:
                    if (s[bodyStart] == 'q' && s[bodyStart + 1] == 'u' && s[bodyStart + 2] == 'o' && s[bodyStart + 3] == 't')
                    { sb.Append('"'); state.Pos = semi + 1; return; }
                    break;
                case 5:
                    if (s[bodyStart] == 'a' && s[bodyStart + 1] == 'p' && s[bodyStart + 2] == 'o' && s[bodyStart + 3] == 's')
                    { sb.Append('\''); state.Pos = semi + 1; return; }
                    break;
            }

            if (s[bodyStart] == '#')
            {
                if (TryExpandNumericRef(s, bodyStart + 1, semi, sb))
                {
                    state.Pos = semi + 1;
                    return;
                }
                // Malformed numeric ref: emit replacement char (browser-style).
                sb.Append('\uFFFD');
                state.Pos = semi + 1;
                return;
            }

            // Unknown named entity: emit literal text (never fatal).
            sb.Append('&');
            state.Pos = ampPos + 1;
        }

        private static bool TryExpandNumericRef(string s, int start, int semi, System.Text.StringBuilder sb)
        {
            int digitsStart = start;
            bool hex = false;
            if (digitsStart < semi && (s[digitsStart] == 'x' || s[digitsStart] == 'X'))
            {
                hex = true;
                digitsStart++;
            }

            int digitCount = semi - digitsStart;
            if (digitCount <= 0 || digitCount > 8)
            {
                return false;
            }

            long value = 0;
            for (int i = digitsStart; i < semi; i++)
            {
                int d = hex ? HexValue(s[i]) : (s[i] - '0');
                if (d < 0)
                {
                    return false;
                }
                // Guard before multiply: keeps decimal/hex accumulation in range
                // without exceptions and bounds the digit budget implicitly.
                if (value > 0x11000)
                {
                    return false;
                }
                value = hex ? (value << 4) + d : (value * 10) + d;
                if (value > 0x110000)
                {
                    return false;
                }
            }

            if (value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF) || value == 0)
            {
                return false;
            }

            if (value > 0xFFFF)
            {
                sb.Append(char.ConvertFromUtf32((int)value));
            }
            else
            {
                sb.Append((char)value);
            }
            return true;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        // ------------------------------------------------------------ id index

        private static void RegisterElement(SvgParsedDocument doc, SvgElement element)
        {
            string id = element.IdAttribute;
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            if (doc.ElementsById.TryGetValue(id, out _))
            {
                doc.Report.SawDuplicateId = true;
                doc.Report.Warn($"duplicate id '{id}' ignored (first wins)");
                return;
            }

            doc.ElementsById[id] = element;
        }

        // ------------------------------------------------------- char classes

        private static bool IsNameStartChar(char c)
        {
            return char.IsLetter(c) || c == '_' || c == ':';
        }

        private static bool IsNameChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == ':' || c == '-' || c == '.';
        }
    }

    /// <summary>
    /// Thrown ONLY for sandbox budget violations (element/filter/depth/source).
    /// The renderer catches it and converts it to the parity error message.
    /// It is never caused by malformed input - that always recovers.
    /// </summary>
    internal sealed class SvgSandboxViolationException : System.Exception
    {
        public SvgSandboxViolationException(string message) : base(message) { }
    }
}
