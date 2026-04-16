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

        // --- Tokenizer ---

        private static List<Token> Tokenize(string input)
        {
            var tokens = new List<Token>();
            int i = 0;

            while (i < input.Length)
            {
                char c = input[i];

                // Whitespace
                if (char.IsWhiteSpace(c))
                {
                    while (i < input.Length && char.IsWhiteSpace(input[i]))
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

                // Universal selector
                if (c == '*')
                {
                    tokens.Add(new Token(TokenType.UniversalSelector, "*"));
                    i++;
                    continue;
                }

                // Type selector (element name)
                if (IsIdentStart(c))
                {
                    var ident = ReadIdent(input, ref i);
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

        private static Token ReadAttributeSelector(string input, ref int i)
        {
            // Read attribute name
            SkipWhitespace(input, ref i);
            var attrName = ReadIdent(input, ref i);
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
            while (i < input.Length && input[i] != quote)
            {
                if (input[i] == '\\' && i + 1 < input.Length)
                {
                    i++;
                    sb.Append(input[i]);
                }
                else
                {
                    sb.Append(input[i]);
                }
                i++;
            }
            if (i < input.Length) i++; // Skip closing quote
            return sb.ToString();
        }

        private static string ReadFunctionArg(string input, ref int i)
        {
            int depth = 1;
            var sb = new StringBuilder();
            while (i < input.Length && depth > 0)
            {
                if (input[i] == '(') depth++;
                else if (input[i] == ')') depth--;

                if (depth > 0)
                {
                    sb.Append(input[i]);
                }
                i++;
            }
            return sb.ToString().Trim();
        }

        private static void SkipWhitespace(string input, ref int i)
        {
            while (i < input.Length && char.IsWhiteSpace(input[i]))
                i++;
        }

        private static bool IsIdentStart(char c)
        {
            return char.IsLetter(c) || c == '_' || c == '-' || c == '\\' || c > 127;
        }

        private static bool IsIdentChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '\\' || c > 127;
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
                    escaped = char.ConvertFromUtf32(Math.Clamp(codePoint, 0, 0x10FFFF));
                else
                    escaped = hex;

                if (i < input.Length && char.IsWhiteSpace(input[i]))
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

        // --- Parser ---

        private static List<SelectorChain> ParseSelectorList(List<Token> tokens)
        {
            var chains = new List<SelectorChain>();
            var currentCompound = new List<SimpleSelector>();
            var currentChain = new List<(List<SimpleSelector> Compound, Combinator Combinator)>();
            Combinator pendingCombinator = Combinator.None;

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];

                switch (token.Type)
                {
                    case TokenType.Comma:
                        // End of selector
                        if (currentCompound.Count > 0)
                        {
                            currentChain.Add((currentCompound, Combinator.None));
                            currentCompound = new List<SimpleSelector>();
                        }
                        if (currentChain.Count > 0)
                        {
                            chains.Add(new SelectorChain(currentChain));
                            currentChain = new List<(List<SimpleSelector>, Combinator)>();
                        }
                        pendingCombinator = Combinator.None;
                        break;

                    case TokenType.Whitespace:
                        // Potential descendant combinator
                        if (currentCompound.Count > 0)
                        {
                            pendingCombinator = Combinator.Descendant;
                        }
                        break;

                    case TokenType.ChildCombinator:
                        if (currentCompound.Count > 0)
                        {
                            currentChain.Add((currentCompound, Combinator.Child));
                            currentCompound = new List<SimpleSelector>();
                        }
                        pendingCombinator = Combinator.None;
                        break;

                    case TokenType.AdjacentSiblingCombinator:
                        if (currentCompound.Count > 0)
                        {
                            currentChain.Add((currentCompound, Combinator.AdjacentSibling));
                            currentCompound = new List<SimpleSelector>();
                        }
                        pendingCombinator = Combinator.None;
                        break;

                    case TokenType.GeneralSiblingCombinator:
                        if (currentCompound.Count > 0)
                        {
                            currentChain.Add((currentCompound, Combinator.GeneralSibling));
                            currentCompound = new List<SimpleSelector>();
                        }
                        pendingCombinator = Combinator.None;
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
                        break;
                }
            }

            // Handle remaining tokens
            if (currentCompound.Count > 0)
            {
                currentChain.Add((currentCompound, Combinator.None));
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
                TokenType.TypeSelector => new TypeSelector(token.Value.ToUpperInvariant()),
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
            return name.ToLowerInvariant() switch
            {
                "not" => new NegationSelector(Parse(arg ?? "")),
                "is" or "where" => new IsWhereSelector(name, Parse(arg ?? "")),
                "has" => new HasSelector(Parse(arg ?? "")),
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
                "default" or "defined" => new StatePseudoClassSelector(name),
                _ => new StatePseudoClassSelector(name) // Fallback
            };
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
