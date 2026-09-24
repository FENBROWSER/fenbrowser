// SpecRef: CSS Syntax Module Level 3 tokenization algorithm
// CapabilityId: CSS-SYNTAX-TOKENIZATION-01
// Determinism: strict
// FallbackPolicy: clean-unsupported
using System;
using System.Collections.Generic;
using System.Text;
using System.Globalization;

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
            _input = Preprocess(input);
            _length = _input.Length;
            _position = 0;
        }

        public CssToken Peek()
        {
            var saved = _position;
            var token = Consume();
            _position = saved;
            return token;
        }

        public int SavePosition() => _position;

        public void RestorePosition(int position)
        {
            _position = position;
        }

        // Whitespace tokens keep their text (custom property values and selector
        // serialisation preserve it), but nearly every run is spaces or one newline
        // followed by indentation, so those come from a table instead of a substring.
        private const int CachedWhitespaceRun = 32;
        private static readonly string[] SpaceRuns = BuildWhitespaceRuns(string.Empty);
        private static readonly string[] NewlineRuns = BuildWhitespaceRuns("\n");

        private static string[] BuildWhitespaceRuns(string prefix)
        {
            var runs = new string[CachedWhitespaceRun];
            for (int i = 0; i < runs.Length; i++)
            {
                runs[i] = prefix + new string(' ', i);
            }
            return runs;
        }

        private static string WhitespaceText(string input, int start, int length)
        {
            int spacesFrom = input[start] == '\n' ? start + 1 : start;
            int spaces = start + length - spacesFrom;
            if (spaces < CachedWhitespaceRun && input.AsSpan(spacesFrom, spaces).IndexOfAnyExcept(' ') < 0)
            {
                return spacesFrom == start ? SpaceRuns[spaces] : NewlineRuns[spaces];
            }
            return input.Substring(start, length);
        }

        private static string Preprocess(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            if (input[0] == '\uFEFF') input = input.Substring(1);

            int firstSpecialCharacter = 0;
            while (firstSpecialCharacter < input.Length)
            {
                char c = input[firstSpecialCharacter];
                if (c == '\r' || c == '\f' || c == '\0' || char.IsSurrogate(c))
                {
                    break;
                }

                firstSpecialCharacter++;
            }

            if (firstSpecialCharacter == input.Length)
            {
                return input;
            }

            var sb = new StringBuilder(input.Length);
            sb.Append(input, 0, firstSpecialCharacter);
            for (int i = firstSpecialCharacter; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '\r')
                {
                    // CSS input preprocessing: CRLF -> LF and CR -> LF.
                    if (i + 1 < input.Length && input[i + 1] == '\n')
                    {
                        i++;
                    }
                    sb.Append('\n');
                }
                else if (c == '\f')
                {
                    // Form-feed is normalized to LF by CSS Syntax preprocessing.
                    sb.Append('\n');
                }
                else if (c == '\0')
                {
                    sb.Append('\uFFFD');
                }
                else if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]))
                    {
                        sb.Append(c);
                        sb.Append(input[++i]);
                    }
                    else
                    {
                        sb.Append('\uFFFD');
                    }
                }
                else if (char.IsLowSurrogate(c))
                {
                    sb.Append('\uFFFD');
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
            ConsumeComments();

            if (_position >= _length)
                return new CssToken(CssTokenType.EOF);

            char code = _input[_position];

            if (IsWhitespace(code))
            {
                int start = _position;
                ConsumeWhitespace();
                return new CssToken(CssTokenType.Whitespace, WhitespaceText(_input, start, _position - start));
            }

            if (code == '"' || code == '\'')
            {
                return ConsumeStringToken();
            }

            if (code == '#')
            {
                if (IsNameChar(PeekAt(1)) || AreValidEscape(_input, _position + 1))
                {
                    _position++;
                    bool wouldStartIdent = WouldStartIdentifier(_input, _position);
                    string name = ConsumeName();
                    return new CssToken(CssTokenType.Hash, name, wouldStartIdent ? HashType.Id : HashType.Unrestricted);
                }

                _position++;
                return new CssToken(CssTokenType.Delim, '#');
            }

            if (code == '(')
            {
                _position++;
                return new CssToken(CssTokenType.LeftParen);
            }

            if (code == ')')
            {
                _position++;
                return new CssToken(CssTokenType.RightParen);
            }

            if (code == '+')
            {
                if (StartsWithNumber())
                {
                    return ConsumeNumericToken();
                }
                _position++;
                return new CssToken(CssTokenType.Delim, '+');
            }

            if (code == ',')
            {
                _position++;
                return new CssToken(CssTokenType.Comma);
            }

            if (code == '-')
            {
                if (StartsWithNumber())
                {
                    return ConsumeNumericToken();
                }
                if (_position + 2 < _length && _input[_position + 1] == '-' && _input[_position + 2] == '>')
                {
                    _position += 3;
                    return new CssToken(CssTokenType.CDC);
                }
                if (StartsWithIdentifier())
                {
                    return ConsumeIdentLikeToken();
                }
                _position++;
                return new CssToken(CssTokenType.Delim, '-');
            }

            if (code == '.')
            {
                if (StartsWithNumber())
                {
                    return ConsumeNumericToken();
                }
                _position++;
                return new CssToken(CssTokenType.Delim, '.');
            }

            if (code == ':')
            {
                _position++;
                return new CssToken(CssTokenType.Colon);
            }

            if (code == ';')
            {
                _position++;
                return new CssToken(CssTokenType.Semicolon);
            }

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

            if (code == '[')
            {
                _position++;
                return new CssToken(CssTokenType.LeftBracket);
            }

            if (code == '\\')
            {
                if (IsValidEscape(_position))
                {
                    return ConsumeIdentLikeToken();
                }
                _position++;
                return new CssToken(CssTokenType.Delim, '\\');
            }

            if (code == ']')
            {
                _position++;
                return new CssToken(CssTokenType.RightBracket);
            }

            if (code == '{')
            {
                _position++;
                return new CssToken(CssTokenType.LeftBrace);
            }

            if (code == '}')
            {
                _position++;
                return new CssToken(CssTokenType.RightBrace);
            }

            if (IsDigit(code))
            {
                return ConsumeNumericToken();
            }

            if (IsNameStart(code))
            {
                return ConsumeIdentLikeToken();
            }

            _position++;
            return new CssToken(CssTokenType.Delim, code);
        }

        private void ConsumeComments()
        {
            while (_position + 1 < _length && _input[_position] == '/' && _input[_position + 1] == '*')
            {
                _position += 2;
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
            char ending = _input[_position];
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
                    // CSS Syntax says to reconsume the newline in the main tokenizer.
                    // Do not advance here; the next Consume() will emit whitespace.
                    return new CssToken(CssTokenType.BadString);
                }
                if (c == '\\')
                {
                    _position++;
                    if (_position >= _length) break;
                    if (_input[_position] == '\n')
                    {
                        _position++;
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
            string numberStr = ConsumeNumber();
            if (!double.TryParse(numberStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                number = numberStr.StartsWith("-", StringComparison.Ordinal)
                    ? double.NegativeInfinity
                    : double.PositiveInfinity;
            }

            // A dimension is created when the next three code points would start an
            // identifier, including hyphen-leading and escaped units.
            if (WouldStartIdentifier(_input, _position))
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
                _position++;

                int tempPos = _position;
                while (tempPos < _length && IsWhitespace(_input[tempPos])) tempPos++;
                if (tempPos >= _length)
                {
                    return new CssToken(CssTokenType.Function, name);
                }
                if (_input[tempPos] == '"' || _input[tempPos] == '\'')
                {
                    return new CssToken(CssTokenType.Function, name);
                }

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
                    _ = ConsumeEscape();
                }
                else
                {
                    _position++;
                }
            }
        }

        private string ConsumeName()
        {
            int nameStart = _position;
            int segmentStart = _position;
            StringBuilder sb = null;
            while (_position < _length)
            {
                char c = _input[_position];
                if (IsNameChar(c))
                {
                    _position++;
                }
                else if (IsValidEscape(_position))
                {
                    sb ??= new StringBuilder();
                    sb.Append(_input, segmentStart, _position - segmentStart);
                    _position++;
                    sb.Append(ConsumeEscape());
                    segmentStart = _position;
                }
                else
                {
                    break;
                }
            }

            if (sb == null)
            {
                return _input.Substring(nameStart, _position - nameStart);
            }

            sb.Append(_input, segmentStart, _position - segmentStart);
            return sb.ToString();
        }

        private string ConsumeNumber()
        {
            int start = _position;
            if (_position < _length && (_input[_position] == '+' || _input[_position] == '-')) _position++;
            while (_position < _length && IsDigit(_input[_position])) _position++;
            if (_position + 1 < _length && _input[_position] == '.' && IsDigit(_input[_position + 1]))
            {
                _position += 2;
                while (_position < _length && IsDigit(_input[_position])) _position++;
            }
            if (_position + 1 < _length && (_input[_position] == 'e' || _input[_position] == 'E'))
            {
                int savedPos = _position;
                _position++;
                if (_position < _length && (_input[_position] == '+' || _input[_position] == '-')) _position++;
                if (_position < _length && IsDigit(_input[_position]))
                {
                    while (_position < _length && IsDigit(_input[_position])) _position++;
                }
                else
                {
                    _position = savedPos;
                }
            }

            return _input.Substring(start, _position - start);
        }

        private string ConsumeEscape()
        {
            if (_position >= _length) return "\uFFFD";

            char c = _input[_position];
            if (IsHexDigit(c))
            {
                int start = _position;
                int max = Math.Min(_length, _position + 6);
                while (_position < max && IsHexDigit(_input[_position])) _position++;

                string hex = _input.Substring(start, _position - start);
                int codePoint = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                if (_position < _length && IsWhitespace(_input[_position])) _position++;

                if (codePoint == 0 ||
                    (codePoint >= 0xD800 && codePoint <= 0xDFFF) ||
                    codePoint > 0x10FFFF)
                {
                    return "\uFFFD";
                }

                // Non-BMP escapes produce a surrogate pair. Returning string avoids
                // truncating them to only the high surrogate.
                return char.ConvertFromUtf32(codePoint);
            }

            _position++;
            return c.ToString();
        }

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

        private bool StartsWithNumber()
        {
            if (_position >= _length) return false;
            char c = _input[_position];
            if (IsDigit(c)) return true;
            if (c == '+' || c == '-')
            {
                return _position + 1 < _length &&
                       (IsDigit(_input[_position + 1]) ||
                        (_input[_position + 1] == '.' && _position + 2 < _length && IsDigit(_input[_position + 2])));
            }
            if (c == '.') return _position + 1 < _length && IsDigit(_input[_position + 1]);
            return false;
        }

        private bool StartsWithIdentifier() => WouldStartIdentifier(_input, _position);

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
            if (index + 1 >= str.Length) return true;
            return str[index + 1] != '\n';
        }

        private char PeekAt(int offset)
        {
            if (_position + offset >= _length) return '\0';
            return _input[_position + offset];
        }
    }
}
