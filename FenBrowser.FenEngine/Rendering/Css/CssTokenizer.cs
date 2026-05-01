// SpecRef: CSS Syntax Module Level 3 tokenization algorithm
// CapabilityId: CSS-SYNTAX-TOKENIZATION-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
using System;
using System.Collections.Generic;
using System.Text;
using System.Globalization; // Added for NumberStyles

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// CSS Syntax Level 3 Tokenizer.
    /// Implements https://www.w3.org/TR/css-syntax-3/#tokenization
    /// </summary>
    public class CssTokenizer
    {
        private readonly string _input;
        private int _position;
        private readonly int _length;

        public CssTokenizer(string input)
        {
            // Preprocessing: CSS spec requires replacing CRLF with LF, CR with LF, NULL with Replacement Char
            _input = Preprocess(input);
            _length = _input.Length;
            _position = 0;
        }

        private static string Preprocess(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            
            var sb = new StringBuilder(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '\r')
                {
                    // CRLF -> LF, CR -> LF
                    if (i + 1 < input.Length && input[i + 1] == '\n')
                    {
                        i++;
                    }
                    sb.Append('\n');
                }
                else if (c == '\0')
                {
                    sb.Append('\uFFFD'); // Replacement character
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        public CssToken Consume()
        {
            // Main processing loop
            // https://www.w3.org/TR/css-syntax-3/#consume-token
            
            // Consume comments first
            ConsumeComments();

            if (_position >= _length)
                return new CssToken(CssTokenType.EOF);

            char code = _input[_position];

            // Whitespace
            if (IsWhitespace(code))
            {
                ConsumeWhitespace();
                return new CssToken(CssTokenType.Whitespace);
            }

            // String "
            if (code == '"')
            {
                return ConsumeStringToken();
            }

            // Hash #
            if (code == '#')
            {
                if (IsNameChar(PeekAt(1)) || AreValidEscape(_input, _position + 1)) 
                {
                    _position++;
                    bool isId = CheckThreeCodePoints(0, IsIdentifierStart); // heuristic, or follow strictly?
                    // Spec: If the next 3 code points would start an identifier, set type flag to "id" 
                    // Actually spec says: If the next input code point is a name code point or the next two input code points are a valid escape, then:
                    // 1. Create a <hash-token>.
                    // 2. If the next 3 code points would start an identifier, set the <hash-token>’s type flag to "id".
                    // 3. Consume a name, and set the <hash-token>’s value to the returned string.
                    // 4. Return the <hash-token>.
                    
                    bool wouldStartIdent = WouldStartIdentifier(_input, _position);
                    string name = ConsumeName();
                    return new CssToken(CssTokenType.Hash, name, wouldStartIdent ? HashType.Id : HashType.Unrestricted);
                }
                
                _position++;
                return new CssToken(CssTokenType.Delim, '#');
            }

            // String '
            if (code == '\'')
            {
                 return ConsumeStringToken();
            }

            // Left Paren (
            if (code == '(')
            {
                _position++;
                return new CssToken(CssTokenType.LeftParen);
            }

            // Right Paren )
            if (code == ')')
            {
                _position++;
                return new CssToken(CssTokenType.RightParen);
            }

            // + (Number checks)
            if (code == '+')
            {
                if (StartsWithNumber())
                {
                    return ConsumeNumericToken();
                }
                _position++;
                return new CssToken(CssTokenType.Delim, '+');
            }

            // ,
            if (code == ',')
            {
                _position++;
                return new CssToken(CssTokenType.Comma);
            }

            // - (Number, Ident, CDC)
            if (code == '-')
            {
                if (StartsWithNumber())
                {
                    return ConsumeNumericToken();
                }
                if (StartsWithIdentifier()) 
                {
                    return ConsumeIdentLikeToken();
                }
                if (_position + 2 < _length && _input[_position + 1] == '-' && _input[_position + 2] == '>')
                {
                    _position += 3;
                    return new CssToken(CssTokenType.CDC);
                }
                _position++;
                return new CssToken(CssTokenType.Delim, '-');
            }

            // . (Number)
            if (code == '.')
            {
                 if (StartsWithNumber())
                {
                    return ConsumeNumericToken();
                }
                _position++;
                return new CssToken(CssTokenType.Delim, '.');
            }

            // :
            if (code == ':')
            {
                _position++;
                return new CssToken(CssTokenType.Colon);
            }

            // ;
            if (code == ';')
            {
                _position++;
                return new CssToken(CssTokenType.Semicolon);
            }

            // < (CDO)
            if (code == '<')
            {
                 if (_position + 3 < _length && _input[_position + 1] == '!' && _input[_position + 2] == '-' && _input[_position + 3] == '-')
                {
                    _position += 4;
                    return new CssToken(CssTokenType.CDO);
                }
                _position++;
                return new CssToken(CssTokenType.Delim, '<');
            }

            // @
            if (code == '@')
            {
                if (WouldStartIdentifier(_input, _position + 1))
                {
                    _position++;
                    string name = ConsumeName();
                    return new CssToken(CssTokenType.AtKeyword, name);
                }
                _position++;
                return new CssToken(CssTokenType.Delim, '@');
            }

            // [
            if (code == '[')
            {
                _position++;
                return new CssToken(CssTokenType.LeftBracket);
            }

            // \ (Escape -> Ident)
            if (code == '\\')
            {
                 if (IsValidEscape(_position))
                 {
                     return ConsumeIdentLikeToken();
                 }
                 _position++;
                 return new CssToken(CssTokenType.Delim, '\\');
            }

            // ]
            if (code == ']')
            {
                _position++;
                return new CssToken(CssTokenType.RightBracket);
            }

            // {
            if (code == '{')
            {
                _position++;
                return new CssToken(CssTokenType.LeftBrace);
            }

            // }
            if (code == '}')
            {
                _position++;
                return new CssToken(CssTokenType.RightBrace);
            }

            // Digit
            if (IsDigit(code))
            {
                return ConsumeNumericToken();
            }

            // Name start
            if (IsNameStart(code))
            {
                return ConsumeIdentLikeToken();
            }

            // Anything else
            _position++;
            return new CssToken(CssTokenType.Delim, code);
        }

        private void ConsumeComments()
        {
            // If /* consume until */
            while (_position + 1 < _length && _input[_position] == '/' && _input[_position + 1] == '*')
            {
                _position += 2; // skip /*
                while (_position < _length)
                {
                    if (_position + 1 < _length && _input[_position] == '*' && _input[_position + 1] == '/')
                    {
                        _position += 2;
                        break;
                    }
                    _position++;
                }
            }
        }

        private void ConsumeWhitespace()
        {
            while (_position < _length && IsWhitespace(_input[_position]))
            {
                _position++;
            }
        }

        private CssToken ConsumeStringToken()
        {
            char ending = _input[_position]; // " or '
            _position++;
            
            var sb = new StringBuilder();
            while (_position < _length)
            {
                char c = _input[_position];
                
                if (c == ending)
                {
                    _position++;
                    return new CssToken(CssTokenType.String, sb.ToString());
                }
                if (c == '\n')
                {
                    // Bad string
                    _position++; 
                    return new CssToken(CssTokenType.BadString);
                }
                if (c == '\\')
                {
                    // Escape
                     _position++;
                     if (_position >= _length) break; // EOF
                     if (_input[_position] == '\n')
                     {
                         _position++; // Escaped newline, ignore
                         continue;
                     }
                     sb.Append(ConsumeEscape());
                     continue;
                }
                
                sb.Append(c);
                _position++;
            }
            
            return new CssToken(CssTokenType.String, sb.ToString());
        }
        
        private CssToken ConsumeNumericToken()
        {
             // Placeholder implementation of number consumption
             // Spec says: Use numeric state machine. 
             // Here we use a simplified version for Phase 3.1
             string numberStr = ConsumeNumber();
             double number = double.Parse(numberStr, CultureInfo.InvariantCulture);
             
             // Check % or identifier (Dimension)
             if (CheckThreeCodePoints(0, IsIdentifierStart)) // Wait, check if next starts ident
             {
                 string unit = ConsumeName();
                 return new CssToken(CssTokenType.Dimension, number, unit);
             }
             if (_position < _length && _input[_position] == '%')
             {
                 _position++;
                 return new CssToken(CssTokenType.Percentage, number);
             }
             
             return new CssToken(CssTokenType.Number, number);
        }

        private CssToken ConsumeIdentLikeToken()
        {
             string name = ConsumeName();
             
             if (name.Equals("url", StringComparison.OrdinalIgnoreCase) && 
                 _position < _length && _input[_position] == '(')
             {
                 _position++; // (
                 
                 // Check if there is whitespace then quote
                 int tempPos = _position;
                 while(tempPos < _length && IsWhitespace(_input[tempPos])) tempPos++;
                 if (tempPos >= _length) 
                 {
                     // EOF inside url( -> function
                     return new CssToken(CssTokenType.Function, name);
                 }
                 if (_input[tempPos] == '"' || _input[tempPos] == '\'')
                 {
                     return new CssToken(CssTokenType.Function, name);
                 }
                 
                 // Otherwise unquoted url
                 return ConsumeUrlToken();
             }
             
             if (_position < _length && _input[_position] == '(')
             {
                 _position++;
                 return new CssToken(CssTokenType.Function, name);
             }
             
             return new CssToken(CssTokenType.Ident, name);
        }

        private CssToken ConsumeUrlToken()
        {
            // Consume as much whitespace as possible
            ConsumeWhitespace();
            
            if (_position >= _length) 
            {
                return new CssToken(CssTokenType.Url, "");
            }

            var sb = new StringBuilder();
            while (_position < _length)
            {
                char c = _input[_position];
                
                if (c == ')')
                {
                    _position++;
                    return new CssToken(CssTokenType.Url, sb.ToString());
                }
                if (c == '"' || c == '\'' || c == '(' || IsNonPrintable(c))
                {
                    // Parse error
                    ConsumeBadUrlRemnants();
                    return new CssToken(CssTokenType.BadUrl);
                }
                if (IsWhitespace(c))
                {
                    ConsumeWhitespace();
                    if (_position < _length && _input[_position] == ')')
                    {
                        _position++;
                        return new CssToken(CssTokenType.Url, sb.ToString());
                    }
                    ConsumeBadUrlRemnants();
                    return new CssToken(CssTokenType.BadUrl);
                }
                if (c == '\\')
                {
                    if (IsValidEscape(_position))
                    {
                        _position++;
                        sb.Append(ConsumeEscape());
                        continue;
                    }
                    // Parse error
                    ConsumeBadUrlRemnants();
                    return new CssToken(CssTokenType.BadUrl);
                }
                
                sb.Append(c);
                _position++;
            }
            return new CssToken(CssTokenType.Url, sb.ToString());
        }

        private void ConsumeBadUrlRemnants()
        {
            while (_position < _length)
            {
                if (_input[_position] == ')')
                {
                    _position++;
                    break;
                }
                if (IsValidEscape(_position))
                {
                    _position++;
                    ConsumeEscape(); // ignore result
                }
                else
                {
                    _position++;
                }
            }
        }

        private string ConsumeName()
        {
            var sb = new StringBuilder();
            while (_position < _length)
            {
                char c = _input[_position];
                if (IsNameChar(c))
                {
                    sb.Append(c);
                    _position++;
                }
                else if (IsValidEscape(_position))
                {
                    _position++;
                    sb.Append(ConsumeEscape());
                }
                else
                {
                    break;
                }
            }
            return sb.ToString();
        }
        
        private string ConsumeNumber()
        {
            int start = _position;
            // Consume [+-]? digit+ (. digit+)? ((e|E) [+-]? digit+)?
            if (_position < _length && (_input[_position] == '+' || _input[_position] == '-')) _position++;
            while (_position < _length && IsDigit(_input[_position])) _position++;
            if (_position + 1 < _length && _input[_position] == '.' && IsDigit(_input[_position + 1]))
            {
                _position += 2; // . and digit
                while (_position < _length && IsDigit(_input[_position])) _position++;
            }
            if (_position + 1 < _length && (_input[_position] == 'e' || _input[_position] == 'E'))
            {
                // Exponent
                int savedPos = _position;
                _position++;
                if (_position < _length && (_input[_position] == '+' || _input[_position] == '-')) _position++;
                if (_position < _length && IsDigit(_input[_position]))
                {
                    while (_position < _length && IsDigit(_input[_position])) _position++;
                }
                else
                {
                    _position = savedPos; // Backtrack
                }
            }
            
            return _input.Substring(start, _position - start);
        }

        private char ConsumeEscape()
        {
            if (_position >= _length) return '\uFFFD';
            
            char c = _input[_position];
            if (IsHexDigit(c))
            {
                 int start = _position;
                 int max = Math.Min(_length, _position + 6);
                 while (_position < max && IsHexDigit(_input[_position])) _position++;
                 
                 string hex = _input.Substring(start, _position - start);
                 int codePoint = int.Parse(hex, NumberStyles.HexNumber);
                 
                 if (_position < _length && IsWhitespace(_input[_position])) _position++;
                 
                 if (codePoint == 0 || (codePoint >= 0xD800 && codePoint <= 0xDFFF) || codePoint > 0x10FFFF)
                    return '\uFFFD';
                    
                 return char.ConvertFromUtf32(codePoint)[0]; 
            }
            
            _position++; // Spec says consume the char if not hex
            return c;
        }

        // Helpers
        private static bool IsWhitespace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\f';
        private static bool IsDigit(char c) => c >= '0' && c <= '9';
        private static bool IsHexDigit(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        private static bool IsUppercaseLetter(char c) => c >= 'A' && c <= 'Z';
        private static bool IsLowercaseLetter(char c) => c >= 'a' && c <= 'z';
        private static bool IsLetter(char c) => IsUppercaseLetter(c) || IsLowercaseLetter(c);
        private static bool IsNonAscii(char c) => c >= 0x0080;
        private static bool IsNameStart(char c) => IsLetter(c) || IsNonAscii(c) || c == '_';
        private static bool IsNameChar(char c) => IsNameStart(c) || IsDigit(c) || c == '-';
        private static bool IsNonPrintable(char c) => (c >= 0 && c <= 8) || c == 0x0B || (c >= 0x0E && c <= 0x1F) || c == 0x7F;

        private static bool IsIdentifierStart(char c) => IsNameStart(c); 

        private bool StartsWithNumber()
        {
            if (_position >= _length) return false;
            char c = _input[_position];
            if (IsDigit(c)) return true;
            if (c == '+' || c == '-') return _position + 1 < _length && (IsDigit(_input[_position + 1]) || (_input[_position + 1] == '.' && _position + 2 < _length && IsDigit(_input[_position + 2])));
            if (c == '.') return _position + 1 < _length && IsDigit(_input[_position + 1]);
            return false;
        }
        
        private bool StartsWithIdentifier()
        {
             return WouldStartIdentifier(_input, _position);
        }
        
        private static bool WouldStartIdentifier(string str, int index)
        {
            if (index >= str.Length) return false;
            char c = str[index];
            if (c == '-')
            {
                if (index + 1 >= str.Length) return false;
                char c2 = str[index + 1];
                return IsNameStart(c2) || c2 == '-' || AreValidEscape(str, index + 1);
            }
            if (IsNameStart(c)) return true;
            if (c == '\\') return AreValidEscape(str, index);
            return false;
        }
        
        private bool IsValidEscape(int pos) => AreValidEscape(_input, pos);
        
        private static bool AreValidEscape(string str, int index)
        {
            if (index >= str.Length || str[index] != '\\') return false;
            if (index + 1 >= str.Length) return false;
            return str[index + 1] != '\n';
        }
        
        private bool CheckThreeCodePoints(int offset, Func<char, bool> check)
        {
             if (_position + offset >= _length) return false;
             return check(_input[_position + offset]);
        }
        
        private char PeekAt(int offset)
        {
            if (_position + offset >= _length) return '\0';
            return _input[_position + offset];
        }
    }
}
