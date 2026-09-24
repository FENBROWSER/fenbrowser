// SpecRef: CSSOM 6.4 (CSS rules), CSS Cascade 5 §6.3 (@import), CSS Namespaces 3 §3,
//          CSS Conditional 3 (@supports), CSS Animations 1 §3 (@keyframes),
//          CSS Paged Media 3 (@page), CSS Counter Styles 3 (@counter-style)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// The at-rules the cascade resolves from the stylesheet text before it parses
    /// (@import, @supports, @container, @keyframes) or does not apply at all
    /// (@namespace, @page, @counter-style). CSSOM still has to report each of them in
    /// cssRules, so with <see cref="PreserveCssomRules"/> set the parser keeps them.
    /// </summary>
    public partial class CssSyntaxParser
    {
        /// <summary>Keep every at-rule CSSOM exposes instead of dropping the ones the cascade pre-resolves.</summary>
        public bool PreserveCssomRules { get; set; }

        private bool _cssomSeenNamespace;
        private bool _cssomSeenOtherRule;

        /// <summary>
        /// CSS Cascade 5 §6.3 / CSS Namespaces 3 §3: @import may only follow @charset
        /// and @layer statements, and @namespace only those and @import. A misplaced
        /// one is invalid and dropped.
        /// </summary>
        private bool AcceptTopLevelCssomRule(CssRule rule)
        {
            switch (rule)
            {
                case null:
                    return true;
                case CssImportRule:
                    return !_cssomSeenNamespace && !_cssomSeenOtherRule;
                case CssNamespaceRule:
                    if (_cssomSeenOtherRule) return false;
                    _cssomSeenNamespace = true;
                    return true;
                case CssLayerRule { IsStatement: true } when !_cssomSeenNamespace:
                    return true;
                default:
                    _cssomSeenOtherRule = true;
                    return true;
            }
        }

        private bool TryConsumeCssomAtRule(string name, int nestingDepth, out CssRule rule)
        {
            rule = null;
            switch (name.ToLowerInvariant())
            {
                case "import":
                    rule = ConsumeStatementAtRule(nestingDepth, ParseImportPrelude);
                    return true;
                case "namespace":
                    rule = ConsumeStatementAtRule(nestingDepth, ParseNamespacePrelude);
                    return true;
                case "supports":
                case "container":
                {
                    var prelude = ConsumeAtRulePrelude();
                    if (_currentToken.Type != CssTokenType.LeftBrace)
                    {
                        SkipAtRuleRemainder();
                        return true;
                    }

                    ConsumeToken(); // {
                    var conditional = CreateCssomConditionRule(name, TokensToText(prelude), out var rules);
                    ParseInsideBlock(rules, nestingDepth + 1);
                    if (_currentToken.Type == CssTokenType.RightBrace) ConsumeToken();
                    rule = conditional;
                    return true;
                }
                case "keyframes":
                case "-webkit-keyframes":
                    rule = ConsumeKeyframesRule();
                    return true;
                case "page":
                {
                    var prelude = ConsumeAtRulePrelude();
                    if (_currentToken.Type != CssTokenType.LeftBrace)
                    {
                        SkipAtRuleRemainder();
                        return true;
                    }

                    var page = new CssPageRule { Selector = TokensToText(prelude) };
                    page.Declarations.AddRange(ConsumeDeclarationBlock());
                    rule = page;
                    return true;
                }
                case "counter-style":
                {
                    var prelude = ConsumeAtRulePrelude();
                    var nameToken = prelude.Where(t => t.Type != CssTokenType.Whitespace).ToList();
                    if (_currentToken.Type != CssTokenType.LeftBrace || nameToken.Count != 1 || nameToken[0].Type != CssTokenType.Ident)
                    {
                        SkipAtRuleRemainder();
                        return true;
                    }

                    var counterStyle = new CssCounterStyleRule { Name = nameToken[0].Value };
                    counterStyle.Declarations.AddRange(ConsumeDeclarationBlock());
                    rule = counterStyle;
                    return true;
                }
                case "font-feature-values":
                    rule = ConsumeFontFeatureValuesRule();
                    return true;
                default:
                    return false;
            }
        }

        // CSS Fonts 4 §6.9: @font-feature-values <family-name># { <feature-value-block>* }
        private CssRule ConsumeFontFeatureValuesRule()
        {
            var family = TokensToText(ConsumeAtRulePrelude());
            if (_currentToken.Type != CssTokenType.LeftBrace || family.Length == 0)
            {
                SkipAtRuleRemainder();
                return null;
            }

            var rule = new CssFontFeatureValuesRule { FontFamily = family };
            ConsumeToken(); // {
            while (_currentToken.Type != CssTokenType.RightBrace && _currentToken.Type != CssTokenType.EOF)
            {
                if (_currentToken.Type != CssTokenType.AtKeyword)
                {
                    ConsumeComponentValue();
                    continue;
                }

                var blockName = _currentToken.Value.ToLowerInvariant();
                ConsumeToken();
                ConsumeAtRulePrelude();
                if (_currentToken.Type != CssTokenType.LeftBrace)
                {
                    SkipAtRuleRemainder();
                    continue;
                }

                var declarations = ConsumeDeclarationBlock();
                if (blockName is "annotation" or "ornaments" or "stylistic" or "swash" or "character-variant" or "styleset")
                {
                    rule.Blocks.Add(new KeyValuePair<string, List<CssDeclaration>>(blockName, declarations));
                }
            }

            if (_currentToken.Type == CssTokenType.RightBrace) ConsumeToken();
            return rule;
        }

        private CssRule CreateCssomConditionRule(string name, string condition, out List<CssRule> rules)
        {
            if (name.Equals("container", StringComparison.OrdinalIgnoreCase))
            {
                var container = new CssContainerRule { Condition = condition };
                rules = container.Rules;
                return container;
            }

            var supports = new CssSupportsRule { Condition = condition };
            rules = supports.Rules;
            return supports;
        }

        // @import and @namespace are statements: a block makes them invalid, and
        // neither is allowed inside another rule.
        private CssRule ConsumeStatementAtRule(int nestingDepth, Func<List<CssToken>, CssRule> parsePrelude)
        {
            var prelude = ConsumeAtRulePrelude();
            if (_currentToken.Type != CssTokenType.Semicolon && _currentToken.Type != CssTokenType.EOF)
            {
                SkipAtRuleRemainder();
                return null;
            }

            if (_currentToken.Type == CssTokenType.Semicolon) ConsumeToken();
            return nestingDepth > 0 ? null : parsePrelude(prelude);
        }

        // @import [ <url> | <string> ] [ layer | layer(<layer-name>) ]? [ supports(...) ]? <media-query-list>?
        private static CssRule ParseImportPrelude(List<CssToken> prelude)
        {
            int i = 0;
            if (!TryReadUrl(prelude, ref i, out var href)) return null;

            var rule = new CssImportRule { Href = href };
            SkipWhitespace(prelude, ref i);
            if (i < prelude.Count && prelude[i].Type == CssTokenType.Ident &&
                prelude[i].Value.Equals("layer", StringComparison.OrdinalIgnoreCase))
            {
                rule.ImportLayerName = string.Empty;
                i++;
            }
            else if (i < prelude.Count && IsFunction(prelude[i], "layer"))
            {
                rule.ImportLayerName = TokensToText(ReadFunctionArguments(prelude, ref i));
            }

            SkipWhitespace(prelude, ref i);
            if (i < prelude.Count && IsFunction(prelude[i], "supports"))
            {
                rule.SupportsText = TokensToText(ReadFunctionArguments(prelude, ref i));
            }

            rule.MediaText = TokensToText(prelude.Skip(i));
            return rule;
        }

        // @namespace <namespace-prefix>? [ <string> | <url> ]
        private static CssRule ParseNamespacePrelude(List<CssToken> prelude)
        {
            int i = 0;
            SkipWhitespace(prelude, ref i);
            string prefix = string.Empty;
            if (i < prelude.Count && prelude[i].Type == CssTokenType.Ident)
            {
                prefix = prelude[i].Value;
                i++;
            }

            if (!TryReadUrl(prelude, ref i, out var uri)) return null;
            SkipWhitespace(prelude, ref i);
            return i == prelude.Count ? new CssNamespaceRule { Prefix = prefix, NamespaceUri = uri } : null;
        }

        private CssRule ConsumeKeyframesRule()
        {
            var prelude = ConsumeAtRulePrelude().Where(t => t.Type != CssTokenType.Whitespace).ToList();
            if (_currentToken.Type != CssTokenType.LeftBrace ||
                prelude.Count != 1 ||
                (prelude[0].Type != CssTokenType.Ident && prelude[0].Type != CssTokenType.String))
            {
                SkipAtRuleRemainder();
                return null;
            }

            var keyframes = new CssKeyframesRule { Name = prelude[0].Value };
            ConsumeToken(); // {
            while (_currentToken.Type != CssTokenType.RightBrace && _currentToken.Type != CssTokenType.EOF)
            {
                if (_currentToken.Type == CssTokenType.Whitespace || _currentToken.Type == CssTokenType.Semicolon)
                {
                    ConsumeToken();
                    continue;
                }

                var selector = new List<CssToken>();
                while (_currentToken.Type != CssTokenType.LeftBrace &&
                       _currentToken.Type != CssTokenType.RightBrace &&
                       _currentToken.Type != CssTokenType.EOF)
                {
                    selector.Add(_currentToken);
                    ConsumeToken();
                }

                if (_currentToken.Type != CssTokenType.LeftBrace) break;
                var declarations = ConsumeDeclarationBlock();
                var keyText = NormalizeKeyframeSelector(selector);
                if (keyText == null) continue;

                var keyframe = new CssKeyframeRule { KeyText = keyText };
                keyframe.Declarations.AddRange(declarations);
                keyframes.Keyframes.Add(keyframe);
            }

            if (_currentToken.Type == CssTokenType.RightBrace) ConsumeToken();
            return keyframes;
        }

        /// <summary>
        /// CSS Animations 1 §3: a keyframe selector is a comma list of from, to and
        /// percentages; CSSOM serialises from as 0% and to as 100%. Null if invalid.
        /// </summary>
        internal static string NormalizeKeyframeSelector(IEnumerable<CssToken> tokens)
        {
            var parts = new List<string>();
            bool expectValue = true;
            foreach (var token in tokens)
            {
                if (token.Type == CssTokenType.Whitespace || token.Type == CssTokenType.Comment) continue;
                if (token.Type == CssTokenType.Comma)
                {
                    if (expectValue) return null;
                    expectValue = true;
                    continue;
                }

                if (!expectValue) return null;
                if (token.Type == CssTokenType.Ident && token.Value.Equals("from", StringComparison.OrdinalIgnoreCase)) parts.Add("0%");
                else if (token.Type == CssTokenType.Ident && token.Value.Equals("to", StringComparison.OrdinalIgnoreCase)) parts.Add("100%");
                else if (token.Type == CssTokenType.Percentage && token.NumericValue >= 0 && token.NumericValue <= 100)
                    parts.Add(token.NumericValue.ToString(CultureInfo.InvariantCulture) + "%");
                else return null;
                expectValue = false;
            }

            return parts.Count == 0 || expectValue ? null : string.Join(", ", parts);
        }

        private List<CssToken> ConsumeAtRulePrelude()
        {
            var prelude = new List<CssToken>();
            while (_currentToken.Type != CssTokenType.LeftBrace &&
                   _currentToken.Type != CssTokenType.Semicolon &&
                   _currentToken.Type != CssTokenType.EOF)
            {
                if (_currentToken.Type != CssTokenType.Comment) prelude.Add(_currentToken);
                ConsumeToken();
            }

            return prelude;
        }

        private void SkipAtRuleRemainder()
        {
            if (_currentToken.Type == CssTokenType.LeftBrace) ConsumeSimpleBlock();
            else if (_currentToken.Type == CssTokenType.Semicolon) ConsumeToken();
        }

        private static bool TryReadUrl(List<CssToken> tokens, ref int i, out string url)
        {
            url = null;
            SkipWhitespace(tokens, ref i);
            if (i >= tokens.Count) return false;

            var token = tokens[i];
            if (token.Type == CssTokenType.Url || token.Type == CssTokenType.String)
            {
                url = token.Value ?? string.Empty;
                i++;
                return true;
            }

            if (IsFunction(token, "url"))
            {
                var args = ReadFunctionArguments(tokens, ref i).Where(t => t.Type != CssTokenType.Whitespace).ToList();
                if (args.Count == 1 && args[0].Type == CssTokenType.String)
                {
                    url = args[0].Value ?? string.Empty;
                    return true;
                }
            }

            return false;
        }

        private static bool IsFunction(CssToken token, string name) =>
            token.Type == CssTokenType.Function && string.Equals(token.Value, name, StringComparison.OrdinalIgnoreCase);

        // Reads a function's arguments up to its matching ")" and leaves i after it.
        private static List<CssToken> ReadFunctionArguments(List<CssToken> tokens, ref int i)
        {
            var args = new List<CssToken>();
            int depth = 1;
            for (i++; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Type == CssTokenType.Function || token.Type == CssTokenType.LeftParen) depth++;
                else if (token.Type == CssTokenType.RightParen && --depth == 0)
                {
                    i++;
                    break;
                }

                args.Add(token);
            }

            return args;
        }

        private static void SkipWhitespace(List<CssToken> tokens, ref int i)
        {
            while (i < tokens.Count && tokens[i].Type == CssTokenType.Whitespace) i++;
        }

        // Prelude text with whitespace runs collapsed to one space.
        private static string TokensToText(IEnumerable<CssToken> tokens)
        {
            var builder = new StringBuilder();
            bool pendingSpace = false;
            foreach (var token in tokens)
            {
                if (token.Type == CssTokenType.Whitespace)
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace) builder.Append(' ');
                pendingSpace = false;
                builder.Append(token.Type == CssTokenType.Number
                    ? token.NumericValue.ToString(CultureInfo.InvariantCulture)
                    : token.ToStringValue());
            }

            return builder.ToString();
        }
    }
}
