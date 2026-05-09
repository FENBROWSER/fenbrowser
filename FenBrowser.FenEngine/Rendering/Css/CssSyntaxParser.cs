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

        public int MaxRules { get; set; } = 200000;
        public int MaxDeclarationsPerBlock { get; set; } = 8192;

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
                    var rule = ConsumeAtRule();
                    if (!TryAddRule(sheet.Rules, rule)) break;
                    ConsumeWhitespace();
                    continue;
                }

                // Qualified Rule (Style Rule)
                var qRule = ConsumeQualifiedRule();
                if (qRule != null && !TryAddRule(sheet.Rules, qRule))
                {
                    break;
                }
                ConsumeWhitespace();
            }
            return sheet;
        }

        private CssRule ConsumeAtRule()
        {
            // Placeholder for @media, etc.
            // For now, simple consumption until block
            string name = _currentToken.Value;
            ConsumeToken(); // skip @name

            if (name.Equals("media", StringComparison.OrdinalIgnoreCase))
            {
                var mediaRule = new CssMediaRule();
                
                // 1. Consume condition (up to the opening brace)
                var conditionTokens = new List<CssToken>();
                while (_currentToken.Type != CssTokenType.LeftBrace && _currentToken.Type != CssTokenType.Semicolon && _currentToken.Type != CssTokenType.EOF)
                {
                    conditionTokens.Add(_currentToken);
                    ConsumeToken();
                }
                
                // Flatten tokens to string for the condition
                mediaRule.Condition = string.Join("", conditionTokens.Select(t => t.ToStringValue())).Trim();

                // 2. Consume block content
                if (_currentToken.Type == CssTokenType.LeftBrace)
                {
                    ConsumeToken(); // {
                    ParseInsideBlock(mediaRule.Rules);
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
                    ParseInsideBlock(layerRule.Rules);
                    if (_currentToken.Type == CssTokenType.RightBrace) ConsumeToken();
                    return layerRule;
                }
                else if (_currentToken.Type == CssTokenType.Semicolon)
                {
                    ConsumeToken();
                    // Just a declaration of order: @layer base, theme;
                    // We can represent this by returning multiple rules or a special rule.
                    // For simplicity, let's return a CssLayerRule with multiple names or handle it in a special way.
                    // But the parser returns ONE rule.
                    // Let's make CssLayerRule.Name contain the comma-separated string if it's a declaration only?
                    // No, let's return a rule for EACH name.
                    // Wait, ParseStylesheet accepts a list of rules.
                    
                    // Actually, I'll return a special CssLayerRule where Name identifies it as a multi-declaration.
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
                // Simple @scope (...) to (...) parsing
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
                    ParseInsideBlock(scopeRule.Rules);
                    if (_currentToken.Type == CssTokenType.RightBrace) ConsumeToken();
                }
                return scopeRule;
            }

            if (name.Equals("font-face", StringComparison.OrdinalIgnoreCase))
            {
                var fontFaceRule = new CssFontFaceRule();

                // Consume any at-rule prelude tokens up to the declaration block.
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
            
            return null; // Ignore unknown rules
        }

        private void ParseInsideBlock(List<CssRule> rules)
        {
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
                    var subRule = ConsumeAtRule();
                    if (!TryAddRule(rules, subRule)) return;
                }
                else
                {
                    var subRule = ConsumeQualifiedRule();
                    if (!TryAddRule(rules, subRule)) return;
                }
            }
        }

        private CssStyleRule ConsumeQualifiedRule(CssSelector parentSelector = null)
        {
            var rule = new CssStyleRule();
            rule.Order = _ruleCount++;

            // Parse selector (prelude)
            var selectorTokens = new List<CssToken>();
            int selLoop = 0;
            while (_currentToken.Type != CssTokenType.LeftBrace && _currentToken.Type != CssTokenType.EOF)
            {
                if (selLoop++ > 100000) break; // Safety break
                selectorTokens.Add(_currentToken);
                ConsumeToken();
            }

            if (_currentToken.Type == CssTokenType.EOF) return null; // Parse error

            // Parse selector string and specificity
            rule.Selector = ParseSelector(selectorTokens, parentSelector);
            if (rule.Selector == null)
            {
                 // Invalid selector (e.g. empty or malformed), but we MUST consume the block to advance parser state
                 ConsumeDeclarationBlock(null);
                 return null; 
            }

            // Parse block
            ConsumeStyleRuleBlock(rule);
            
            return rule;
        }

        private void ConsumeStyleRuleBlock(CssStyleRule rule)
        {
            ConsumeToken(); // {
            int declarationCount = 0;
            int nestCount = 0;
            const int MaxNestingDepth = 256;

            while (_currentToken.Type != CssTokenType.RightBrace && _currentToken.Type != CssTokenType.EOF)
            {
                if (_currentToken.Type == CssTokenType.Whitespace || _currentToken.Type == CssTokenType.Semicolon)
                {
                    ConsumeToken();
                    continue;
                }

                // Check for unambiguous nested rule starts (delim selectors, hash, at-keyword, brackets, colon)
                if (IsUnambiguousNestedRuleStart(_currentToken))
                {
                    if (nestCount >= MaxNestingDepth)
                    {
                        ConsumeComponentValue();
                        continue;
                    }

                    if (_currentToken.Type == CssTokenType.AtKeyword)
                    {
                        var nestedAtRule = ConsumeNestedAtRule(rule.Selector);
                        if (nestedAtRule != null)
                        {
                            rule.NestedRules.Add(nestedAtRule);
                            nestCount++;
                        }
                    }
                    else
                    {
                        var nestedRule = ConsumeQualifiedRule(rule.Selector);
                        if (nestedRule != null)
                        {
                            rule.NestedRules.Add(nestedRule);
                            nestCount++;
                        }
                    }
                    continue;
                }

                // For Ident tokens, try declaration first; if invalid (no colon), treat as nested rule
                if (_currentToken.Type == CssTokenType.Ident)
                {
                    // Peek past whitespace to check if next meaningful token is a colon
                    var peek = ConsumeDeclarationOrNestedRule(rule, ref nestCount, MaxNestingDepth);
                    if (peek) continue;
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

        private bool ConsumeDeclarationOrNestedRule(CssStyleRule rule, ref int nestCount, int maxNestingDepth)
        {
            // Peek the next token to disambiguate Ident token
            var savedPosition = _tokenizer.SavePosition();
            var savedToken = _currentToken;

            // Skip current Ident
            ConsumeToken();

            // Skip whitespace to find the next meaningful token
            while (_currentToken.Type == CssTokenType.Whitespace)
                ConsumeToken();

            bool isDeclaration = _currentToken.Type == CssTokenType.Colon;

            // Restore position
            _tokenizer.RestorePosition(savedPosition);
            _currentToken = savedToken;

            if (isDeclaration)
            {
                // ConsumeDeclaration will handle it above
                return false;
            }

            // This is a nested rule starting with an Ident selector
            if (nestCount >= maxNestingDepth)
            {
                // Consume to avoid infinite loop
                var discardSelectorTokens = new List<CssToken>();
                while (_currentToken.Type != CssTokenType.LeftBrace && _currentToken.Type != CssTokenType.EOF)
                {
                    _currentToken = _tokenizer.Consume();
                }
                if (_currentToken.Type == CssTokenType.LeftBrace)
                    ConsumeSimpleBlock();
                return true;
            }

            var nestedRule = ConsumeQualifiedRule(rule.Selector);
            if (nestedRule != null)
            {
                rule.NestedRules.Add(nestedRule);
                nestCount++;
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

        private static bool IsNestedRuleStart(CssToken token)
        {
            switch (token.Type)
            {
                case CssTokenType.Ident:
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

        private CssRule ConsumeNestedAtRule(CssSelector parentSelector)
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
                    ParseInsideBlockWithParent(mediaRule.Rules, parentSelector);
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
                    ParseInsideBlockWithParent(atRule.Rules, parentSelector);
                    if (_currentToken.Type == CssTokenType.RightBrace)
                        ConsumeToken(); // }
                    return atRule;
                }
                return null;
            }

            // Unknown nested at-rule: consume and discard
            while (_currentToken.Type != CssTokenType.Semicolon && _currentToken.Type != CssTokenType.LeftBrace && _currentToken.Type != CssTokenType.EOF)
                ConsumeToken();

            if (_currentToken.Type == CssTokenType.LeftBrace)
                ConsumeSimpleBlock();
            else if (_currentToken.Type == CssTokenType.Semicolon)
                ConsumeToken();

            return null;
        }

        private void ParseInsideBlockWithParent(List<CssRule> rules, CssSelector parentSelector)
        {
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
                    var subRule = ConsumeAtRule();
                    if (!TryAddRule(rules, subRule)) return;
                }
                else
                {
                    var subRule = ConsumeQualifiedRule(parentSelector);
                    if (!TryAddRule(rules, subRule)) return;
                }
            }
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
                // Recovery: consume until ; or }
                ConsumeComponentValue(); 
                return null;
            }

            string property = NormalizePropertyName(_currentToken.Value);
            ConsumeToken();
            
            while (_currentToken.Type == CssTokenType.Whitespace) ConsumeToken();

            if (_currentToken.Type != CssTokenType.Colon)
            {
                // Parse error: recover until declaration boundary while preserving parser progress.
                RecoverMalformedDeclaration();
                return null;
            }
            ConsumeToken(); // :

            while (_currentToken.Type == CssTokenType.Whitespace) ConsumeToken();

            // Value
            var valueTokens = new List<CssToken>();
            bool important = false;
            
            while (_currentToken.Type != CssTokenType.Semicolon && _currentToken.Type != CssTokenType.RightBrace && _currentToken.Type != CssTokenType.EOF)
            {
                valueTokens.Add(_currentToken);
                ConsumeToken();
            }
            
            // Post-process value tokens for !important
            // Reverse check for "important" ident and "!" delim
            if (valueTokens.Count >= 2)
            {
                var last = valueTokens.Last();
                // Check if last is separate whitespace? Tokenizer might merge? No.
                // Loop backwards skipping whitespace
                int i = valueTokens.Count - 1;
                while (i >= 0 && valueTokens[i].Type == CssTokenType.Whitespace) i--;
                
                if (i >= 0 && valueTokens[i].Type == CssTokenType.Ident && valueTokens[i].Value.Equals("important", StringComparison.OrdinalIgnoreCase))
                {
                    int j = i - 1;
                    while (j >= 0 && valueTokens[j].Type == CssTokenType.Whitespace) j--;
                    if (j >= 0 && valueTokens[j].Type == CssTokenType.Delim && valueTokens[j].Delimiter == '!')
                    {
                        important = true;
                        // Remove ! and important from value
                        // Truncate list at j
                        valueTokens = valueTokens.Take(j).ToList();
                    }
                }
            }

            // CSS parsing rule: a leftover '!' inside a declaration value is invalid
            // unless it formed a terminal !important that we stripped above.
            // Acid2 depends on malformed declarations like "border: ... ! error" being ignored.
            if (valueTokens.Any(t => t.Type == CssTokenType.Delim && t.Delimiter == '!'))
            {
                return null;
            }

            // Stringify value (Basic support)
            string valueStr = string.Join("", valueTokens.Select(t => t.ToStringValue())); // Need simple ToString helper

            if (property == "visibility")
            {
                global::FenBrowser.Core.EngineLogCompat.Log($"[CssSyntaxParser] Parsed declaration: {property}: {valueStr.Trim()} (Important: {important})", global::FenBrowser.Core.Logging.LogCategory.CSS, global::FenBrowser.Core.Logging.LogLevel.Debug);
            }

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

            // Custom properties are case-sensitive and must preserve authored casing.
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
                if (loop++ > 100000) break; // Safety
                ConsumeToken(); 
            }
        }

        private int _nestingLevel = 0;

        private void ConsumeSimpleBlock()
        {
            // Iterative implementation to avoid StackOverflow and improve performance
            
            // Determine initial ending
            CssTokenType initialEnding = CssTokenType.RightBrace;
            if (_currentToken.Type == CssTokenType.LeftParen) initialEnding = CssTokenType.RightParen;
            else if (_currentToken.Type == CssTokenType.LeftBracket) initialEnding = CssTokenType.RightBracket;

            ConsumeToken(); // Consume the opening token
            
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

                    // Nested block starts
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
                 // Log and rethrow to Ensure visibility
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

            // CSS Nesting: resolve & references and implicit parent prepending
            if (parentSelector != null)
            {
                raw = ResolveNestingSelector(raw, parentSelector.Raw);
            }

            var chains = SelectorMatcher.ParseSelectorList(raw);
            if (chains.Count == 0) return null;

            var specificity = chains.Select(c => c.Specificity).OrderByDescending(s => s).FirstOrDefault();

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

            // Implicit nesting: prepend parent with descendant combinator
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

            var sb = new System.Text.StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];

                bool mustEscape =
                    char.IsWhiteSpace(c) ||
                    c == '\\' ||
                    c == '\0' ||
                    (!IsNameChar(c) && !(i == 0 && c == '-')) ||
                    (i == 0 && char.IsDigit(c)) ||
                    (i == 1 && value[0] == '-' && char.IsDigit(c));

                if (mustEscape)
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
                 case CssTokenType.Whitespace: return " ";
                 case CssTokenType.CDO: return "<!--";
                 case CssTokenType.CDC: return "-->";
             }
             return "";
        }
    }
}
