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
                    FenBrowser.Core.FenLogger.Warn("[CssSyntaxParser] ParseStylesheet infinite loop detected. Aborting.", FenBrowser.Core.Logging.LogCategory.CSS);
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
                    if (rule != null) sheet.Rules.Add(rule);
                    ConsumeWhitespace();
                    continue;
                }

                // Qualified Rule (Style Rule)
                var qRule = ConsumeQualifiedRule();
                if (qRule != null) 
                {
                    sheet.Rules.Add(qRule);
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
                    if (subRule != null) rules.Add(subRule);
                }
                else
                {
                    var subRule = ConsumeQualifiedRule();
                    if (subRule != null) rules.Add(subRule);
                }
            }
        }

        private CssStyleRule ConsumeQualifiedRule()
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
            rule.Selector = ParseSelector(selectorTokens);
            if (rule.Selector == null)
            {
                 // Invalid selector (e.g. empty or malformed), but we MUST consume the block to advance parser state
                 ConsumeDeclarationBlock();
                 return null; 
            }

            // Parse block
            rule.Declarations.AddRange(ConsumeDeclarationBlock());
            
            return rule;
        }

        private List<CssDeclaration> ConsumeDeclarationBlock()
        {
            var declarations = new List<CssDeclaration>();
            ConsumeToken(); // {

            while (_currentToken.Type != CssTokenType.RightBrace && _currentToken.Type != CssTokenType.EOF)
            {
                if (_currentToken.Type == CssTokenType.Whitespace || _currentToken.Type == CssTokenType.Semicolon)
                {
                    ConsumeToken();
                    continue;
                }

                var decl = ConsumeDeclaration();
                if (decl != null) declarations.Add(decl);
            }

            if (_currentToken.Type == CssTokenType.RightBrace)
            {
                ConsumeToken(); // }
            }

            return declarations;
        }

        private CssDeclaration ConsumeDeclaration()
        {
            if (_currentToken.Type != CssTokenType.Ident)
            {
                // Recovery: consume until ; or }
                ConsumeComponentValue(); 
                return null;
            }

            string property = _currentToken.Value.ToLowerInvariant();
            ConsumeToken();
            
            while (_currentToken.Type == CssTokenType.Whitespace) ConsumeToken();

            if (_currentToken.Type != CssTokenType.Colon)
            {
                // Parse error
                return null; // TODO: proper recovery
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

            // Stringify value (Basic support)
            string valueStr = string.Join("", valueTokens.Select(t => t.ToStringValue())); // Need simple ToString helper

            if (property == "visibility")
            {
                global::FenBrowser.Core.FenLogger.Log($"[CssSyntaxParser] Parsed declaration: {property}: {valueStr.Trim()} (Important: {important})", global::FenBrowser.Core.Logging.LogCategory.CSS, global::FenBrowser.Core.Logging.LogLevel.Debug);
            }

            return new CssDeclaration 
            {
                Property = property,
                Value = valueStr.Trim(),
                IsImportant = important
            };
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
                        FenBrowser.Core.FenLogger.Error(msg, FenBrowser.Core.Logging.LogCategory.Rendering);
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
                 FenBrowser.Core.FenLogger.Error($"[CssSyntaxParser] Crash in ConsumeSimpleBlock: {ex}", FenBrowser.Core.Logging.LogCategory.Rendering);
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
        
        private CssSelector ParseSelector(List<CssToken> tokens)
        {
            if (tokens == null || tokens.Count == 0) return null;

            string raw = string.Join("", tokens.Select(t => t.ToStringValue())).Trim();
            if (string.IsNullOrEmpty(raw)) return null;

            var chains = SelectorMatcher.ParseSelectorList(raw);
            if (chains.Count == 0) return null; // Invalid selector

            // Calculate overall specificity - maybe max of chains?
            // The Rule holds ONE specificity?
            // Actually, a rule like "h1, h2" is syntactic sugar for TWO rules.
            // But CSSOM spec says it's one rule with a selector list.
            // When matching, we match ANY of the selectors.
            // The Specificity depends on WHICH selector matched.
            
            // FOR PHASE 3.2: We will simplify. 
            // If we have "h1, h2", we keep it as one Rule.
            // But specificity logic in CascadeEngine needs to know WHICH one matched to apply correct specificity.
            // My CascadeEngine currently uses `styleRule.Selector.Specificity`.
            // This suggests I need to change `CssStyleRule` to either:
            // A) Split into multiple rules during parsing (one per chain).
            // B) CascadeEngine iterates chains and finds best match.
            
            // The most robust way for now: Split comma-separated selectors into multiple CssStyleRules.
            // This simplifies CascadeEngine (it just sorts rules).
            
            // However, `ConsumeQualifiedRule` returns ONE `CssStyleRule`.
            // I should modify `ParseStylesheet` to handle this or modifications to `CssStyleRule`.
            
            // Let's stick to B for now: Update `CssSelector` to hold the list, and update `CascadeEngine` to calculate specificity dynamically based on match.
            
            // But wait, `CssRule` needs an Order. If I split, they have same order? Yes.
            
            // Let's assume for now `CssSelector` holds the chains.
            // I will set `Specificity` to the MAX of the chains for metadata purposes, 
            // but `CascadeEngine` needs to be smarter.
            
            // CRITICAL FIX: Use FirstOrDefault to prevent "Sequence contains no elements" exception
            var specificity = chains.Select(c => c.Specificity).OrderByDescending(s => s).FirstOrDefault();

            // PRE-PARSE functional pseudo-classes to avoid O(N^2) re-parsing during cascade
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
                Raw = raw,
                Chains = chains,
                Specificity = specificity 
            };
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
