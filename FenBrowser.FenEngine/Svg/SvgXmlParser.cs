using System.Collections.Generic;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg
{
    internal static class SvgDiagnosticText
    {
        public const int MaxFatalChars = 256;
        public const int MaxIdentifierChars = 64;

        public static string Bounded(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || maxChars <= 0 || value.Length <= maxChars)
            {
                return value;
            }

            int cut = maxChars;
            if (char.IsHighSurrogate(value[cut - 1]))
            {
                cut--;
            }
            return value.Substring(0, cut);
        }

        public static string Identifier(string value) => Bounded(value, MaxIdentifierChars);
    }

    /// <summary>
    /// Hand-written, sandboxed parser for the XML subset required by standalone SVG.
    ///
    /// Security contract (all enforced DURING the single scan, never afterwards):
    /// 1. A DOCTYPE is honoured only as a bounded internal subset of general
    ///    entities and attribute lists that declare no attribute default value
    ///    (a quoted literal, including a #FIXED default, is rejected, because a
    ///    browser applies it and the engine cannot). External subsets, external
    ///    identifiers, external and parameter entities, conditional sections and
    ///    any undeclared or recursive reference fail the parse closed, so XXE is
    ///    structurally impossible: nothing outside the document is ever read.
    /// 2. Every declared entity is expanded and validated while it is being
    ///    declared, so a declaration that exceeds the depth, size, name or
    ///    expansion-count budget is refused before it can ever be referenced.
    /// 3. Entities resolve through a fixed whitelist plus numeric character
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
        internal const int MaxTextContentChars = 256 * 1024;
        internal const int MaxIdChars = 512;
        internal const long MaxForeignEndTagScanChars = 1024 * 1024;

        internal const int MaxDoctypeChars = 64 * 1024;
        internal const int MaxDoctypeEntities = 128;
        internal const int MaxDoctypeEntityNameChars = 64;
        internal const int MaxDoctypeEntityValueChars = 8 * 1024;
        internal const int MaxDoctypeAttlistChars = 8 * 1024;
        internal const int MaxEntityNameChars = 64;
        internal const int MaxEntityExpansionDepth = 4;
        internal const int MaxEntityExpansionChars = 16 * 1024;
        internal const int MaxEntityExpansionsPerParse = 4096;
        internal const int MaxEntityInjectionChars = 256 * 1024;
        internal const int MaxEntitySplices = 256;
        internal const long MaxEntitySpliceCopyChars = 8L * 1024 * 1024;

        private const int MaxDoctypeLiteralChars = 1024;
        private const string SvgNamespace = "http://www.w3.org/2000/svg";

        // Subtrees consumed raw rather than interpreted as SVG markup. Style text
        // is retained under the same hard text cap for the shared CSS parser;
        // script/title/desc/metadata content remains discarded.
        private static readonly HashSet<string> IgnoredSubtrees = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "style", "script", "h:script", "html:script", "title", "desc", "metadata"
        };

        /// <summary>
        /// Parses <paramref name="source"/> under <paramref name="limits"/>. Never
        /// throws for document content: a sandbox budget violation (element, filter,
        /// depth or source limit) is a rejection like any other fatal error and is
        /// returned as <paramref name="fatalReason"/> with its bare limit message.
        /// </summary>
        public static bool TryParse(
            string source,
            SvgRenderLimits limits,
            out SvgParsedDocument document,
            out string fatalReason)
        {
            try
            {
                return TryParseCore(source, limits, out document, out fatalReason);
            }
            catch (SvgSandboxViolationException ex)
            {
                document = null;
                fatalReason = SvgDiagnosticText.Bounded(ex.Message, SvgDiagnosticText.MaxFatalChars);
                return false;
            }
        }

        private static bool TryParseCore(
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
                        if (!ReadDoctype(state, out string doctypeFatal))
                        {
                            fatalReason = doctypeFatal;
                            return false;
                        }
                        continue;
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
                    if (state.PendingNamespaceFrame != null)
                        state.NamespaceStack.Add(state.PendingNamespaceFrame);
                    break;
                }
                // Invalid start-tag attempt: cursor advanced; keep scanning.
            }

            if (root == null)
            {
                fatalReason = "SVG has no root element";
                return false;
            }
            if (!string.Equals(root.Name, "svg", System.StringComparison.Ordinal) ||
                (!string.IsNullOrEmpty(root.NamespaceUri) &&
                 !string.Equals(root.NamespaceUri, SvgNamespace, System.StringComparison.Ordinal)))
            {
                fatalReason = SvgDiagnosticText.Bounded(
                    $"SVG root element must be 'svg', got '{SvgDiagnosticText.Identifier(root.Name)}'",
                    SvgDiagnosticText.MaxFatalChars);
                return false;
            }

            var doc = new SvgParsedDocument(documentReport) { Root = root };
            doc.ElementsById = new Dictionary<string, SvgElement>(System.StringComparer.Ordinal);
            RegisterElement(doc, root);
            SvgFeatureSupport.Inspect(root, state.Report, limits.TreatScriptsAsInert);

            var openStack = new List<SvgElement> { root };
            state.Report.ElementCount = 1;
            EnforceElementBudget(state, limits);

            // --- Body: iterative descent. ---
            while (!state.Eof && openStack.Count > 0)
            {
                SkipContentNoise(state, openStack[openStack.Count - 1]);

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

                if (state.Peek() == '&' &&
                    state.Dtd != null &&
                    TryExpandContentEntity(state, out string entityFatal))
                {
                    if (entityFatal != null)
                    {
                        fatalReason = entityFatal;
                        return false;
                    }
                    continue;
                }

                if (state.Peek() == '!')
                {
                    if (PeekWordAfterLtBang(state, "DOCTYPE"))
                    {
                        // A declaration in content position is a well-formedness
                        // error, so the bounded-subset path never applies here.
                        state.Report.SawDoctype = true;
                        fatalReason = "DOCTYPE declarations are rejected by the SVG sandbox";
                        return false;
                    }
                    string containerName = openStack[openStack.Count - 1].Name;
                    if (IsCharacterDataContainer(containerName) &&
                        state.Source.AsSpan(state.Pos).StartsWith(
                            "![CDATA[".AsSpan(), System.StringComparison.Ordinal))
                    {
                        state.Report.RequireFallback("CDATA text requires compatibility fallback");
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
                SvgFeatureSupport.Inspect(child, state.Report, limits.TreatScriptsAsInert);

                if (IgnoredSubtrees.Contains(child.Name))
                {
                    if (!child.IsSelfClosing)
                    {
                        SkipIgnoredSubtree(state, child);
                    }
                    AddChild(openStack[openStack.Count - 1], child);
                    continue;
                }

                if (!child.IsSelfClosing &&
                    state.PendingNamespaceFrame != null &&
                    !state.PendingNamespaceFrame.IsSvgNamespace &&
                    !HasForeignEndTag(state, state.PendingNamespaceFrame.RawName))
                {
                    state.Report.Warn(
                        $"foreign element '{SvgDiagnosticText.Identifier(child.Name)}' has no end tag and was not nested");
                    child.UnclosedForeignElement = true;
                    AddChild(openStack[openStack.Count - 1], child);
                    continue;
                }

                if (child.IsSelfClosing)
                {
                    AddChild(openStack[openStack.Count - 1], child);
                    continue;
                }

                int depth = openStack.Count + 1;
                if (depth > effectiveDepthLimit)
                {
                    fatalReason =
                        $"SVG nesting depth ({depth}) exceeds limit ({effectiveDepthLimit})";
                    return false;
                }

                AddChild(openStack[openStack.Count - 1], child);
                openStack.Add(child);
                if (state.PendingNamespaceFrame != null)
                    state.NamespaceStack.Add(state.PendingNamespaceFrame);
            }

            SvgFeatureSupport.InspectHierarchy(root, state.Report);
            document = doc;
            return true;
        }

        // ---------------------------------------------------------------- state

        private sealed class NamespaceFrame
        {
            public readonly Dictionary<string, string> Bindings;
            public readonly bool IsSvgNamespace;
            public readonly string ResolvedName;
            public readonly string NamespaceUri;
            public readonly string RawName;

            public NamespaceFrame(
                Dictionary<string, string> bindings,
                bool isSvgNamespace,
                string resolvedName,
                string namespaceUri,
                string rawName)
            {
                Bindings = bindings;
                IsSvgNamespace = isSvgNamespace;
                ResolvedName = resolvedName;
                NamespaceUri = namespaceUri;
                RawName = rawName;
            }
        }

        private sealed class ParseState
        {
            public string Source;
            public int Pos;
            public readonly SvgParseReport Report;
            public int FilterCount;
            public DtdSubset Dtd;
            public readonly List<NamespaceFrame> NamespaceStack = new List<NamespaceFrame>();
            public NamespaceFrame PendingNamespaceFrame;
            public long ForeignEndTagScanBudget;

            public ParseState(string source, SvgParseReport report)
            {
                Source = source ?? string.Empty;
                Report = report;
                ForeignEndTagScanBudget = MaxForeignEndTagScanChars;
            }

            public bool Eof => Pos >= Source.Length;

            public char Peek() => Pos < Source.Length ? Source[Pos] : '\0';

            public char PeekAt(int offset)
            {
                int index = Pos + offset;
                return index >= 0 && index < Source.Length ? Source[index] : '\0';
            }

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

        // ------------------------------------------------- doctype / dtd subset

        private sealed class SvgDtdEntity
        {
            public readonly string Name;
            public readonly string Value;

            public SvgDtdEntity(string name, string value)
            {
                Name = name;
                Value = value;
            }
        }

        private sealed class DtdSubset
        {
            public readonly Dictionary<string, SvgDtdEntity> Entities =
                new Dictionary<string, SvgDtdEntity>(System.StringComparer.Ordinal);

            public readonly HashSet<string> Active =
                new HashSet<string>(System.StringComparer.Ordinal);

            public int EntityCount;
            public int AttlistCount;
            public int Expansions;
            public int InjectedChars;
            public int Splices;
            public long SpliceCopyChars;
            public bool HasExternalId;
        }

        private static bool RejectDoctype(string detail, out string fatalReason)
        {
            fatalReason = SvgDiagnosticText.Bounded(
                $"DOCTYPE {detail} is rejected by the SVG sandbox",
                SvgDiagnosticText.MaxFatalChars);
            return false;
        }

        private static bool ReadDoctype(ParseState state, out string fatalReason)
        {
            fatalReason = null;
            if (state.Dtd != null)
            {
                return RejectDoctype("declaration", out fatalReason);
            }
            var dtd = new DtdSubset();
            state.Dtd = dtd;

            int start = state.Pos;
            state.Pos += 1 + "DOCTYPE".Length; // cursor sits on '!'
            SkipWhitespace(state);
            if (state.Eof || state.Pos - start > MaxDoctypeChars)
            {
                return RejectDoctype("declaration", out fatalReason);
            }
            if (!ReadName(state, out _))
            {
                return RejectDoctype("declaration", out fatalReason);
            }
            SkipWhitespace(state);

            if (IsDtdKeyword(state, "SYSTEM") || IsDtdKeyword(state, "PUBLIC"))
            {
                if (!ReadExternalId(state, out string externalFatal))
                {
                    return RejectDoctype(externalFatal, out fatalReason);
                }
                dtd.HasExternalId = true;
                SkipWhitespace(state);
            }

            if (!state.Eof && state.Peek() == '[')
            {
                state.Pos++;
                if (!ReadInternalSubset(state, out fatalReason))
                {
                    return false;
                }
                SkipWhitespace(state);
            }
            else
            {
                return RejectDoctype(
                    dtd.HasExternalId
                        ? "external subset"
                        : "declaration without an internal subset",
                    out fatalReason);
            }

            if (state.Eof || state.Peek() != '>' || state.Pos - start > MaxDoctypeChars)
            {
                return RejectDoctype("declaration", out fatalReason);
            }
            state.Pos++;

            if (dtd.EntityCount > 0 || dtd.AttlistCount > 0)
            {
                state.Report.Warn("DOCTYPE internal subset declarations honored (no external resolution)");
            }
            if (dtd.HasExternalId)
            {
                state.Report.Warn("DOCTYPE external identifier ignored; external subsets are never resolved");
            }
            return true;
        }

        private static bool ReadInternalSubset(ParseState state, out string fatalReason)
        {
            fatalReason = null;
            int start = state.Pos;
            while (true)
            {
                SkipWhitespace(state);
                if (state.Eof)
                {
                    return RejectDoctype("internal subset", out fatalReason);
                }
                if (state.Peek() == ']')
                {
                    state.Pos++;
                    return true;
                }
                if (state.Peek() == '<')
                {
                    state.Pos++;
                }
                if (state.Eof || state.Peek() != '!')
                {
                    return RejectDoctype("internal subset", out fatalReason);
                }
                state.Pos++;

                if (Matches(state, state.Pos, "--"))
                {
                    state.Pos += 2;
                    int close = state.Source.IndexOf("-->", state.Pos, System.StringComparison.Ordinal);
                    if (close < 0)
                    {
                        return RejectDoctype("internal subset comment", out fatalReason);
                    }
                    state.Pos = close + 3;
                    continue;
                }

                if (Matches(state, state.Pos, "ENTITY"))
                {
                    state.Pos += 6;
                    if (!ReadEntityDeclaration(state, out fatalReason))
                    {
                        return false;
                    }
                    continue;
                }

                if (Matches(state, state.Pos, "ATTLIST"))
                {
                    state.Pos += 7;
                    if (!ReadAttlistDeclaration(state, out fatalReason))
                    {
                        return false;
                    }
                    continue;
                }

                if (state.Pos - start > MaxDoctypeChars)
                {
                    return RejectDoctype("internal subset over size budget", out fatalReason);
                }
                return RejectDoctype("declaration", out fatalReason);
            }
        }

        private static bool ReadEntityDeclaration(ParseState state, out string fatalReason)
        {
            fatalReason = null;
            SkipWhitespace(state);
            if (state.Eof)
            {
                return RejectDoctype("entity declaration", out fatalReason);
            }
            if (state.Peek() == '%')
            {
                return RejectDoctype("parameter entity declaration", out fatalReason);
            }
            if (!ReadName(state, out string name) || name.Length > MaxDoctypeEntityNameChars)
            {
                return RejectDoctype("entity name", out fatalReason);
            }
            SkipWhitespace(state);
            if (IsDtdKeyword(state, "SYSTEM") || IsDtdKeyword(state, "PUBLIC"))
            {
                return RejectDoctype("external entity declaration", out fatalReason);
            }
            SkipWhitespace(state);
            if (!ReadQuotedLiteral(state, MaxDoctypeEntityValueChars, out string value, out _))
            {
                return RejectDoctype("entity value", out fatalReason);
            }
            SkipWhitespace(state);
            if (state.Eof || state.Peek() != '>')
            {
                return RejectDoctype("entity declaration", out fatalReason);
            }
            state.Pos++;

            if (state.Dtd.EntityCount >= MaxDoctypeEntities)
            {
                return RejectDoctype($"entity count over budget ({MaxDoctypeEntities})", out fatalReason);
            }
            if (!TryExpandEntityText(state.Dtd, name, value, 0, out _, out string detail))
            {
                return RejectDoctype(detail, out fatalReason);
            }

            state.Dtd.Entities[name] = new SvgDtdEntity(name, value);
            state.Dtd.EntityCount++;
            return true;
        }

        private static bool ReadAttlistDeclaration(ParseState state, out string fatalReason)
        {
            fatalReason = null;
            SkipWhitespace(state);
            if (!ReadName(state, out _))
            {
                return RejectDoctype("attribute list declaration", out fatalReason);
            }

            int start = state.Pos;
            while (true)
            {
                if (state.Eof)
                {
                    return RejectDoctype("attribute list declaration", out fatalReason);
                }
                if (state.Pos - start > MaxDoctypeAttlistChars)
                {
                    return RejectDoctype("attribute list over size budget", out fatalReason);
                }

                char c = state.Peek();
                if (c == '>')
                {
                    state.Pos++;
                    state.Dtd.AttlistCount++;
                    return true;
                }
                if (c == ']')
                {
                    state.Dtd.AttlistCount++;
                    return true;
                }
                if (c == '<')
                {
                    if (state.PeekAt(1) != '!')
                    {
                        return RejectDoctype("attribute list declaration", out fatalReason);
                    }
                    state.Pos++;
                    return true;
                }
                if (c == '%')
                {
                    return RejectDoctype("parameter entity reference", out fatalReason);
                }
                if (c == '"' || c == '\'')
                {
                    return RejectDoctype("attribute list default value", out fatalReason);
                }
                state.Pos++;
            }
        }

        private static bool ReadExternalId(ParseState state, out string detail)
        {
            detail = null;
            bool isPublic = IsDtdKeyword(state, "PUBLIC");
            if (!isPublic && !IsDtdKeyword(state, "SYSTEM"))
            {
                detail = "external identifier";
                return false;
            }
            state.Pos += isPublic ? "PUBLIC".Length : "SYSTEM".Length;
            SkipWhitespace(state);
            if (!ReadQuotedLiteral(state, MaxDoctypeLiteralChars, out _, out _))
            {
                detail = "external identifier";
                return false;
            }
            if (isPublic)
            {
                SkipWhitespace(state);
                if (!ReadQuotedLiteral(state, MaxDoctypeLiteralChars, out _, out _))
                {
                    detail = "external identifier";
                    return false;
                }
            }
            return true;
        }

        private static bool ReadQuotedLiteral(
            ParseState state, int maxChars, out string value, out int length)
        {
            value = null;
            length = 0;
            char quote = state.Peek();
            if (quote != '"' && quote != '\'')
            {
                return false;
            }
            state.Pos++;

            int start = state.Pos;
            while (!state.Eof)
            {
                char c = state.Source[state.Pos];
                if (c == quote)
                {
                    length = state.Pos - start;
                    if (length > maxChars)
                    {
                        return false;
                    }
                    value = state.Source.Substring(start, length);
                    state.Pos++;
                    return true;
                }
                if (state.Pos - start >= maxChars)
                {
                    return false;
                }
                state.Pos++;
            }
            return false;
        }

        private static bool ReadName(ParseState state, out string name)
        {
            name = null;
            if (state.Eof || !IsNameStartChar(state.Peek()))
            {
                return false;
            }
            int start = state.Pos;
            state.Pos++;
            while (!state.Eof && IsNameChar(state.Peek()))
            {
                state.Pos++;
            }
            name = state.Source.Substring(start, state.Pos - start);
            return true;
        }

        private static bool IsDtdKeyword(ParseState state, string keyword)
        {
            if (state.Pos + keyword.Length > state.Source.Length)
            {
                return false;
            }
            if (string.Compare(
                    state.Source,
                    state.Pos,
                    keyword,
                    0,
                    keyword.Length,
                    System.StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }
            int after = state.Pos + keyword.Length;
            return after >= state.Source.Length || !IsNameChar(state.Source[after]);
        }

        private static bool Matches(ParseState state, int index, string word)
        {
            if (index < 0 || index + word.Length > state.Source.Length)
            {
                return false;
            }
            return string.CompareOrdinal(state.Source, index, word, 0, word.Length) == 0;
        }

        private static bool TryExpandEntityText(
            DtdSubset dtd, string name, string value, int depth, out string text, out string detail)
        {
            text = null;
            detail = null;
            var output = new System.Text.StringBuilder(
                System.Math.Min(value.Length, MaxEntityExpansionChars));
            if (!AppendEntityText(dtd, name, value, depth, output, out detail))
            {
                return false;
            }
            text = output.ToString();
            return true;
        }

        private static bool AppendEntityText(
            DtdSubset dtd,
            string name,
            string value,
            int depth,
            System.Text.StringBuilder output,
            out string detail)
        {
            detail = null;
            if (depth > MaxEntityExpansionDepth)
            {
                detail = $"entity expansion depth ({depth}) over budget ({MaxEntityExpansionDepth})";
                return false;
            }
            if (!dtd.Active.Add(name))
            {
                detail = $"recursive entity expansion of '{SvgDiagnosticText.Identifier(name)}'";
                return false;
            }

            try
            {
                int i = 0;
                while (i < value.Length)
                {
                    if (value[i] != '&')
                    {
                        if (!AppendBounded(output, value, i, 1))
                        {
                            detail = $"entity expansion over size budget ({MaxEntityExpansionChars})";
                            return false;
                        }
                        i++;
                        continue;
                    }

                    if (!TryReadEntityName(value, i, value.Length, out int bodyStart, out int bodyLength, out int after))
                    {
                        if (!AppendBounded(output, value, i, 1))
                        {
                            detail = $"entity expansion over size budget ({MaxEntityExpansionChars})";
                            return false;
                        }
                        i++;
                        continue;
                    }

                    if (value[bodyStart] == '#' || PredefinedEntityValue(value, bodyStart, bodyLength) != null)
                    {
                        if (!AppendBounded(output, value, i, after - i))
                        {
                            detail = $"entity expansion over size budget ({MaxEntityExpansionChars})";
                            return false;
                        }
                        i = after;
                        continue;
                    }

                    string reference = value.Substring(bodyStart, bodyLength);
                    if (dtd.Active.Contains(reference))
                    {
                        detail =
                            $"recursive entity expansion of '{SvgDiagnosticText.Identifier(reference)}'";
                        return false;
                    }
                    if (!dtd.Entities.TryGetValue(reference, out SvgDtdEntity nested))
                    {
                        detail =
                            $"undeclared entity reference '{SvgDiagnosticText.Identifier(reference)}' in internal subset";
                        return false;
                    }
                    if (dtd.Expansions >= MaxEntityExpansionsPerParse)
                    {
                        detail = $"entity expansion count over budget ({MaxEntityExpansionsPerParse})";
                        return false;
                    }
                    dtd.Expansions++;
                    if (!AppendEntityText(
                            dtd, nested.Name, nested.Value, depth + 1, output, out detail))
                    {
                        return false;
                    }
                    i = after;
                }
                return true;
            }
            finally
            {
                dtd.Active.Remove(name);
            }
        }

        private static bool AppendBounded(
            System.Text.StringBuilder output, string value, int start, int count)
        {
            if (output.Length + count > MaxEntityExpansionChars)
            {
                return false;
            }
            output.Append(value, start, count);
            return true;
        }

        private static bool TryExpandContentEntity(ParseState state, out string fatalReason)
        {
            fatalReason = null;
            DtdSubset dtd = state.Dtd;
            if (!TryReadEntityName(
                    state.Source, state.Pos, state.Source.Length, out int bodyStart, out int bodyLength, out int after))
            {
                return false;
            }
            if (state.Source[bodyStart] == '#' ||
                PredefinedEntityValue(state.Source, bodyStart, bodyLength) != null)
            {
                return false;
            }

            string name = state.Source.Substring(bodyStart, bodyLength);
            if (!dtd.Entities.TryGetValue(name, out SvgDtdEntity declared))
            {
                return false;
            }

            if (!TryExpandEntityText(dtd, declared.Name, declared.Value, 0, out string text, out string detail))
            {
                fatalReason = SvgDiagnosticText.Bounded(
                    $"DOCTYPE {detail} is rejected by the SVG sandbox",
                    SvgDiagnosticText.MaxFatalChars);
                return true;
            }
            if (dtd.Splices >= MaxEntitySplices ||
                dtd.SpliceCopyChars + state.Source.Length > MaxEntitySpliceCopyChars)
            {
                fatalReason = SvgDiagnosticText.Bounded(
                    $"DOCTYPE entity injection over splice budget ({MaxEntitySplices})",
                    SvgDiagnosticText.MaxFatalChars);
                return true;
            }
            if (dtd.InjectedChars + text.Length > MaxEntityInjectionChars)
            {
                fatalReason = SvgDiagnosticText.Bounded(
                    $"DOCTYPE entity injection over character budget ({MaxEntityInjectionChars})",
                    SvgDiagnosticText.MaxFatalChars);
                return true;
            }

            int amp = state.Pos;
            int previousLength = state.Source.Length;
            var spliced = new System.Text.StringBuilder(previousLength + text.Length);
            spliced.Append(state.Source, 0, amp);
            spliced.Append(text);
            spliced.Append(state.Source, after, previousLength - after);
            state.Source = spliced.ToString();
            dtd.Splices++;
            dtd.SpliceCopyChars += previousLength;
            dtd.InjectedChars += text.Length;
            return true;
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
            state.PendingNamespaceFrame = null;
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
            state.PendingNamespaceFrame = BuildNamespaceFrame(state, name, attributes);
            element.Name = state.PendingNamespaceFrame.ResolvedName;
            element.NamespaceUri = state.PendingNamespaceFrame.NamespaceUri;
            return element;
        }

        private static NamespaceFrame BuildNamespaceFrame(
            ParseState state,
            string rawName,
            List<KeyValuePair<string, string>> attributes)
        {
            var current = state.NamespaceStack.Count == 0
                ? null
                : state.NamespaceStack[state.NamespaceStack.Count - 1];
            var bindings = current == null
                ? new Dictionary<string, string>(System.StringComparer.Ordinal)
                : new Dictionary<string, string>(current.Bindings, System.StringComparer.Ordinal);
            foreach (var attribute in attributes)
            {
                if (string.Equals(attribute.Key, "xmlns", System.StringComparison.OrdinalIgnoreCase))
                {
                    bindings[string.Empty] = (attribute.Value ?? string.Empty).Trim();
                }
                else if (attribute.Key.StartsWith("xmlns:", System.StringComparison.OrdinalIgnoreCase))
                {
                    string prefix = attribute.Key.Substring(6).Trim();
                    if (prefix.Length != 0)
                        bindings[prefix] = (attribute.Value ?? string.Empty).Trim();
                }
            }

            string namespaceUri = null;
            bool isSvg = false;
            string localName = rawName;
            int colon = rawName.IndexOf(':');
            if (colon > 0 && colon < rawName.Length - 1)
            {
                string prefix = rawName.Substring(0, colon);
                localName = rawName.Substring(colon + 1);
                if (bindings.TryGetValue(prefix, out string boundNamespace))
                {
                    namespaceUri = boundNamespace;
                    isSvg = string.Equals(boundNamespace, SvgNamespace, System.StringComparison.Ordinal);
                }
            }
            else if (bindings.TryGetValue(string.Empty, out string defaultNamespace))
            {
                namespaceUri = defaultNamespace;
                isSvg = string.Equals(defaultNamespace, SvgNamespace, System.StringComparison.Ordinal);
            }
            else if (current != null && current.IsSvgNamespace)
            {
                namespaceUri = SvgNamespace;
                isSvg = true;
            }
            else if (rawName.Equals("svg", System.StringComparison.Ordinal))
            {
                isSvg = true;
            }

            string resolvedName = isSvg
                ? localName
                : namespaceUri == null || namespaceUri.Length == 0 || colon >= 0
                    ? rawName
                    : "{" + namespaceUri + "}" + rawName;
            return new NamespaceFrame(bindings, isSvg, resolvedName, namespaceUri, rawName);
        }

        private static string ResolveCloseName(ParseState state, string rawName) =>
            BuildNamespaceFrame(state, rawName, new List<KeyValuePair<string, string>>()).ResolvedName;

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
                state.Report.Warn(
                    $"duplicate attribute '{SvgDiagnosticText.Identifier(attrName)}' ignored (first wins)");
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
                int remaining = s.Length - start;
                if (remaining > MaxAttributeValueChars)
                {
                    remaining = MaxAttributeValueChars;
                    state.Report.Warn("attribute value truncated over length budget");
                }
                return s.Substring(start, remaining); // Unterminated quote: bounded prefix.
            }

            int segmentLength = i - start;
            bool hasEntity = segmentLength >= 1 && s.IndexOf('&', start, segmentLength) >= 0;
            if (!hasEntity)
            {
                state.Pos = i + 1;
                if (segmentLength > MaxAttributeValueChars)
                {
                    state.Report.Warn("attribute value truncated over length budget");
                    segmentLength = MaxAttributeValueChars;
                }
                return s.Substring(start, segmentLength);
            }

            // Slow path: expand entities character by character.
            var sb = new System.Text.StringBuilder(System.Math.Min(segmentLength, MaxAttributeValueChars));
            bool truncated = false;
            state.Pos = start;
            while (!state.Eof && state.Peek() != quote)
            {
                if (state.Peek() == '&')
                {
                    ExpandEntity(state, sb, i);
                }
                else
                {
                    if (sb.Length < MaxAttributeValueChars)
                    {
                        sb.Append(state.Source[state.Pos]);
                    }
                    else
                    {
                        truncated = true;
                    }
                    state.Pos++;
                }
                if (sb.Length > MaxAttributeValueChars)
                {
                    sb.Length = MaxAttributeValueChars;
                    truncated = true;
                }
            }
            state.Pos++; // Closing quote (or EOF-safe increment past end).
            if (truncated)
            {
                state.Report.Warn("attribute value truncated over length budget");
            }
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
            int length = state.Pos - start;
            if (length > MaxAttributeValueChars)
            {
                length = MaxAttributeValueChars;
                state.Report.Warn("attribute value truncated over length budget");
            }
            return state.Source.Substring(start, length);
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

            string resolvedName = ResolveCloseName(state, name);
            for (int i = openStack.Count - 1; i >= 0; i--)
            {
                if (string.Equals(openStack[i].Name, resolvedName, System.StringComparison.Ordinal) ||
                    string.Equals(openStack[i].Name, name, System.StringComparison.Ordinal))
                {
                    int removeCount = openStack.Count - i;
                    openStack.RemoveRange(i, removeCount);
                    if (state.NamespaceStack.Count >= removeCount)
                        state.NamespaceStack.RemoveRange(state.NamespaceStack.Count - removeCount, removeCount);
                    return;
                }
            }
            // Unmatched close tag: ignore (browser-style recovery).
        }

        // -------------------------------------------------------- raw skipping

        private static void AddChild(SvgElement parent, SvgElement child)
        {
            child.Parent = parent;
            child.PreviousElementSibling = parent.Children.Count == 0
                ? null
                : parent.Children[parent.Children.Count - 1];
            parent.Children.Add(child);
            if (IsCharacterDataContainer(parent.Name))
            {
                parent.Content.Add(new SvgContentPart(child));
            }
        }

        /// <summary>
        /// True for the SVG elements whose children may be character data rather
        /// than graphics. <c>text</c>/<c>tspan</c> are the classic pair, but
        /// <c>textPath</c> and the link element <c>a</c> carry glyph runs too, so
        /// their content is materialized under the same budget and rules.
        /// </summary>
        private static bool IsCharacterDataContainer(string name) =>
            name is "text" or "tspan" or "textPath" or "a" or "foreignObject";

        private static bool SameIgnoredName(string first, string second)
        {
            if (string.Equals(first, second, System.StringComparison.OrdinalIgnoreCase)) return true;
            int firstColon = first?.IndexOf(':') ?? -1;
            int secondColon = second?.IndexOf(':') ?? -1;
            if (firstColon < 0 || secondColon < 0) return false;
            return string.Equals(
                first.Substring(firstColon + 1), second.Substring(secondColon + 1),
                System.StringComparison.OrdinalIgnoreCase);
        }

        private static void SkipIgnoredSubtree(ParseState state, SvgElement element)
        {
            string name = element.Name;
            int contentStart = state.Pos;
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
                int markupStart = state.Pos;
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
                    if (SameIgnoredName(closed, name))
                    {
                        depth--;
                        if (depth == 0)
                        {
                            if (string.Equals(name, "style", System.StringComparison.OrdinalIgnoreCase))
                            {
                                int length = markupStart - contentStart;
                                if (length > MaxTextContentChars)
                                {
                                    throw new SvgSandboxViolationException(
                                        $"SVG style text length ({length}) exceeds limit ({MaxTextContentChars})");
                                }
                                element.TextContent = length > 0
                                    ? ExtractStyleText(state, contentStart, markupStart)
                                    : string.Empty;
                            }
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
                    if (!selfClosing && SameIgnoredName(opened, name))
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

        private static bool HasForeignEndTag(ParseState state, string rawName)
        {
            if (rawName.Length == 0 || state.ForeignEndTagScanBudget <= 0)
            {
                return false;
            }

            int start = state.Pos;
            long budget = state.ForeignEndTagScanBudget;
            int end = (int)System.Math.Min(state.Source.Length, start + budget);
            int resume = start;
            int depth = 1;
            while (resume < end)
            {
                int lt = state.Source.IndexOf('<', resume, end - resume);
                if (lt < 0)
                {
                    break;
                }

                int savedPos = state.Pos;
                state.Pos = lt + 1;
                if (state.Eof)
                {
                    break;
                }

                if (state.Peek() == '/')
                {
                    state.Pos++;
                    int nameStart = state.Pos;
                    while (!state.Eof && IsNameChar(state.Peek()))
                    {
                        state.Pos++;
                    }
                    if (SameIgnoredName(
                            state.Source.Substring(nameStart, state.Pos - nameStart), rawName) &&
                        --depth == 0)
                    {
                        state.ForeignEndTagScanBudget =
                            System.Math.Max(0, budget - (System.Math.Min(state.Pos, end) - start));
                        state.Pos = start;
                        return true;
                    }
                    resume = state.Pos;
                    state.Pos = savedPos;
                    continue;
                }

                if (IsNameStartChar(state.Peek()))
                {
                    int nameStart = state.Pos;
                    while (!state.Eof && IsNameChar(state.Peek()))
                    {
                        state.Pos++;
                    }
                    // Read the name before scanning past the tag: the scan moves the
                    // cursor over the attributes, which are not part of the name.
                    string opened = state.Source.Substring(nameStart, state.Pos - nameStart);
                    bool insideWindow = state.Pos < end;
                    bool selfClosing = insideWindow && ScanPastTagEnd(state);
                    if (!selfClosing && SameIgnoredName(opened, rawName))
                    {
                        depth++;
                    }
                    resume = System.Math.Min(state.Pos, end);
                    state.Pos = savedPos;
                    continue;
                }

                if (state.Peek() == '!' || state.Peek() == '?')
                {
                    SkipBangOrPi(state);
                }

                resume = System.Math.Min(state.Pos, end);
                state.Pos = savedPos;
            }

            state.ForeignEndTagScanBudget =
                System.Math.Max(0, budget - (System.Math.Max(resume, end) - start));
            state.Pos = start;
            return false;
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
                    if (!state.Eof) state.Pos++; // closing quote; an unterminated value stops at EOF
                    continue;
                }
                if (c == '\'')
                {
                    state.Pos++;
                    while (!state.Eof && state.Peek() != '\'')
                    {
                        state.Pos++;
                    }
                    if (!state.Eof) state.Pos++;
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

        private static void SkipContentNoise(ParseState state, SvgElement current)
        {
            // Advances past text content and the leading '<' of the next markup,
            // leaving the cursor on the character AFTER '<'.
            bool expandsDeclaredEntities = state.Dtd != null && !IsCharacterDataContainer(current.Name);
            int textStart = state.Pos;
            while (!state.Eof)
            {
                char c = state.Peek();
                if (c == '<')
                {
                    AppendTextContent(state, current, textStart, state.Pos);
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
                if (c == '&' && expandsDeclaredEntities && IsNameStartChar(state.PeekAt(1)))
                {
                    // Hand the reference to the caller so declared entities that
                    // expand to markup are spliced into the scan position.
                    AppendTextContent(state, current, textStart, state.Pos);
                    return;
                }
                state.Pos++;
            }
            AppendTextContent(state, current, textStart, state.Pos);
        }

        private static void AppendTextContent(
            ParseState state,
            SvgElement current,
            int start,
            int end)
        {
            if (!IsCharacterDataContainer(current.Name) || end <= start)
            {
                return;
            }

            int existing = current.TextContent?.Length ?? 0;
            int remaining = MaxTextContentChars - existing;
            if (remaining <= 0)
            {
                state.Report.Warn("text content truncated over length budget");
                return;
            }

            string segment = DecodeCharacterData(state, start, end, remaining, out bool truncated);
            if (truncated)
            {
                state.Report.Warn("text content truncated over length budget");
            }

            current.TextContent = existing == 0 ? segment : current.TextContent + segment;
            if (segment.Length > 0)
            {
                current.Content.Add(new SvgContentPart(segment));
            }
        }

        private static string DecodeCharacterData(
            ParseState state,
            int start,
            int end,
            int maximumChars,
            out bool truncated)
        {
            var decoded = new System.Text.StringBuilder(System.Math.Min(end - start, maximumChars));
            var cursor = new ParseState(state.Source, state.Report) { Pos = start, Dtd = state.Dtd };
            while (cursor.Pos < end && decoded.Length < maximumChars)
            {
                if (cursor.Peek() == '&')
                {
                    ExpandEntity(cursor, decoded, end);
                }
                else
                {
                    decoded.Append(cursor.Peek());
                    cursor.Pos++;
                }
            }
            truncated = cursor.Pos < end;
            return decoded.ToString();
        }

        private static string ExtractStyleText(ParseState state, int start, int end)
        {
            int trimmedStart = start;
            int trimmedEnd = end;
            while (trimmedStart < trimmedEnd && char.IsWhiteSpace(state.Source[trimmedStart])) trimmedStart++;
            while (trimmedEnd > trimmedStart && char.IsWhiteSpace(state.Source[trimmedEnd - 1])) trimmedEnd--;

            const string cdataOpen = "<![CDATA[";
            const string cdataClose = "]]>";
            if (trimmedEnd - trimmedStart >= cdataOpen.Length + cdataClose.Length &&
                string.CompareOrdinal(state.Source, trimmedStart, cdataOpen, 0, cdataOpen.Length) == 0 &&
                string.CompareOrdinal(
                    state.Source,
                    trimmedEnd - cdataClose.Length,
                    cdataClose,
                    0,
                    cdataClose.Length) == 0)
            {
                int contentStart = trimmedStart + cdataOpen.Length;
                int contentLength = trimmedEnd - cdataClose.Length - contentStart;
                return state.Source.Substring(contentStart, contentLength);
            }

            return DecodeCharacterData(state, start, end, MaxTextContentChars, out _);
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

        private static void ExpandEntity(
            ParseState state,
            System.Text.StringBuilder sb,
            int endExclusive)
        {
            // Cursor at '&'.
            string s = state.Source;
            int ampPos = state.Pos;

            if (!TryReadEntityName(
                    s, ampPos, endExclusive, out int bodyStart, out int bodyLength, out int afterName))
            {
                sb.Append('&');
                state.Pos = ampPos + 1;
                return;
            }

            string predefined = PredefinedEntityValue(s, bodyStart, bodyLength);
            if (predefined != null)
            {
                sb.Append(predefined);
                state.Pos = afterName;
                return;
            }

            if (s[bodyStart] == '#')
            {
                if (TryExpandNumericRef(s, bodyStart + 1, afterName - 1, sb))
                {
                    state.Pos = afterName;
                    return;
                }
                // Malformed numeric ref: emit replacement char (browser-style).
                sb.Append('\uFFFD');
                state.Pos = afterName;
                return;
            }

            DtdSubset dtd = state.Dtd;
            if (dtd != null &&
                dtd.Entities.TryGetValue(s.Substring(bodyStart, bodyLength), out SvgDtdEntity declared))
            {
                if (TryExpandEntityText(
                        dtd, declared.Name, declared.Value, 0, out string text, out string detail))
                {
                    sb.Append(text);
                }
                else
                {
                    sb.Append('\uFFFD');
                    state.Report.Warn(SvgDiagnosticText.Bounded(
                        $"DOCTYPE {detail}", SvgDiagnosticText.MaxFatalChars));
                }
                state.Pos = afterName;
                return;
            }

            // Unknown named entity: emit literal text (never fatal).
            sb.Append('&');
            state.Pos = ampPos + 1;
        }

        private static bool TryReadEntityName(
            string s,
            int amp,
            int endExclusive,
            out int bodyStart,
            out int bodyLength,
            out int afterName)
        {
            bodyStart = amp + 1;
            bodyLength = 0;
            afterName = amp + 1;
            int limit = System.Math.Min(endExclusive, s.Length);
            int window = System.Math.Min(
                MaxEntityNameChars + 2, // '&' + bounded body + ';'
                System.Math.Max(0, limit - amp));
            if (window <= 0)
            {
                return false;
            }
            int semi = s.IndexOf(';', amp, window);
            if (semi < 0)
            {
                return false;
            }
            int length = semi - amp - 1;
            if (length == 0 || length > MaxEntityNameChars)
            {
                return false;
            }
            bodyLength = length;
            afterName = semi + 1;
            return true;
        }

        private static string PredefinedEntityValue(string s, int start, int length)
        {
            if (length == 3)
            {
                if (s[start] == 'a' && s[start + 1] == 'm' && s[start + 2] == 'p') return "&";
            }
            else if (length == 2)
            {
                if (s[start] == 'l' && s[start + 1] == 't') return "<";
                if (s[start] == 'g' && s[start + 1] == 't') return ">";
            }
            else if (length == 4)
            {
                if (s[start] == 'q' && s[start + 1] == 'u' &&
                    s[start + 2] == 'o' && s[start + 3] == 't') return "\"";
            }
            else if (length == 5)
            {
                if (s[start] == 'a' && s[start + 1] == 'p' && s[start + 2] == 'o' &&
                    s[start + 3] == 's') return "'";
            }
            return null;
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
                doc.Report.Warn(
                    $"duplicate id '{SvgDiagnosticText.Identifier(id)}' ignored (first wins)");
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
    /// It never leaves <see cref="SvgMarkupParser.TryParse"/>, which reports it as
    /// a fatal reason; render-time budget checks throw it to the engine, which
    /// converts it to the same bare limit message. It is never caused by malformed
    /// input - that always recovers.
    /// </summary>
    internal sealed class SvgSandboxViolationException : System.Exception
    {
        public SvgSandboxViolationException(string message) : base(message) { }
    }
}
