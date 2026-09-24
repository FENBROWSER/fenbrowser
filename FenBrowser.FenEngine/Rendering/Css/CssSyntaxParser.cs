// SpecRef: CSS Syntax Module Level 3, parser error handling and recovery
// CapabilityId: CSS-PARSER-RECOVERY-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
using System;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.FenEngine.Rendering.Css
{
    public class CssSyntaxParser
    {
        private readonly CssTokenizer _tokenizer;
        private CssToken _currentToken;
        private int _ruleCount = 0; // For stable sorting
        private int _emittedRuleCount;
        private bool _ruleLimitLogged;
        private bool _declarationLimitLogged;
        private bool _nestingLimitLogged;

        public int MaxRules { get; set; } = 200000;
        public int MaxDeclarationsPerBlock { get; set; } = 8192;
        public int MaxRuleNestingDepth { get; set; } = 256;

        public CssSyntaxParser(CssTokenizer tokenizer)
        {
            _tokenizer = tokenizer;
        }

        public CssStylesheet ParseStylesheet()
        {
            var sheet = new CssStylesheet();
            ConsumeToken(); // Prime
            ConsumeWhitespace();
            
            int loopCount = 0;
            while (_currentToken.Type != CssTokenType.EOF)
            {
                if (loopCount++ > 1000000) // Safety break
                {
                    FenBrowser.Core.EngineLogCompat.Warn("[CssSyntaxParser] ParseStylesheet infinite loop detected. Aborting.", FenBrowser.Core.Logging.LogCategory.CSS);
                    break;
                }

                if (_currentToken.Type == CssTokenType.CDO || _currentToken.Type == CssTokenType.CDC)
                {
                    ConsumeToken();
                    continue;
                }

                if (_currentToken.Type == CssTokenType.AtKeyword)
                {
                    var rule = ConsumeAtRule(0);
                    if (!TryAddRule(sheet.Rules, rule)) break;
                    ConsumeWhitespace();
                    continue;
                }

                // Qualified Rule (Style Rule)
                var qRule = ConsumeQualifiedRule(null, 0);
                if (qRule != null && !TryAddRule(sheet.Rules, qRule))
                {
                    break;
                }
                ConsumeWhitespace();
            }
            return sheet;
        }

        private CssRule ConsumeAtRule(int nestingDepth)
        {
            string name = _currentToken.Value;
            ConsumeToken(); // skip @name

            if (name.Equals("media", StringComparison.OrdinalIgnoreCase))
            {
                var mediaRule = new CssMediaRule();
                var conditionTokens = new List<CssToken>();
                while (_currentToken.Type != CssTokenType.LeftBrace && _currentToken.Type != CssTokenType.Semicolon && _currentToken.Type != CssTokenType.EOF)
                {
                    conditionTokens.Add(_currentToken);
                    ConsumeToken();
                }
                mediaRule.Condition = string.Join("", conditionTokens.Select(t => t.ToStringValue())).Trim();

                if (_currentToken.Type == CssTokenType.LeftBrace)
                {
                    ConsumeToken(); // {
                    ParseInsideBlock(mediaRule.Rules, nestingDepth + 1);
                    if (_currentToken.Type == CssTokenType.RightBrace)
                    {
                        ConsumeToken(); // }
                    }
                }
                return mediaRule;
            }
            
            if (name.Equals("layer", StringComparison.OrdinalIgnoreCase))
            {
                var nameTokens = new List<CssToken>();
                while (_currentToken.Type != CssTokenType.LeftBrace && _currentToken.Type != CssTokenType.Semicolon && _currentToken.Type != CssTokenType.EOF)
                {
                    nameTokens.Add(_currentToken);
                    ConsumeToken();
                }
                
                string rawNames = string.Join("", nameTokens.Select(t => t.ToStringValue())).Trim();
                var names = rawNames.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()).ToList();

                if (_currentToken.Type == CssTokenType.LeftBrace)
                {
                    ConsumeToken();
                    var layerRule = new CssLayerRule { Name = names.FirstOrDefault() };
                    ParseInsideBlock(layerRule.Rules, nestingDepth + 1);
                    if (_currentToken.Type == CssTokenType.RightBrace) ConsumeToken();
                    return layerRule;
                }
                else if (_currentToken.Type == CssTokenType.Semicolon)
                {
                    ConsumeToken();
                    return new CssLayerRule { Name = rawNames, Rules = { } }; 
                }
                return null;
            }
            
            if (name.Equals("scope", StringComparison.OrdinalIgnoreCase))
            {
                var scopeRule = new CssScopeRule();
                var selectorTokens = new List<CssToken>();
                while (_currentToken.Type != CssTokenType.LeftBrace && _currentToken.Type != CssTokenType.EOF)
                {
                    selectorTokens.Add(_currentToken);
                    ConsumeToken();
                }
                
                string raw = string.Join("", selectorTokens.Select(t => t.ToStringValue())).Trim();
                if (raw.Contains(" to "))
                {
                    var parts = raw.Split(new[] { " to " }, 2, StringSplitOptions.None);
                    scopeRule.ScopeSelector = parts[0].Trim('(', ')', ' ');
                    scopeRule.EndSelector = parts[1].Trim('(', ')', ' ');
                }
                else
                {
                    scopeRule.ScopeSelector = raw.Trim('(', ')', ' ');
                }

                if (_currentToken.Type == CssTokenType.LeftBrace)
                {
                    ConsumeToken();
                    ParseInsideBlock(scopeRule.Rules, nestingDepth + 1);
                    if (_currentToken.Type == CssTokenType.RightBrace) ConsumeToken();
                }
                return scopeRule;
            }

            if (name.Equals("font-face", StringComparison.OrdinalIgnoreCase))
            {
                var fontFaceRule = new CssFontFaceRule();

                while (_currentToken.Type != CssTokenType.LeftBrace &&
                       _currentToken.Type != CssTokenType.Semicolon &&
                       _currentToken.Type != CssTokenType.EOF)
                {
                    ConsumeToken();
                }

                if (_currentToken.Type == CssTokenType.LeftBrace)
                {
                    fontFaceRule.Declarations.AddRange(ConsumeDeclarationBlock());
                    return fontFaceRule;
                }

                if (_currentToken.Type == CssTokenType.Semicolon)
                {
                    ConsumeToken();
                }

                return null;
            }

            if (name.Equals("property", StringComparison.OrdinalIgnoreCase))
            {
                var nameTokens = new List<CssToken>();
                while (_currentToken.Type != CssTokenType.LeftBrace &&
                       _currentToken.Type != CssTokenType.Semicolon &&
                       _currentToken.Type != CssTokenType.EOF)
                {
                    nameTokens.Add(_currentToken);
                    ConsumeToken();
                }

                var propertyRule = new CssPropertyRule
                {
                    Name = string.Join("", nameTokens.Select(t => t.ToStringValue())).Trim()
                };

                if (_currentToken.Type == CssTokenType.LeftBrace)
                {
                    propertyRule.Declarations.AddRange(ConsumeDeclarationBlock());
                    return propertyRule;
                }

                if (_currentToken.Type == CssTokenType.Semicolon)
                {
                    ConsumeToken();
                }

                return null;
            }

            // Unknown @rule, consume until semicolon or block
            while (_currentToken.Type != CssTokenType.Semicolon && _currentToken.Type != CssTokenType.LeftBrace && _currentToken.Type != CssTokenType.EOF)
            {
                ConsumeToken();
            }

            if (_currentToken.Type == CssTokenType.LeftBrace)
            {
                ConsumeSimpleBlock();
            }
            else if (_currentToken.Type == CssTokenType.Semicolon)
            {
                ConsumeToken();
            }
            
            return null;
        }

        private void ParseInsideBlock(List<CssRule> rules, int nestingDepth)
        {
            if (nestingDepth > MaxRuleNestingDepth)
            {
                SkipCurrentBlockContentsAtNestingLimit();
                return;
            }

            int loopCount = 0;
            while (_currentToken.Type != CssTokenType.RightBrace && _currentToken.Type != CssTokenType.EOF)
            {
                if (loopCount++ > 100000) break;
                if (_currentToken.Type == CssTokenType.Whitespace || _currentToken.Type == CssTokenType.Comment)
                {
                    ConsumeToken();
                    continue;
                }

                if (_currentToken.Type == CssTokenType.AtKeyword)
                {
                    var subRule = ConsumeAtRule(nestingDepth);
                    if (!TryAddRule(rules, subRule)) return;
                }
                else
                {
                    var subRule = ConsumeQualifiedRule(null, nestingDepth);
                    if (!TryAddRule(rules, subRule)) return;
                }
            }
        }

        private CssStyleRule ConsumeQualifiedRule(CssSelector parentSelector = null, int nestingDepth = 0)
        {
            var rule = new CssStyleRule();
            rule.Order = _ruleCount++;

            var selectorTokens = new List<CssToken>();
            int selLoop = 0;
            while (_currentToken.Type != CssTokenType.LeftBrace && _currentToken.Type != CssTokenType.EOF)
            {
                if (selLoop++ > 100000) break;
                selectorTokens.Add(_currentToken);
                ConsumeToken();
            }

            if (_currentToken.Type == CssTokenType.EOF) return null;

            if (parentSelector == null)
            {
                int firstIdx = selectorTokens.FindIndex(t => t.Type != CssTokenType.Whitespace);
                if (firstIdx >= 0 && selectorTokens[firstIdx].Type == CssTokenType.Ident && selectorTokens[firstIdx].Value != null && selectorTokens[firstIdx].Value.StartsWith("--"))
                {
                    int nextIdx = selectorTokens.FindIndex(firstIdx + 1, t => t.Type != CssTokenType.Whitespace);
                    if (nextIdx >= 0 && selectorTokens[nextIdx].Type == CssTokenType.Colon)
                    {
                        ConsumeDeclarationBlock(null);
                        return null;
                    }
                }
            }

            rule.Selector = ParseSelector(selectorTokens, parentSelector);
            if (rule.Selector == null)
            {
                 ConsumeDeclarationBlock(null);
                 return null; 
            }

            ConsumeStyleRuleBlock(rule, nestingDepth);
            return rule;
        }

        private void ConsumeStyleRuleBlock(CssStyleRule rule, int nestingDepth)
        {
            ConsumeToken(); // {
            int declarationCount = 0;

            while (_currentToken.Type != CssTokenType.RightBrace && _currentToken.Type != CssTokenType.EOF)
            {
                if (_currentToken.Type == CssTokenType.Whitespace || _currentToken.Type == CssTokenType.Semicolon)
                {
                    ConsumeToken();
                    continue;
                }

                if (IsUnambiguousNestedRuleStart(_currentToken))
                {
                    if (nestingDepth >= MaxRuleNestingDepth)
                    {
                        ConsumeNestedRuleAtNestingLimit();
                        continue;
                    }

                    if (_currentToken.Type == CssTokenType.AtKeyword)
                    {
                        var nestedAtRule = ConsumeNestedAtRule(rule.Selector, nestingDepth + 1);
                        if (nestedAtRule != null)
                        {
                            rule.NestedRules.Add(nestedAtRule);
                        }
                    }
                    else
                    {
                        var nestedRule = ConsumeQualifiedRule(rule.Selector, nestingDepth + 1);
                        if (nestedRule != null)
                        {
                            rule.NestedRules.Add(nestedRule);
                        }
                    }
                    continue;
                }

                if (_currentToken.Type == CssTokenType.Ident)
                {
                    var handledNestedRule = ConsumeDeclarationOrNestedRule(rule, nestingDepth);
                    if (handledNestedRule) continue;
                }

                if (declarationCount >= MaxDeclarationsPerBlock)
                {
                    if (!_declarationLimitLogged)
                    {
                        _declarationLimitLogged = true;
                        FenBrowser.Core.EngineLogCompat.Warn($"[CssSyntaxParser] Declaration block limit reached ({MaxDeclarationsPerBlock}). Remaining declarations were skipped.", FenBrowser.Core.Logging.LogCategory.CSS);
                    }
                    while (_currentToken.Type != CssTokenType.RightBrace && _currentToken.Type != CssTokenType.EOF)
                    {
                        ConsumeComponentValue();
                    }
                    break;
                }

                var decl = ConsumeDeclaration();
                if (decl != null)
                {
                    rule.Declarations.Add(decl);
                    declarationCount++;
                }
            }

            if (_currentToken.Type == CssTokenType.RightBrace)
            {
                ConsumeToken(); // }
            }
        }

        private bool ConsumeDeclarationOrNestedRule(CssStyleRule rule, int nestingDepth)
        {
            var savedPosition = _tokenizer.SavePosition();
            var savedToken = _currentToken;

            ConsumeToken();
            while (_currentToken.Type == CssTokenType.Whitespace)
                ConsumeToken();

            bool isDeclaration = _currentToken.Type == CssTokenType.Colon;

            _tokenizer.RestorePosition(savedPosition);
            _currentToken = savedToken;

            if (isDeclaration)
            {
                return false;
            }

            if (nestingDepth >= MaxRuleNestingDepth)
            {
                ConsumeNestedRuleAtNestingLimit();
                return true;
            }

            var nestedRule = ConsumeQualifiedRule(rule.Selector, nestingDepth + 1);
            if (nestedRule != null)
            {
                rule.NestedRules.Add(nestedRule);
            }
            return true;
        }

        private static bool IsUnambiguousNestedRuleStart(CssToken token)
        {
            switch (token.Type)
            {
                case CssTokenType.Delim when token.Delimiter == '.' || token.Delimiter == '#' || 
                                             token.Delimiter == ':' || token.Delimiter == '[' ||
                                             token.Delimiter == '*' || token.Delimiter == '&' ||
                                             token.Delimiter == '>' || token.Delimiter == '+' || 
                                             token.Delimiter == '~':
                case CssTokenType.Hash:
                case CssTokenType.LeftBracket:
                case CssTokenType.Colon:
                case CssTokenType.AtKeyword:
                    return true;
                default:
                    return false;
            }
        }

        private CssRule ConsumeNestedAtRule(CssSelector parentSelector, int nestingDepth)
        {
            string name = _currentToken.Value;
            ConsumeToken(); // skip @name

            if (name.Equals("media", StringComparison.OrdinalIgnoreCase))
            {
                var conditionTokens = new List<CssToken>();
                while (_currentToken.Type != CssTokenType.LeftBrace && _currentToken.Type != CssTokenType.Semicolon && _currentToken.Type != CssTokenType.EOF)
                {
                    conditionTokens.Add(_currentToken);
                    ConsumeToken();
                }

                string condition = string.Join("", conditionTokens.Select(t => t.ToStringValue())).Trim();

                if (_currentToken.Type == CssTokenType.LeftBrace)
                {
                    ConsumeToken(); // {
                    var mediaRule = new CssMediaRule { Condition = condition };
                    ParseInsideBlockWithParent(mediaRule.Rules, parentSelector, nestingDepth + 1);
                    if (_currentToken.Type == CssTokenType.RightBrace)
                        ConsumeToken(); // }
                    return mediaRule;
                }
                return null;
            }

            if (name.Equals("supports", StringComparison.OrdinalIgnoreCase) || 
                name.Equals("container", StringComparison.OrdinalIgnoreCase))
            {
                var preludeTokens = new List<CssToken>();
                while (_currentToken.Type != CssTokenType.LeftBrace && _currentToken.Type != CssTokenType.Semicolon && _currentToken.Type != CssTokenType.EOF)
                {
                    preludeTokens.Add(_currentToken);
                    ConsumeToken();
                }

                string condition = string.Join("", preludeTokens.Select(t => t.ToStringValue())).Trim();

                if (_currentToken.Type == CssTokenType.LeftBrace)
                {
                    ConsumeToken(); // {
                    var atRule = new CssMediaRule { Condition = condition };
                    ParseInsideBlockWithParent(atRule.Rules, parentSelector, nestingDepth + 1);
                    if (_currentToken.Type == CssTokenType.RightBrace)
                        ConsumeToken(); // }
                    return atRule;
                }
                return null;
            }

            while (_currentToken.Type != CssTokenType.Semicolon && _currentToken.Type != CssTokenType.LeftBrace && _currentToken.Type != CssTokenType.EOF)
                ConsumeToken();

            if (_currentToken.Type == CssTokenType.LeftBrace)
                ConsumeSimpleBlock();
            else if (_currentToken.Type == CssTokenType.Semicolon)
                ConsumeToken();

            return null;
        }

        private void ParseInsideBlockWithParent(List<CssRule> rules, CssSelector parentSelector, int nestingDepth)
        {
            if (nestingDepth > MaxRuleNestingDepth)
            {
                SkipCurrentBlockContentsAtNestingLimit();
                return;
            }

            int loopCount = 0;
            while (_currentToken.Type != CssTokenType.RightBrace && _currentToken.Type != CssTokenType.EOF)
            {
                if (loopCount++ > 100000) break;
                if (_currentToken.Type == CssTokenType.Whitespace || _currentToken.Type == CssTokenType.Comment)
                {
                    ConsumeToken();
                    continue;
                }

                if (_currentToken.Type == CssTokenType.AtKeyword)
                {
                    var nestedName = _currentToken.Value;
                    CssRule subRule;
                    if (nestedName.Equals("media", StringComparison.OrdinalIgnoreCase) ||
                        nestedName.Equals("supports", StringComparison.OrdinalIgnoreCase) ||
                        nestedName.Equals("container", StringComparison.OrdinalIgnoreCase))
                    {
                        subRule = ConsumeNestedAtRule(parentSelector, nestingDepth);
                    }
                    else
                    {
                        subRule = ConsumeAtRule(nestingDepth);
                    }
                    if (!TryAddRule(rules, subRule)) return;
                }
                else
                {
                    var subRule = ConsumeQualifiedRule(parentSelector, nestingDepth);
                    if (!TryAddRule(rules, subRule)) return;
                }
            }
        }

        private void ConsumeNestedRuleAtNestingLimit()
        {
            LogNestingLimitOnce();

            if (_currentToken.Type == CssTokenType.AtKeyword)
            {
                ConsumeToken();
                while (_currentToken.Type != CssTokenType.Semicolon &&
                       _currentToken.Type != CssTokenType.LeftBrace &&
                       _currentToken.Type != CssTokenType.EOF)
                {
                    ConsumeComponentValue();
                }

                if (_currentToken.Type == CssTokenType.LeftBrace)
                {
                    ConsumeSimpleBlock();
                }
                else if (_currentToken.Type == CssTokenType.Semicolon)
                {
                    ConsumeToken();
                }
                return;
            }

            while (_currentToken.Type != CssTokenType.LeftBrace &&
                   _currentToken.Type != CssTokenType.RightBrace &&
                   _currentToken.Type != CssTokenType.EOF)
            {
                ConsumeComponentValue();
            }

            if (_currentToken.Type == CssTokenType.LeftBrace)
            {
                ConsumeSimpleBlock();
            }
        }

        private void SkipCurrentBlockContentsAtNestingLimit()
        {
            LogNestingLimitOnce();
            while (_currentToken.Type != CssTokenType.RightBrace &&
                   _currentToken.Type != CssTokenType.EOF)
            {
                ConsumeComponentValue();
            }
        }

        private void LogNestingLimitOnce()
        {
            if (_nestingLimitLogged)
            {
                return;
            }

            _nestingLimitLogged = true;
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[CssSyntaxParser] Rule nesting limit reached ({MaxRuleNestingDepth}). Deeper nested rules were skipped.",
                FenBrowser.Core.Logging.LogCategory.CSS);
        }

        private List<CssDeclaration> ConsumeDeclarationBlock(CssSelector parentSelector = null)
        {
            var declarations = new List<CssDeclaration>();
            ConsumeToken(); // {
            int declarationCount = 0;

            while (_currentToken.Type != CssTokenType.RightBrace && _currentToken.Type != CssTokenType.EOF)
            {
                if (declarationCount >= MaxDeclarationsPerBlock)
                {
                    if (!_declarationLimitLogged)
                    {
                        _declarationLimitLogged = true;
                        FenBrowser.Core.EngineLogCompat.Warn($"[CssSyntaxParser] Declaration block limit reached ({MaxDeclarationsPerBlock}). Remaining declarations were skipped.", FenBrowser.Core.Logging.LogCategory.CSS);
                    }

                    while (_currentToken.Type != CssTokenType.RightBrace && _currentToken.Type != CssTokenType.EOF)
                    {
                        ConsumeComponentValue();
                    }
                    break;
                }

                if (_currentToken.Type == CssTokenType.Whitespace || _currentToken.Type == CssTokenType.Semicolon)
                {
                    ConsumeToken();
                    continue;
                }

                var decl = ConsumeDeclaration();
                if (decl != null)
                {
                    declarations.Add(decl);
                    declarationCount++;
                }
            }

            if (_currentToken.Type == CssTokenType.RightBrace)
            {
                ConsumeToken(); // }
            }

            return declarations;
        }

        private bool TryAddRule(List<CssRule> target, CssRule rule)
        {
            if (rule == null)
            {
                return true;
            }

            if (_emittedRuleCount >= MaxRules)
            {
                if (!_ruleLimitLogged)
                {
                    _ruleLimitLogged = true;
                    FenBrowser.Core.EngineLogCompat.Warn($"[CssSyntaxParser] Rule limit reached ({MaxRules}). Remaining rules were skipped.", FenBrowser.Core.Logging.LogCategory.CSS);
                }
                return false;
            }

            target.Add(rule);
            _emittedRuleCount++;
            return true;
        }

        private CssDeclaration ConsumeDeclaration()
        {
            if (_currentToken.Type != CssTokenType.Ident)
            {
                ConsumeComponentValue(); 
                return null;
            }

            string property = NormalizePropertyName(_currentToken.Value);
            ConsumeToken();
            
            while (_currentToken.Type == CssTokenType.Whitespace) ConsumeToken();

            if (_currentToken.Type != CssTokenType.Colon)
            {
                RecoverMalformedDeclaration();
                return null;
            }
            ConsumeToken(); // :

            while (_currentToken.Type == CssTokenType.Whitespace) ConsumeToken();

            var valueTokens = new List<CssToken>();
            bool important = false;
            int braceDepth = 0, parenDepth = 0, bracketDepth = 0;
            
            while (_currentToken.Type != CssTokenType.EOF)
            {
                if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                {
                    if (_currentToken.Type == CssTokenType.Semicolon || _currentToken.Type == CssTokenType.RightBrace)
                    {
                        break;
                    }
                }

                if (_currentToken.Type == CssTokenType.LeftBrace) braceDepth++;
                else if (_currentToken.Type == CssTokenType.RightBrace && braceDepth > 0) braceDepth--;
                else if (_currentToken.Type == CssTokenType.LeftParen || _currentToken.Type == CssTokenType.Function) parenDepth++;
                else if (_currentToken.Type == CssTokenType.RightParen && parenDepth > 0) parenDepth--;
                else if (_currentToken.Type == CssTokenType.LeftBracket) bracketDepth++;
                else if (_currentToken.Type == CssTokenType.RightBracket && bracketDepth > 0) bracketDepth--;

                valueTokens.Add(_currentToken);
                ConsumeToken();
            }
            
            if (valueTokens.Count >= 2)
            {
                int i = valueTokens.Count - 1;
                while (i >= 0 && valueTokens[i].Type == CssTokenType.Whitespace) i--;
                
                if (i >= 0 && valueTokens[i].Type == CssTokenType.Ident && valueTokens[i].Value.Equals("important", StringComparison.OrdinalIgnoreCase))
                {
                    int j = i - 1;
                    while (j >= 0 && valueTokens[j].Type == CssTokenType.Whitespace) j--;
                    if (j >= 0 && valueTokens[j].Type == CssTokenType.Delim && valueTokens[j].Delimiter == '!')
                    {
                        important = true;
                        valueTokens = valueTokens.Take(j).ToList();
                    }
                }
            }

            if (valueTokens.Any(t => t.Type == CssTokenType.Delim && t.Delimiter == '!'))
            {
                return null;
            }

            if (!property.StartsWith("--", StringComparison.Ordinal) && valueTokens.Any(t => t.Type == CssTokenType.LeftBrace || t.Type == CssTokenType.RightBrace))
            {
                // Standard property with {} blocks: per CSS Values and Units 4,
                // only allowed if it is a whole-value block containing var().
                int firstNonWs = valueTokens.FindIndex(t => t.Type != CssTokenType.Whitespace);
                int lastNonWs = valueTokens.FindLastIndex(t => t.Type != CssTokenType.Whitespace);
                if (firstNonWs < 0 || lastNonWs <= firstNonWs ||
                    valueTokens[firstNonWs].Type != CssTokenType.LeftBrace ||
                    valueTokens[lastNonWs].Type != CssTokenType.RightBrace)
                {
                    return null;
                }
                int depth = 0;
                bool validWholeBlock = true;
                bool hasVar = false;
                for (int k = firstNonWs; k <= lastNonWs; k++)
                {
                    var t = valueTokens[k];
                    if (t.Type == CssTokenType.LeftBrace)
                    {
                        depth++;
                    }
                    else if (t.Type == CssTokenType.RightBrace)
                    {
                        depth--;
                        if (depth == 0 && k < lastNonWs)
                        {
                            validWholeBlock = false;
                            break;
                        }
                    }
                    if (t.Type == CssTokenType.Function && string.Equals(t.Value, "var", StringComparison.OrdinalIgnoreCase))
                    {
                        hasVar = true;
                    }
                }
                if (!validWholeBlock || !hasVar || depth != 0)
                {
                    return null;
                }
            }

            if (property.StartsWith("--", StringComparison.Ordinal))
            {
                int testBrace = 0, testParen = 0, testBracket = 0;
                bool badTokens = false;
                foreach (var t in valueTokens)
                {
                    if (t.Type == CssTokenType.LeftBrace) testBrace++;
                    else if (t.Type == CssTokenType.RightBrace) { if (testBrace == 0) { badTokens = true; break; } testBrace--; }
                    else if (t.Type == CssTokenType.LeftParen || t.Type == CssTokenType.Function) testParen++;
                    else if (t.Type == CssTokenType.RightParen) { if (testParen == 0) { badTokens = true; break; } testParen--; }
                    else if (t.Type == CssTokenType.LeftBracket) testBracket++;
                    else if (t.Type == CssTokenType.RightBracket) { if (testBracket == 0) { badTokens = true; break; } testBracket--; }
                }
                if (badTokens || testBrace != 0 || testParen != 0 || testBracket != 0)
                {
                    return null;
                }
            }

            string valueStr = string.Join("", valueTokens.Select(t => t.ToStringValue()));

            return new CssDeclaration 
            {
                Property = property,
                Value = valueStr.Trim(),
                IsImportant = important
            };
        }

        private static string NormalizePropertyName(string property)
        {
            if (string.IsNullOrEmpty(property))
            {
                return property ?? string.Empty;
            }

            if (property.StartsWith("--", StringComparison.Ordinal))
            {
                return property;
            }

            return property.ToLowerInvariant();
        }

        private void RecoverMalformedDeclaration()
        {
            while (_currentToken.Type != CssTokenType.Semicolon &&
                   _currentToken.Type != CssTokenType.RightBrace &&
                   _currentToken.Type != CssTokenType.EOF)
            {
                ConsumeComponentValue();
            }

            if (_currentToken.Type == CssTokenType.Semicolon)
            {
                ConsumeToken();
            }
        }

        private void ConsumeToken()
        {
            _currentToken = _tokenizer.Consume();
        }

        private void ConsumeWhitespace()
        {
            int loop = 0;
            while ((_currentToken.Type == CssTokenType.Whitespace || _currentToken.Type == CssTokenType.Comment) && _currentToken.Type != CssTokenType.EOF)
            {
                if (loop++ > 100000) break;
                ConsumeToken(); 
            }
        }

        private void ConsumeSimpleBlock()
        {
            CssTokenType initialEnding = CssTokenType.RightBrace;
            if (_currentToken.Type == CssTokenType.LeftParen) initialEnding = CssTokenType.RightParen;
            else if (_currentToken.Type == CssTokenType.LeftBracket) initialEnding = CssTokenType.RightBracket;

            ConsumeToken();
            
            var stack = new Stack<CssTokenType>();
            stack.Push(initialEnding);

            int safety = 0;
            const int MAX_TOKENS = 500000;

            try 
            {
                while (stack.Count > 0)
                {
                    if (_currentToken.Type == CssTokenType.EOF) return;
                    
                    if (safety++ > MAX_TOKENS)
                    {
                        var msg = $"CssSyntaxParser: Block too large or infinite loop. Stack={stack.Count} Token={_currentToken.Type}";
                        FenBrowser.Core.EngineLogCompat.Error(msg, FenBrowser.Core.Logging.LogCategory.Rendering);
                        throw new InvalidOperationException(msg);
                    }

                    CssTokenType expected = stack.Peek();

                    if (_currentToken.Type == expected)
                    {
                        stack.Pop();
                        ConsumeToken();
                        continue;
                    }

                    if (_currentToken.Type == CssTokenType.LeftBrace)
                    {
                        stack.Push(CssTokenType.RightBrace);
                        ConsumeToken();
                    }
                    else if (_currentToken.Type == CssTokenType.LeftParen)
                    {
                        stack.Push(CssTokenType.RightParen);
                        ConsumeToken();
                    }
                    else if (_currentToken.Type == CssTokenType.LeftBracket)
                    {
                        stack.Push(CssTokenType.RightBracket);
                        ConsumeToken();
                    }
                    else
                    {
                        ConsumeToken();
                    }
                }
            }
            catch (Exception ex)
            {
                 FenBrowser.Core.EngineLogCompat.Error($"[CssSyntaxParser] Crash in ConsumeSimpleBlock: {ex}", FenBrowser.Core.Logging.LogCategory.Rendering);
                 throw;
            }
        }

        private void ConsumeComponentValue()
        {
             if (_currentToken.Type == CssTokenType.LeftBrace || 
                _currentToken.Type == CssTokenType.LeftParen || 
                _currentToken.Type == CssTokenType.LeftBracket)
             {
                 ConsumeSimpleBlock();
             }
             else
             {
                 ConsumeToken();
             }
        }
        
        private CssSelector ParseSelector(List<CssToken> tokens, CssSelector parentSelector = null)
        {
            if (tokens == null || tokens.Count == 0) return null;

            string raw = ReconstructSelectorText(tokens);
            if (string.IsNullOrWhiteSpace(raw)) return null;

            if (parentSelector != null)
            {
                raw = ResolveNestingSelector(raw, parentSelector.Raw);
            }

            var chains = SelectorMatcher.ParseSelectorList(raw);
            if (chains.Count == 0) return null;

            var specificity = SelectorMatcher.GetMaximumSpecificity(chains);

            foreach (var chain in chains)
            {
                foreach (var seg in chain.Segments)
                {
                    foreach (var ps in seg.PseudoClasses)
                    {
                        if (!string.IsNullOrEmpty(ps.Args) && (ps.Name == "is" || ps.Name == "not" || ps.Name == "where" || ps.Name == "has"))
                        {
                            ps.ParsedArgs = SelectorMatcher.ParseSelectorList(ps.Args);
                        }
                    }
                }
            }

            return new CssSelector
            {
                Raw = raw?.Trim(),
                Chains = chains,
                Specificity = specificity 
            };
        }

        private static string ResolveNestingSelector(string nestedSelector, string parentSelector)
        {
            if (string.IsNullOrEmpty(nestedSelector)) return parentSelector ?? "";
            if (string.IsNullOrEmpty(parentSelector)) return nestedSelector;

            bool hasNestingSelector = nestedSelector.Contains('&');
            if (hasNestingSelector)
            {
                return nestedSelector.Replace("&", parentSelector);
            }

            return parentSelector + " " + nestedSelector;
        }

        private static string ReconstructSelectorText(List<CssToken> tokens)
        {
            return string.Join("", tokens.Select(SelectorTokenToStringValue));
        }

        private static string SelectorTokenToStringValue(CssToken token)
        {
            switch (token.Type)
            {
                case CssTokenType.Ident:
                    return EscapeIdentifier(token.Value);
                case CssTokenType.Hash:
                    return "#" + EscapeIdentifier(token.Value);
                case CssTokenType.AtKeyword:
                    return "@" + EscapeIdentifier(token.Value);
                case CssTokenType.Function:
                    return EscapeIdentifier(token.Value) + "(";
                case CssTokenType.Dimension:
                    return token.NumericValue + EscapeIdentifier(token.Unit);
                default:
                    return token.ToStringValue();
            }
        }

        private static string EscapeIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            int firstEscape = -1;
            for (int i = 0; i < value.Length; i++)
            {
                if (RequiresIdentifierEscape(value, i))
                {
                    firstEscape = i;
                    break;
                }
            }

            if (firstEscape < 0)
            {
                return value;
            }

            var sb = new System.Text.StringBuilder(value.Length);
            sb.Append(value, 0, firstEscape);
            for (int i = firstEscape; i < value.Length; i++)
            {
                char c = value[i];

                if (RequiresIdentifierEscape(value, i))
                {
                    if (c == '\0')
                    {
                        sb.Append('\uFFFD');
                    }
                    else
                    {
                        sb.Append('\\');
                        sb.Append(c);
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private static bool RequiresIdentifierEscape(string value, int index)
        {
            char c = value[index];
            return char.IsWhiteSpace(c) ||
                   c == '\\' ||
                   c == '\0' ||
                   (!IsNameChar(c) && !(index == 0 && c == '-')) ||
                   (index == 0 && char.IsDigit(c)) ||
                   (index == 1 && value[0] == '-' && char.IsDigit(c));
        }

        private static bool IsNameStart(char c)
        {
            return char.IsLetter(c) || c == '_' || c >= 0x0080;
        }

        private static bool IsNameChar(char c)
        {
            return IsNameStart(c) || char.IsDigit(c) || c == '-';
        }
    }

    public static class CssTokenExtensions
    {
        public static string ToStringValue(this CssToken token)
        {
             switch (token.Type)
             {
                 case CssTokenType.Ident: return token.Value;
                 case CssTokenType.Hash: return "#" + token.Value;
                 case CssTokenType.AtKeyword: return "@" + token.Value;
                 case CssTokenType.Function: return token.Value + "(";
                 case CssTokenType.Dimension: return token.NumericValue + token.Unit;
                 case CssTokenType.Percentage: return token.NumericValue + "%";
                 case CssTokenType.Number: return token.NumericValue.ToString();
                 case CssTokenType.String: return "\"" + token.Value + "\"";
                 case CssTokenType.Url: return $"url({token.Value})";
                 case CssTokenType.Delim: return token.Delimiter.ToString();
                 case CssTokenType.Colon: return ":";
                 case CssTokenType.Semicolon: return ";";
                 case CssTokenType.LeftBrace: return "{";
                 case CssTokenType.RightBrace: return "}";
                 case CssTokenType.LeftParen: return "(";
                 case CssTokenType.RightParen: return ")";
                 case CssTokenType.LeftBracket: return "[";
                 case CssTokenType.RightBracket: return "]";
                 case CssTokenType.Comma: return ",";
                 case CssTokenType.Whitespace: return token.Value ?? " ";
                 case CssTokenType.CDO: return "<!--";
                 case CssTokenType.CDC: return "-->";
             }
             return "";
        }
    }
}
