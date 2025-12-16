using System;
using System.Collections.Generic;
using System.Text;
using System.Globalization;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// CSS token types according to CSS Syntax Level 3.
    /// </summary>
    public enum CssTokenType
    {
        Ident,          // identifiers
        Function,       // function-name(
        AtKeyword,      // @keyword
        Hash,           // #identifier
        String,         // "..." or '...'
        Url,            // url(...)
        Number,         // integers and decimals
        Percentage,     // number%
        Dimension,      // number + unit (px, em, etc.)
        Whitespace,     // spaces, tabs, newlines
        Colon,          // :
        Semicolon,      // ;
        Comma,          // ,
        OpenBracket,    // [
        CloseBracket,   // ]
        OpenParen,      // (
        CloseParen,     // )
        OpenBrace,      // {
        CloseBrace,     // }
        Delim,          // any other single character
        Comment,        // /* ... */
        CDO,            // <!--
        CDC,            // -->
        EOF,            // end of input
        BadString,      // unclosed string
        BadUrl          // malformed url
    }

    /// <summary>
    /// Represents a single CSS token.
    /// </summary>
    public class CssToken
    {
        public CssTokenType Type { get; set; }
        public string Value { get; set; }
        public double NumericValue { get; set; }
        public string Unit { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }

        public override string ToString()
        {
            if (Type == CssTokenType.Number || Type == CssTokenType.Dimension)
                return $"{Type}({NumericValue}{Unit})";
            return $"{Type}({Value})";
        }
    }

    /// <summary>
    /// CSS tokenizer implementing CSS Syntax Level 3 specification.
    /// Handles minified CSS, escaped characters, and nested structures.
    /// </summary>
    public class CssTokenizer
    {
        private readonly string _input;
        private int _pos;
        private int _line;
        private int _column;

        public CssTokenizer(string cssText)
        {
            _input = cssText ?? "";
            _pos = 0;
            _line = 1;
            _column = 1;
        }

        /// <summary>
        /// Tokenize the entire CSS input.
        /// </summary>
        public List<CssToken> Tokenize()
        {
            var tokens = new List<CssToken>();
            
            while (!IsEof())
            {
                var token = NextToken();
                if (token != null)
                {
                    tokens.Add(token);
                }
            }
            
            tokens.Add(new CssToken { Type = CssTokenType.EOF, Line = _line, Column = _column });
            return tokens;
        }

        /// <summary>
        /// Get the next token.
        /// </summary>
        public CssToken NextToken()
        {
            if (IsEof()) return new CssToken { Type = CssTokenType.EOF };

            int startLine = _line;
            int startCol = _column;

            char c = Peek();

            // Comments
            if (c == '/' && PeekAt(1) == '*')
            {
                return ConsumeComment(startLine, startCol);
            }

            // Whitespace
            if (IsWhitespace(c))
            {
                return ConsumeWhitespace(startLine, startCol);
            }

            // Strings
            if (c == '"' || c == '\'')
            {
                return ConsumeString(c, startLine, startCol);
            }

            // Hash
            if (c == '#')
            {
                Advance();
                if (IsNameChar(Peek()))
                {
                    string value = ConsumeName();
                    return new CssToken { Type = CssTokenType.Hash, Value = value, Line = startLine, Column = startCol };
                }
                return new CssToken { Type = CssTokenType.Delim, Value = "#", Line = startLine, Column = startCol };
            }

            // At-keyword
            if (c == '@')
            {
                Advance();
                if (IsNameStartChar(Peek()))
                {
                    string name = ConsumeName();
                    return new CssToken { Type = CssTokenType.AtKeyword, Value = name, Line = startLine, Column = startCol };
                }
                return new CssToken { Type = CssTokenType.Delim, Value = "@", Line = startLine, Column = startCol };
            }

            // Numbers
            if (IsDigit(c) || (c == '.' && IsDigit(PeekAt(1))) || 
                ((c == '+' || c == '-') && (IsDigit(PeekAt(1)) || (PeekAt(1) == '.' && IsDigit(PeekAt(2))))))
            {
                return ConsumeNumeric(startLine, startCol);
            }

            // Identifiers and functions
            if (IsNameStartChar(c) || c == '-')
            {
                return ConsumeIdentLike(startLine, startCol);
            }

            // Single character tokens
            Advance();
            return c switch
            {
                ':' => new CssToken { Type = CssTokenType.Colon, Value = ":", Line = startLine, Column = startCol },
                ';' => new CssToken { Type = CssTokenType.Semicolon, Value = ";", Line = startLine, Column = startCol },
                ',' => new CssToken { Type = CssTokenType.Comma, Value = ",", Line = startLine, Column = startCol },
                '[' => new CssToken { Type = CssTokenType.OpenBracket, Value = "[", Line = startLine, Column = startCol },
                ']' => new CssToken { Type = CssTokenType.CloseBracket, Value = "]", Line = startLine, Column = startCol },
                '(' => new CssToken { Type = CssTokenType.OpenParen, Value = "(", Line = startLine, Column = startCol },
                ')' => new CssToken { Type = CssTokenType.CloseParen, Value = ")", Line = startLine, Column = startCol },
                '{' => new CssToken { Type = CssTokenType.OpenBrace, Value = "{", Line = startLine, Column = startCol },
                '}' => new CssToken { Type = CssTokenType.CloseBrace, Value = "}", Line = startLine, Column = startCol },
                _ => new CssToken { Type = CssTokenType.Delim, Value = c.ToString(), Line = startLine, Column = startCol }
            };
        }

        #region Consume Methods

        private CssToken ConsumeComment(int startLine, int startCol)
        {
            Advance(); // /
            Advance(); // *
            var sb = new StringBuilder();
            
            while (!IsEof())
            {
                if (Peek() == '*' && PeekAt(1) == '/')
                {
                    Advance();
                    Advance();
                    break;
                }
                sb.Append(Advance());
            }
            
            return new CssToken { Type = CssTokenType.Comment, Value = sb.ToString(), Line = startLine, Column = startCol };
        }

        private CssToken ConsumeWhitespace(int startLine, int startCol)
        {
            var sb = new StringBuilder();
            while (IsWhitespace(Peek()))
            {
                sb.Append(Advance());
            }
            return new CssToken { Type = CssTokenType.Whitespace, Value = sb.ToString(), Line = startLine, Column = startCol };
        }

        private CssToken ConsumeString(char quote, int startLine, int startCol)
        {
            Advance(); // opening quote
            var sb = new StringBuilder();
            
            while (!IsEof())
            {
                char c = Peek();
                
                if (c == quote)
                {
                    Advance();
                    return new CssToken { Type = CssTokenType.String, Value = sb.ToString(), Line = startLine, Column = startCol };
                }
                
                if (c == '\\')
                {
                    Advance();
                    if (!IsEof())
                    {
                        char escaped = Advance();
                        if (escaped == '\n') continue; // line continuation
                        sb.Append(escaped);
                    }
                    continue;
                }
                
                if (c == '\n')
                {
                    // Unclosed string
                    return new CssToken { Type = CssTokenType.BadString, Value = sb.ToString(), Line = startLine, Column = startCol };
                }
                
                sb.Append(Advance());
            }
            
            return new CssToken { Type = CssTokenType.BadString, Value = sb.ToString(), Line = startLine, Column = startCol };
        }

        private CssToken ConsumeNumeric(int startLine, int startCol)
        {
            var sb = new StringBuilder();
            
            // Sign
            if (Peek() == '+' || Peek() == '-')
            {
                sb.Append(Advance());
            }
            
            // Integer part
            while (IsDigit(Peek()))
            {
                sb.Append(Advance());
            }
            
            // Decimal part
            if (Peek() == '.' && IsDigit(PeekAt(1)))
            {
                sb.Append(Advance()); // .
                while (IsDigit(Peek()))
                {
                    sb.Append(Advance());
                }
            }
            
            // Exponent
            if ((Peek() == 'e' || Peek() == 'E') && 
                (IsDigit(PeekAt(1)) || ((PeekAt(1) == '+' || PeekAt(1) == '-') && IsDigit(PeekAt(2)))))
            {
                sb.Append(Advance());
                if (Peek() == '+' || Peek() == '-')
                {
                    sb.Append(Advance());
                }
                while (IsDigit(Peek()))
                {
                    sb.Append(Advance());
                }
            }
            
            double numValue = 0;
            double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out numValue);
            
            // Check for unit or percentage
            if (Peek() == '%')
            {
                Advance();
                return new CssToken { Type = CssTokenType.Percentage, Value = sb.ToString(), NumericValue = numValue, Line = startLine, Column = startCol };
            }
            
            if (IsNameStartChar(Peek()) || Peek() == '-')
            {
                string unit = ConsumeName();
                return new CssToken { Type = CssTokenType.Dimension, Value = sb.ToString(), NumericValue = numValue, Unit = unit, Line = startLine, Column = startCol };
            }
            
            return new CssToken { Type = CssTokenType.Number, Value = sb.ToString(), NumericValue = numValue, Line = startLine, Column = startCol };
        }

        private CssToken ConsumeIdentLike(int startLine, int startCol)
        {
            string name = ConsumeName();
            
            // Check for url()
            if (name.Equals("url", StringComparison.OrdinalIgnoreCase) && Peek() == '(')
            {
                Advance(); // (
                return ConsumeUrl(name, startLine, startCol);
            }
            
            // Check for function
            if (Peek() == '(')
            {
                Advance();
                return new CssToken { Type = CssTokenType.Function, Value = name, Line = startLine, Column = startCol };
            }
            
            return new CssToken { Type = CssTokenType.Ident, Value = name, Line = startLine, Column = startCol };
        }

        private CssToken ConsumeUrl(string name, int startLine, int startCol)
        {
            // Skip whitespace
            while (IsWhitespace(Peek())) Advance();
            
            // Check for quoted URL
            if (Peek() == '"' || Peek() == '\'')
            {
                var stringToken = ConsumeString(Peek(), startLine, startCol);
                while (IsWhitespace(Peek())) Advance();
                if (Peek() == ')') Advance();
                return new CssToken { Type = CssTokenType.Url, Value = stringToken.Value, Line = startLine, Column = startCol };
            }
            
            // Unquoted URL
            var sb = new StringBuilder();
            while (!IsEof())
            {
                char c = Peek();
                if (c == ')') { Advance(); break; }
                if (IsWhitespace(c)) { while (IsWhitespace(Peek())) Advance(); if (Peek() == ')') { Advance(); break; } }
                if (c == '"' || c == '\'' || c == '(') return new CssToken { Type = CssTokenType.BadUrl, Value = sb.ToString(), Line = startLine, Column = startCol };
                if (c == '\\')
                {
                    Advance();
                    if (!IsEof()) sb.Append(Advance());
                    continue;
                }
                sb.Append(Advance());
            }
            
            return new CssToken { Type = CssTokenType.Url, Value = sb.ToString(), Line = startLine, Column = startCol };
        }

        private string ConsumeName()
        {
            var sb = new StringBuilder();
            while (IsNameChar(Peek()))
            {
                if (Peek() == '\\')
                {
                    Advance();
                    if (!IsEof()) sb.Append(Advance());
                }
                else
                {
                    sb.Append(Advance());
                }
            }
            return sb.ToString();
        }

        #endregion

        #region Helper Methods

        private bool IsEof() => _pos >= _input.Length;
        
        private char Peek() => IsEof() ? '\0' : _input[_pos];
        
        private char PeekAt(int offset)
        {
            int idx = _pos + offset;
            return idx >= _input.Length ? '\0' : _input[idx];
        }
        
        private char Advance()
        {
            if (IsEof()) return '\0';
            char c = _input[_pos++];
            if (c == '\n') { _line++; _column = 1; }
            else { _column++; }
            return c;
        }

        private static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        private static bool IsDigit(char c) => c >= '0' && c <= '9';
        private static bool IsLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        private static bool IsNameStartChar(char c) => IsLetter(c) || c == '_' || c > 127;
        private static bool IsNameChar(char c) => IsNameStartChar(c) || IsDigit(c) || c == '-';

        #endregion
    }
}
