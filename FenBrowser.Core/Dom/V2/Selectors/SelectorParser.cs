// WHATWG DOM Living Standard compliant implementation
// FenBrowser.Core.Dom.V2.Selectors - Compiled Selector Parser

using System;
using System.Collections.Generic;
using System.Text;

namespace FenBrowser.Core.Dom.V2.Selectors
{
    /// <summary>
    /// High-performance CSS selector parser.
    /// Parses selector strings into CompiledSelector objects for fast matching.
    /// </summary>
    public static class SelectorParser
    {
        /// <summary>
        /// Parses a selector string into a compiled selector.
        /// Supports CSS Selectors Level 3 and partial Level 4.
        /// </summary>
        public static CompiledSelector Parse(string selectors)
        {
            if (string.IsNullOrWhiteSpace(selectors))
                throw new DomException("SyntaxError", "Selector cannot be empty");

            var tokens = Tokenize(selectors);
            var chains = ParseSelectorList(tokens);

            return new CompiledSelector(chains);
        }

        internal readonly struct RelativeSelectorBranch
        {
            public RelativeSelectorBranch(CompiledSelector selector, Combinator leadingCombinator, string source)
            {
                Selector = selector ?? throw new ArgumentNullException(nameof(selector));
                LeadingCombinator = leadingCombinator;
                Source = source ?? string.Empty;
            }

            public CompiledSelector Selector { get; }
            public Combinator LeadingCombinator { get; }
            public string Source { get; }
        }

        internal static RelativeSelectorBranch[] ParseRelativeSelectorList(string selectors)
        {
            if (string.IsNullOrWhiteSpace(selectors))
                throw new DomException("SyntaxError", ":has() requires a relative selector");

            var result = new List<RelativeSelectorBranch>();
            foreach (var rawBranch in SplitTopLevelSelectorBranches(selectors))
            {
                var branch = rawBranch.Trim();
                if (branch.Length == 0)
                    continue;

                var leading = Combinator.Descendant;
                switch (branch[0])
                {
                    case '>': leading = Combinator.Child; break;
                    case '+': leading = Combinator.AdjacentSibling; break;
                    case '~': leading = Combinator.GeneralSibling; break;
                }

                if (branch[0] is '>' or '+' or '~')
                    branch = branch.Substring(1).TrimStart();

                if (branch.Length == 0)
                    continue;

                try
                {
                    result.Add(new RelativeSelectorBranch(Parse(branch), leading, rawBranch.Trim()));
                }
                catch (DomException)
                {
                    // :has() uses a forgiving relative-selector list. One invalid
                    // branch must not invalidate otherwise usable alternatives.
                }
            }

            if (result.Count == 0)
                throw new DomException("SyntaxError", ":has() contains no valid relative selector");

            return result.ToArray();
        }

        private static IEnumerable<string> SplitTopLevelSelectorBranches(string input)
        {
            int start = 0;
            int parenDepth = 0;
            int bracketDepth = 0;
            char quote = '\0';
            bool escaped = false;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (quote != '\0')
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }
                    continue;
                }

                if (c is '\'' or '"')
                {
                    quote = c;
                    continue;
                }
                if (c == '\\')
                {
                    if (i + 1 < input.Length) i++;
                    continue;
                }
                if (c == '(') { parenDepth++; continue; }
                if (c == ')') { if (parenDepth > 0) parenDepth--; continue; }
                if (c == '[') { bracketDepth++; continue; }
                if (c == ']') { if (bracketDepth > 0) bracketDepth--; continue; }

                if (c == ',' && parenDepth == 0 && bracketDepth == 0)
                {
                    yield return input.Substring(start, i - start);
                    start = i + 1;
                }
            }

            yield return input.Substring(start);
        }

        // --- Tokenizer ---

        private static List<Token> Tokenize(string input)
        {
            var tokens = new List<Token>();
            int i = 0;

            while (i < input.Length)
            {
                char c = input[i];

                // Whitespace
                if (IsCssWhitespace(c))
                {
                    while (i < input.Length && IsCssWhitespace(input[i]))
                        i++;
                    tokens.Add(new Token(TokenType.Whitespace, " "));
                    continue;
                }

                // Combinators
                if (c == '>')
                {
                    tokens.Add(new Token(TokenType.ChildCombinator, ">"));
                    i++;
                    continue;
                }
                if (c == '+')
                {
                    tokens.Add(new Token(TokenType.AdjacentSiblingCombinator, "+"));
                    i++;
                    continue;
                }
                if (c == '~')
                {
                    tokens.Add(new Token(TokenType.GeneralSiblingCombinator, "~"));
                    i++;
                    continue;
                }

                // Comma (selector list separator)
                if (c == ',')
                {
                    tokens.Add(new Token(TokenType.Comma, ","));
                    i++;
                    continue;
                }

                // ID selector
                if (c == '#')
                {
                    i++;
                    var ident = ReadIdent(input, ref i);
                    if (string.IsNullOrEmpty(ident))
                        throw new DomException("SyntaxError", $"Expected identifier after # at position {i}");
                    tokens.Add(new Token(TokenType.IdSelector, ident));
                    continue;
                }

                // Class selector
                if (c == '.')
                {
                    i++;
                    var ident = ReadIdent(input, ref i);
                    if (string.IsNullOrEmpty(ident))
                        throw new DomException("SyntaxError", $"Expected identifier after . at position {i}");
                    tokens.Add(new Token(TokenType.ClassSelector, ident));
                    continue;
                }

                // Attribute selector
                if (c == '[')
                {
                    i++;
                    var attr = ReadAttributeSelector(input, ref i);
                    tokens.Add(attr);
                    continue;
                }

                // Pseudo-class or pseudo-element
                if (c == ':')
                {
                    i++;
                    bool isElement = false;
                    if (i < input.Length && input[i] == ':')
                    {
                        isElement = true;
                        i++;
                    }
                    var ident = ReadIdent(input, ref i);
                    if (string.IsNullOrEmpty(ident))
                        throw new DomException("SyntaxError", $"Expected identifier after : at position {i}");

                    // Check for functional pseudo-class
                    string arg = null;
                    if (i < input.Length && input[i] == '(')
                    {
                        i++;
                        arg = ReadFunctionArg(input, ref i);
                    }

                    tokens.Add(new Token(
                        isElement ? TokenType.PseudoElement : TokenType.PseudoClass,
                        ident,
                        arg));
                    continue;
                }

                // Universal selector, possibly the "any namespace" prefix of a type
                // selector: "*|button" matches a button in any namespace at all.
                if (c == '*')
                {
                    i++;
                    if (TryReadNamespaceSeparator(input, ref i))
                    {
                        tokens.Add(ReadQualifiedName(input, ref i));
                        continue;
                    }

                    tokens.Add(new Token(TokenType.UniversalSelector, "*"));
                    continue;
                }

                // A type selector with no namespace at all: "|button".
                if (c == '|' && TryReadNamespaceSeparator(input, ref i))
                {
                    tokens.Add(ReadQualifiedName(input, ref i));
                    continue;
                }

                // Type selector (element name), or a namespace prefix before one.
                if (IsIdentStart(c))
                {
                    var ident = ReadIdent(input, ref i);
                    if (TryReadNamespaceSeparator(input, ref i))
                    {
                        // A prefix can only be resolved against namespace declarations,
                        // and a selector string carries none: Selectors 4 makes that a
                        // parse error rather than something that matches nothing.
                        throw new DomException("SyntaxError", $"Undeclared namespace prefix '{ident}' in selector");
                    }

                    tokens.Add(new Token(TokenType.TypeSelector, ident));
                    continue;
                }

                throw new DomException("SyntaxError", $"Unexpected character '{c}' at position {i}");
            }

            return tokens;
        }

        private static string ReadIdent(string input, ref int i)
        {
            var sb = new StringBuilder();
            while (i < input.Length)
            {
                if (IsIdentChar(input[i]))
                {
                    sb.Append(input[i]);
                    i++;
                    continue;
                }

                if (input[i] == '\\')
                {
                    if (!TryReadEscapedCodePoint(input, ref i, out var escaped))
                        break;

                    sb.Append(escaped);
                    continue;
                }

                break;
            }
            return sb.ToString();
        }

        /// <summary>
        /// A '|' that separates a namespace prefix from a local name, rather than the '||'
        /// column combinator or the "|=" attribute operator. Consumes it when it is one.
        /// </summary>
        private static bool TryReadNamespaceSeparator(string input, ref int i)
        {
            if (i >= input.Length || input[i] != '|')
                return false;
            if (i + 1 < input.Length && (input[i + 1] == '|' || input[i + 1] == '='))
                return false;
            i++;
            return true;
        }

        /// <summary>The local name after a namespace prefix; '*' means any name.</summary>
        private static Token ReadQualifiedName(string input, ref int i)
        {
            if (i < input.Length && input[i] == '*')
            {
                i++;
                return new Token(TokenType.UniversalSelector, "*");
            }

            var name = ReadIdent(input, ref i);
            if (string.IsNullOrEmpty(name))
                throw new DomException("SyntaxError", "Expected an element name after a namespace separator");
            return new Token(TokenType.TypeSelector, name);
        }

        private static Token ReadAttributeSelector(string input, ref int i)
        {
            // Read attribute name
            SkipWhitespace(input, ref i);
            var attrName = ReadIdent(input, ref i);
            if (string.IsNullOrEmpty(attrName))
                throw new DomException("SyntaxError", $"Expected attribute name at position {i}");
            SkipWhitespace(input, ref i);

            if (i >= input.Length)
                throw new DomException("SyntaxError", "Unexpected end of attribute selector");

            // Just presence check?
            if (input[i] == ']')
            {
                i++;
                return new Token(TokenType.AttributeSelector, attrName, null, AttributeMatchType.Exists);
            }

            // Read operator
            var matchType = AttributeMatchType.Equals;
            char op = input[i];
            if (op == '=')
            {
                matchType = AttributeMatchType.Equals;
                i++;
            }
            else if (i + 1 < input.Length && input[i + 1] == '=')
            {
                matchType = op switch
                {
                    '~' => AttributeMatchType.Includes,
                    '|' => AttributeMatchType.DashMatch,
                    '^' => AttributeMatchType.Prefix,
                    '$' => AttributeMatchType.Suffix,
                    '*' => AttributeMatchType.Substring,
                    _ => throw new DomException("SyntaxError", $"Invalid attribute operator '{op}'")
                };
                i += 2;
            }
            else
            {
                throw new DomException("SyntaxError", $"Invalid attribute operator at position {i}");
            }

            // Read value
            SkipWhitespace(input, ref i);
            string value;
            if (i < input.Length && (input[i] == '"' || input[i] == '\''))
            {
                value = ReadString(input, ref i);
            }
            else
            {
                value = ReadIdent(input, ref i);
                if (string.IsNullOrEmpty(value))
                    throw new DomException("SyntaxError", $"Expected attribute value at position {i}");
            }

            // Check for case-sensitivity flag
            SkipWhitespace(input, ref i);
            bool caseInsensitive = false;
            if (i < input.Length &&
                (input[i] == 'i' || input[i] == 'I' || input[i] == 's' || input[i] == 'S'))
            {
                caseInsensitive = input[i] == 'i' || input[i] == 'I';
                i++;
                SkipWhitespace(input, ref i);
            }

            if (i >= input.Length || input[i] != ']')
                throw new DomException("SyntaxError", "Expected ] at end of attribute selector");
            i++;

            return new Token(TokenType.AttributeSelector, attrName, value, matchType, caseInsensitive);
        }

        private static string ReadString(string input, ref int i)
        {
            char quote = input[i];
            i++;
            var sb = new StringBuilder();
            while (i < input.Length)
            {
                if (input[i] == quote)
                {
                    i++;
                    return sb.ToString();
                }

                if (input[i] == '\\')
                {
                    if (i + 1 >= input.Length)
                        throw new DomException("SyntaxError", "Unterminated escape in selector string");

                    i++;
                    sb.Append(input[i]);
                    i++;
                    continue;
                }

                sb.Append(input[i]);
                i++;
            }

            throw new DomException("SyntaxError", "Unterminated selector string");
        }

        private static string ReadFunctionArg(string input, ref int i)
        {
            int depth = 1;
            char quote = '\0';
            bool escaped = false;
            var sb = new StringBuilder();

            while (i < input.Length)
            {
                char c = input[i++];

                if (quote != '\0')
                {
                    sb.Append(c);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == quote)
                    {
                        quote = '\0';
                    }
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    quote = c;
                    sb.Append(c);
                    continue;
                }

                if (c == '(')
                {
                    depth++;
                    sb.Append(c);
                    continue;
                }

                if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                        return sb.ToString().Trim();

                    sb.Append(c);
                    continue;
                }

                if (c == '\\' && i < input.Length)
                {
                    // Preserve CSS escapes for the nested selector parser rather
                    // than consuming them at the outer functional-pseudo layer.
                    sb.Append(c);
                    sb.Append(input[i++]);
                    continue;
                }

                sb.Append(c);
            }

            throw new DomException("SyntaxError", "Unterminated functional pseudo-class");
        }

        private static void SkipWhitespace(string input, ref int i)
        {
            while (i < input.Length && IsCssWhitespace(input[i]))
                i++;
        }

        private static bool IsIdentStart(char c)
        {
            return char.IsLetter(c) || c == '_' || c == '-' || c == '\\' || c > 127;
        }

        private static bool IsIdentChar(char c)
        {
            // A backslash is not part of an identifier: it starts an escape, and the caller
            // reads it. Counting it here meant the escape branch was never reached, so a
            // fully escaped selector - the form wptrunner builds, "#\77 \70 \74 ..." -
            // matched nothing at all.
            return char.IsLetterOrDigit(c) || c == '_' || c == '-' || c > 127;
        }

        private static bool TryReadEscapedCodePoint(string input, ref int i, out string escaped)
        {
            escaped = string.Empty;
            if (i >= input.Length || input[i] != '\\')
                return false;

            i++;
            if (i >= input.Length)
                return false;

            int hexStart = i;
            int hexLen = 0;
            while (i < input.Length && hexLen < 6 && IsHexDigit(input[i]))
            {
                i++;
                hexLen++;
            }

            if (hexLen > 0)
            {
                var hex = input.Substring(hexStart, hexLen);
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int codePoint))
                {
                    // CSS Syntax replaces NULL, surrogate code points and values
                    // outside Unicode's scalar range rather than feeding them to
                    // ConvertFromUtf32 (which throws for surrogates).
                    if (codePoint == 0 || codePoint > 0x10FFFF ||
                        (codePoint >= 0xD800 && codePoint <= 0xDFFF))
                    {
                        codePoint = 0xFFFD;
                    }
                    escaped = char.ConvertFromUtf32(codePoint);
                }
                else
                {
                    escaped = "\uFFFD";
                }

                if (i < input.Length && IsCssWhitespace(input[i]))
                    i++;

                return true;
            }

            escaped = input[i].ToString();
            i++;
            return true;
        }

        private static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') ||
                   (c >= 'a' && c <= 'f') ||
                   (c >= 'A' && c <= 'F');
        }

        private static bool IsCssWhitespace(char c)
        {
            return c is '\t' or '\n' or '\f' or '\r' or ' ';
        }

        // --- Parser ---

        private static List<SelectorChain> ParseSelectorList(List<Token> tokens)
        {
            var chains = new List<SelectorChain>();
            var currentCompound = new List<SimpleSelector>();
            var currentChain = new List<(List<SimpleSelector> Compound, Combinator Combinator)>();
            Combinator pendingCombinator = Combinator.None;
            bool justSawComma = false;

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];

                switch (token.Type)
                {
                    case TokenType.Comma:
                        if (currentCompound.Count == 0)
                        {
                            // Empty branches, repeated commas, and a comma after an
                            // explicit combinator are all invalid selector lists.
                            throw new DomException("SyntaxError", "Empty selector branch");
                        }

                        currentChain.Add((currentCompound, Combinator.None));
                        chains.Add(new SelectorChain(currentChain));
                        currentCompound = new List<SimpleSelector>();
                        currentChain = new List<(List<SimpleSelector>, Combinator)>();
                        pendingCombinator = Combinator.None;
                        justSawComma = true;
                        break;

                    case TokenType.Whitespace:
                        // Potential descendant combinator. Leading/trailing whitespace
                        // is harmless and does not create an empty compound.
                        if (currentCompound.Count > 0)
                        {
                            pendingCombinator = Combinator.Descendant;
                        }
                        break;

                    case TokenType.ChildCombinator:
                    case TokenType.AdjacentSiblingCombinator:
                    case TokenType.GeneralSiblingCombinator:
                        if (currentCompound.Count == 0)
                        {
                            // Relative selectors are not accepted by this top-level
                            // parser; callers such as querySelector must fail closed on
                            // leading/repeated explicit combinators.
                            throw new DomException("SyntaxError", "Combinator is missing a left-hand selector");
                        }

                        var explicitCombinator = token.Type switch
                        {
                            TokenType.ChildCombinator => Combinator.Child,
                            TokenType.AdjacentSiblingCombinator => Combinator.AdjacentSibling,
                            _ => Combinator.GeneralSibling
                        };
                        currentChain.Add((currentCompound, explicitCombinator));
                        currentCompound = new List<SimpleSelector>();
                        pendingCombinator = Combinator.None;
                        justSawComma = false;
                        break;

                    default:
                        // Simple selector
                        if (pendingCombinator == Combinator.Descendant && currentCompound.Count > 0)
                        {
                            currentChain.Add((currentCompound, Combinator.Descendant));
                            currentCompound = new List<SimpleSelector>();
                        }
                        pendingCombinator = Combinator.None;

                        currentCompound.Add(TokenToSimpleSelector(token));
                        justSawComma = false;
                        break;
                }
            }

            if (justSawComma)
                throw new DomException("SyntaxError", "Selector list cannot end with a comma");

            if (currentCompound.Count > 0)
            {
                currentChain.Add((currentCompound, Combinator.None));
            }
            else if (currentChain.Count > 0)
            {
                // The only way to have a chain without a current compound here is
                // to have ended with an explicit combinator.
                throw new DomException("SyntaxError", "Selector cannot end with a combinator");
            }

            if (currentChain.Count > 0)
            {
                chains.Add(new SelectorChain(currentChain));
            }

            if (chains.Count == 0)
                throw new DomException("SyntaxError", "Empty selector");

            return chains;
        }

        private static SimpleSelector TokenToSimpleSelector(Token token)
        {
            return token.Type switch
            {
                TokenType.TypeSelector => new TypeSelector(token.Value),
                TokenType.UniversalSelector => new UniversalSelector(),
                TokenType.IdSelector => new IdSelector(token.Value),
                TokenType.ClassSelector => new ClassSelector(token.Value),
                TokenType.AttributeSelector => new AttributeSelector(
                    token.Value, token.AttrValue, token.MatchType, token.CaseInsensitive),
                TokenType.PseudoClass => CreatePseudoClassSelector(token.Value, token.FunctionArg),
                TokenType.PseudoElement => new PseudoElementSelector(token.Value, token.FunctionArg),
                _ => throw new DomException("SyntaxError", $"Unexpected token type {token.Type}")
            };
        }

        private static SimpleSelector CreatePseudoClassSelector(string name, string arg)
        {
            var normalizedName = name.ToLowerInvariant();
            SimpleSelector selector = normalizedName switch
            {
                "not" => new NegationSelector(Parse(arg ?? "")),
                "is" or "where" => new IsWhereSelector(name, Parse(arg ?? "")),
                "has" => new HasSelector(arg ?? ""),
                "nth-child" => new NthChildSelector(arg, false),
                "nth-last-child" => new NthChildSelector(arg, true),
                "nth-of-type" => new NthOfTypeSelector(arg, false),
                "nth-last-of-type" => new NthOfTypeSelector(arg, true),
                "first-child" => new NthChildSelector("1", false),
                "last-child" => new NthChildSelector("1", true),
                "first-of-type" => new NthOfTypeSelector("1", false),
                "last-of-type" => new NthOfTypeSelector("1", true),
                "only-child" => new OnlyChildSelector(false),
                "only-of-type" => new OnlyChildSelector(true),
                "root" => new RootSelector(),
                "empty" => new EmptySelector(),
                "host" => new HostSelector(arg),
                "link" or "visited" or "hover" or "active" or "focus" or
                "focus-visible" or "focus-within" or "target" or
                "enabled" or "disabled" or "checked" or "indeterminate" or
                "required" or "optional" or "valid" or "invalid" or
                "in-range" or "out-of-range" or "read-only" or "read-write" or
                "default" or "defined" or "picture-in-picture" => new StatePseudoClassSelector(name),
                _ => throw new DomException("SyntaxError", $"Unknown pseudo-class :{name}")
            };

            if (arg != null && normalizedName is not (
                    "not" or "is" or "where" or "has" or "nth-child" or
                    "nth-last-child" or "nth-of-type" or "nth-last-of-type" or "host"))
            {
                throw new DomException("SyntaxError", $"Pseudo-class :{name} does not accept arguments");
            }

            return selector;
        }
    }

    // --- Token Types ---

    internal enum TokenType
    {
        Whitespace,
        Comma,
        ChildCombinator,
        AdjacentSiblingCombinator,
        GeneralSiblingCombinator,
        TypeSelector,
        UniversalSelector,
        IdSelector,
        ClassSelector,
        AttributeSelector,
        PseudoClass,
        PseudoElement
    }

    public enum AttributeMatchType
    {
        Exists,     // [attr]
        Equals,     // [attr=value]
        Includes,   // [attr~=value]
        DashMatch,  // [attr|=value]
        Prefix,     // [attr^=value]
        Suffix,     // [attr$=value]
        Substring   // [attr*=value]
    }

    internal readonly struct Token
    {
        public readonly TokenType Type;
        public readonly string Value;
        public readonly string FunctionArg;
        public readonly string AttrValue;
        public readonly AttributeMatchType MatchType;
        public readonly bool CaseInsensitive;

        public Token(TokenType type, string value, string functionArg = null)
        {
            Type = type;
            Value = value;
            FunctionArg = functionArg;
            AttrValue = null;
            MatchType = AttributeMatchType.Exists;
            CaseInsensitive = false;
        }

        public Token(TokenType type, string attrName, string attrValue, AttributeMatchType matchType, bool caseInsensitive = false)
        {
            Type = type;
            Value = attrName;
            AttrValue = attrValue;
            MatchType = matchType;
            CaseInsensitive = caseInsensitive;
            FunctionArg = null;
        }
    }

    /// <summary>
    /// Combinator type between compound selectors.
    /// </summary>
    public enum Combinator
    {
        None,               // End of chain
        Descendant,         // Space (whitespace)
        Child,              // >
        AdjacentSibling,    // +
        GeneralSibling      // ~
    }
}
