using FenBrowser.Js.Source;
using System.Globalization;
using System.Text;

namespace FenBrowser.Js.Lexer;

public sealed class JsLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete",
        "do", "else", "export", "extends", "finally", "for", "function", "if", "import", "in", "of",
        "instanceof", "let", "new", "return", "super", "switch", "this", "throw", "try", "typeof",
        "var", "void", "while", "with", "yield", "true", "false", "null"
    };

    private readonly string _source;
    private int _index;
    private int _line = 1;
    private int _column = 1;
    private bool _atLineStart = true;
    private Token? _lastSignificantToken;
    private Token? _pendingMalformedTrivia;

    public JsLexer(SourceText source)
    {
        _source = source.Text;
    }

    public IReadOnlyList<Token> LexAll()
    {
        var tokens = new List<Token>();

        while (true)
        {
            SkipTrivia();
            if (_pendingMalformedTrivia is { } malformedTrivia)
            {
                _pendingMalformedTrivia = null;
                tokens.Add(malformedTrivia);
                _lastSignificantToken = malformedTrivia;
                continue;
            }

            if (_index >= _source.Length)
            {
                tokens.Add(new Token(TokenKind.EndOfFile, string.Empty, new SourceSpan(_index, 0, _line, _column)));
                return tokens;
            }

            _atLineStart = false;

            var start = _index;
            var line = _line;
            var column = _column;
            var ch = _source[_index];

            if (ch == '/' && CanStartRegexLiteral())
            {
                if (TryReadRegexLiteral(out var regexText))
                {
                    var token = new Token(TokenKind.RegularExpression, regexText, new SourceSpan(start, regexText.Length, line, column));
                    tokens.Add(token);
                    _lastSignificantToken = token;
                    continue;
                }
            }

            if (IsIdentifierStart(ch) || (ch == '\\' && IsUnicodeEscapeStart(_index)))
            {
                var builder = new StringBuilder();
                var hadEscape = false;
                var malformedIdentifier = false;
                if (ch == '\\')
                {
                    hadEscape = true;
                    if (!TryReadUnicodeEscape(out var escapedStart))
                    {
                        _index++;
                        _column++;
                        var invalidEscape = new Token(TokenKind.Unknown, ch.ToString(), new SourceSpan(start, 1, line, column));
                        tokens.Add(invalidEscape);
                        _lastSignificantToken = invalidEscape;
                        continue;
                    }

                    if (IsLineTerminator(escapedStart) || IsWhiteSpace(escapedStart) || !IsIdentifierStart(escapedStart))
                    {
                        malformedIdentifier = true;
                    }

                    builder.Append(escapedStart);
                }
                else
                {
                    builder.Append(ch);
                    _index++;
                    _column++;
                }

                while (_index < _source.Length)
                {
                    var c = _source[_index];
                    if (IsIdentifierPart(c))
                    {
                        builder.Append(c);
                        _index++;
                        _column++;
                        continue;
                    }

                    if (c == '\\' && IsUnicodeEscapeStart(_index))
                    {
                        hadEscape = true;
                        if (!TryReadUnicodeEscape(out var escaped) ||
                            IsLineTerminator(escaped) ||
                            IsWhiteSpace(escaped) ||
                            !IsIdentifierPart(escaped))
                        {
                            malformedIdentifier = true;
                            break;
                        }

                        builder.Append(escaped);
                        continue;
                    }

                    break;
                }

                var text = malformedIdentifier ? _source[start.._index] : builder.ToString();
                var kind = malformedIdentifier
                    ? TokenKind.Unknown
                    : !hadEscape && Keywords.Contains(text)
                        ? TokenKind.Keyword
                        : TokenKind.Identifier;
                var token = new Token(kind, text, new SourceSpan(start, _index - start, line, column));
                tokens.Add(token);
                _lastSignificantToken = token;
                continue;
            }

            if (char.IsDigit(ch))
            {
                _index++;
                _column++;
                var isNonDecimalRadix = false;
                if (ch == '0' && _index < _source.Length)
                {
                    var radix = _source[_index];
                    if (radix is 'x' or 'X')
                    {
                        isNonDecimalRadix = true;
                        _index++;
                        _column++;
                        while (_index < _source.Length && (IsHexDigit(_source[_index]) || _source[_index] == '_'))
                        {
                            _index++;
                            _column++;
                        }
                    }
                    else if (radix is 'o' or 'O')
                    {
                        isNonDecimalRadix = true;
                        _index++;
                        _column++;
                        while (_index < _source.Length && (IsOctDigit(_source[_index]) || _source[_index] == '_'))
                        {
                            _index++;
                            _column++;
                        }
                    }
                    else if (radix is 'b' or 'B')
                    {
                        isNonDecimalRadix = true;
                        _index++;
                        _column++;
                        while (_index < _source.Length && (IsBinDigit(_source[_index]) || _source[_index] == '_'))
                        {
                            _index++;
                            _column++;
                        }
                    }
                    else
                    {
                        while (_index < _source.Length && (char.IsDigit(_source[_index]) || _source[_index] == '_'))
                        {
                            _index++;
                            _column++;
                        }
                    }
                }
                else
                {
                    while (_index < _source.Length && (char.IsDigit(_source[_index]) || _source[_index] == '_'))
                    {
                        _index++;
                        _column++;
                    }
                }

                if (!isNonDecimalRadix)
                {
                    if (_index < _source.Length && _source[_index] == '.' && (_index + 1 >= _source.Length || _source[_index + 1] != '.'))
                    {
                        _index++;
                        _column++;
                        while (_index < _source.Length && (char.IsDigit(_source[_index]) || _source[_index] == '_'))
                        {
                            _index++;
                            _column++;
                        }
                    }

                    if (_index < _source.Length && (_source[_index] == 'e' || _source[_index] == 'E'))
                    {
                        var expStart = _index;
                        var expIndex = _index + 1;
                        if (expIndex < _source.Length && (_source[expIndex] == '+' || _source[expIndex] == '-'))
                        {
                            expIndex++;
                        }

                        var hasExponentDigits = false;
                        while (expIndex < _source.Length && (char.IsDigit(_source[expIndex]) || _source[expIndex] == '_'))
                        {
                            if (_source[expIndex] != '_')
                            {
                                hasExponentDigits = true;
                            }
                            expIndex++;
                        }

                        if (hasExponentDigits)
                        {
                            _column += expIndex - expStart;
                            _index = expIndex;
                        }
                    }
                }

                if (_index < _source.Length && _source[_index] == 'n')
                {
                    _index++;
                    _column++;
                }

                var token = new Token(TokenKind.Number, _source[start.._index], new SourceSpan(start, _index - start, line, column));
                tokens.Add(token);
                _lastSignificantToken = token;
                continue;
            }

            if (ch == '.' && _index + 1 < _source.Length && char.IsDigit(_source[_index + 1]))
            {
                _index++;
                _column++;
                while (_index < _source.Length && (char.IsDigit(_source[_index]) || _source[_index] == '_'))
                {
                    _index++;
                    _column++;
                }

                if (_index < _source.Length && (_source[_index] == 'e' || _source[_index] == 'E'))
                {
                    var expStart = _index;
                    var expIndex = _index + 1;
                    if (expIndex < _source.Length && (_source[expIndex] == '+' || _source[expIndex] == '-'))
                    {
                        expIndex++;
                    }

                    var hasExponentDigits = false;
                    while (expIndex < _source.Length && (char.IsDigit(_source[expIndex]) || _source[expIndex] == '_'))
                    {
                        if (_source[expIndex] != '_')
                        {
                            hasExponentDigits = true;
                        }

                        expIndex++;
                    }

                    if (hasExponentDigits)
                    {
                        _column += expIndex - expStart;
                        _index = expIndex;
                    }
                }

                var token = new Token(TokenKind.Number, _source[start.._index], new SourceSpan(start, _index - start, line, column));
                tokens.Add(token);
                _lastSignificantToken = token;
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                var quote = ch;
                _index++;
                _column++;
                var malformed = false;
                while (_index < _source.Length)
                {
                    var c = _source[_index];
                    if (IsLineTerminator(c))
                    {
                        malformed = true;
                        AdvanceLineTerminator();
                        break;
                    }

                    _index++;
                    _column++;
                    if (c == '\\' && _index < _source.Length)
                    {
                        if (IsLineTerminator(_source[_index]))
                        {
                            AdvanceLineTerminator();
                        }
                        else
                        {
                            _index++;
                            _column++;
                        }

                        continue;
                    }

                    if (c == quote)
                    {
                        break;
                    }
                }

                var token = new Token(malformed ? TokenKind.Unknown : TokenKind.String, _source[start.._index], new SourceSpan(start, _index - start, line, column));
                tokens.Add(token);
                _lastSignificantToken = token;
                continue;
            }

            if (ch == '`')
            {
                var text = ReadTemplateLiteralToken();
                var token = new Token(TokenKind.Template, text, new SourceSpan(start, text.Length, line, column));
                tokens.Add(token);
                _lastSignificantToken = token;
                continue;
            }

            if (TryReadPunctuator(out var punctuatorText))
            {
                var token = new Token(TokenKind.Punctuator, punctuatorText, new SourceSpan(start, punctuatorText.Length, line, column));
                tokens.Add(token);
                _lastSignificantToken = token;
                continue;
            }

            _index++;
            _column++;
            var unknown = new Token(TokenKind.Unknown, ch.ToString(), new SourceSpan(start, 1, line, column));
            tokens.Add(unknown);
            _lastSignificantToken = unknown;
        }
    }

    private void SkipTrivia()
    {
        while (_index < _source.Length)
        {
            var ch = _source[_index];
            if (IsWhiteSpace(ch))
            {
                _index++;
                _column++;
                continue;
            }

            if (IsLineTerminator(ch))
            {
                AdvanceLineTerminator();
                continue;
            }

            if (_index == 0 && _source.Length >= 2 && _source[0] == '#' && _source[1] == '!')
            {
                _index += 2;
                _column += 2;
                while (_index < _source.Length && !IsLineTerminator(_source[_index]))
                {
                    _index++;
                    _column++;
                }

                continue;
            }

            if (_atLineStart && _index + 2 < _source.Length && _source[_index] == '-' && _source[_index + 1] == '-' && _source[_index + 2] == '>')
            {
                _index += 3;
                _column += 3;
                while (_index < _source.Length && !IsLineTerminator(_source[_index]))
                {
                    _index++;
                    _column++;
                }

                continue;
            }

            if (_index + 3 < _source.Length && _source[_index] == '<' && _source[_index + 1] == '!' && _source[_index + 2] == '-' && _source[_index + 3] == '-')
            {
                _index += 4;
                _column += 4;
                while (_index < _source.Length && !IsLineTerminator(_source[_index]))
                {
                    _index++;
                    _column++;
                }

                continue;
            }

            if (ch == '/' && _index + 1 < _source.Length)
            {
                var next = _source[_index + 1];
                if (next == '/')
                {
                    _index += 2;
                    _column += 2;
                    while (_index < _source.Length && !IsLineTerminator(_source[_index]))
                    {
                        _index++;
                        _column++;
                    }

                    continue;
                }

                if (next == '*')
                {
                    var commentStart = _index;
                    var commentLine = _line;
                    var commentColumn = _column;
                    _index += 2;
                    _column += 2;
                    var closed = false;
                    while (_index + 1 < _source.Length)
                    {
                        if (_source[_index] == '*' && _source[_index + 1] == '/')
                        {
                            _index += 2;
                            _column += 2;
                            closed = true;
                            break;
                        }

                        if (IsLineTerminator(_source[_index]))
                        {
                            AdvanceLineTerminator();
                        }
                        else
                        {
                            _index++;
                            _column++;
                        }
                    }

                    if (!closed)
                    {
                        _pendingMalformedTrivia = new Token(
                            TokenKind.Unknown,
                            _source[commentStart..],
                            new SourceSpan(commentStart, _source.Length - commentStart, commentLine, commentColumn));
                        _index = _source.Length;
                    }

                    continue;
                }
            }

            break;
        }
    }

    private bool TryReadPunctuator(out string text)
    {
        if (_index + 2 < _source.Length)
        {
            var three = _source.Substring(_index, 3);
            if (three is "===" or "!==" or "..." or "&&=" or "||=" or "??=" or ">>>" or "<<=" or ">>=" or "**=")
            {
                _index += 3;
                _column += 3;
                text = three;
                return true;
            }
        }

        if (_index + 1 < _source.Length)
        {
            var two = _source.Substring(_index, 2);
            if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||" or "??" or "+=" or "-=" or "*=" or "/=" or "%=" or "++" or "--" or "<<" or ">>" or "**")
            {
                _index += 2;
                _column += 2;
                text = two;
                return true;
            }
        }

        var ch = _source[_index];
        if (ch is '{' or '}' or '(' or ')' or '[' or ']' or ';' or ',' or '.' or ':' or '?' or '+' or '-' or '*' or '/' or '%' or '=' or '!' or '<' or '>' or '&' or '|')
        {
            _index++;
            _column++;
            text = ch.ToString();
            return true;
        }

        text = string.Empty;
        return false;
    }

    private bool CanStartRegexLiteral()
    {
        if (_lastSignificantToken is null)
        {
            return true;
        }

        var token = _lastSignificantToken.Value;
        if (token.Kind == TokenKind.Keyword)
        {
            return token.Text is "return" or "throw" or "case" or "typeof" or "new" or "delete" or "void" or "in" or "instanceof";
        }

        if (token.Kind != TokenKind.Punctuator)
        {
            return false;
        }

        return token.Text is "(" or "{" or "[" or "," or ";" or ":" or "?" or "=" or "==" or "!=" or "<" or ">" or "<=" or ">=" or "&&" or "||" or "!" or "+" or "-" or "*" or "%" or "/";
    }

    private bool TryReadRegexLiteral(out string rawText)
    {
        var start = _index;
        var i = _index + 1;
        var escaped = false;
        var inCharClass = false;
        while (i < _source.Length)
        {
            var ch = _source[i];
            if (!escaped)
            {
                if (ch == '\\')
                {
                    escaped = true;
                    i++;
                    continue;
                }

                if (ch == '[')
                {
                    inCharClass = true;
                    i++;
                    continue;
                }

                if (ch == ']' && inCharClass)
                {
                    inCharClass = false;
                    i++;
                    continue;
                }

                if (ch == '/' && !inCharClass)
                {
                    i++;
                    while (i < _source.Length && char.IsLetter(_source[i]))
                    {
                        i++;
                    }

                    rawText = _source[start..i];
                    _column += i - _index;
                    _index = i;
                    return true;
                }

                if (IsLineTerminator(ch))
                {
                    break;
                }
            }
            else
            {
                escaped = false;
            }

            i++;
        }

        rawText = string.Empty;
        return false;
    }

    private static bool IsHexDigit(char ch) =>
        (ch >= '0' && ch <= '9') ||
        (ch >= 'a' && ch <= 'f') ||
        (ch >= 'A' && ch <= 'F');

    private static bool IsOctDigit(char ch) => ch >= '0' && ch <= '7';

    private static bool IsBinDigit(char ch) => ch == '0' || ch == '1';

    private bool IsUnicodeEscapeStart(int index)
    {
        if (index + 1 >= _source.Length || _source[index] != '\\' || _source[index + 1] != 'u')
        {
            return false;
        }

        if (index + 2 >= _source.Length)
        {
            return false;
        }

        if (_source[index + 2] == '{')
        {
            return true;
        }

        return index + 5 < _source.Length;
    }

    private bool TryReadUnicodeEscape(out char value)
    {
        value = '\0';
        if (!IsUnicodeEscapeStart(_index))
        {
            return false;
        }

        _index += 2; // \u
        _column += 2;

        if (_index < _source.Length && _source[_index] == '{')
        {
            _index++;
            _column++;
            var hexStart = _index;
            while (_index < _source.Length && _source[_index] != '}')
            {
                if (!IsHexDigit(_source[_index]))
                {
                    return false;
                }

                _index++;
                _column++;
            }

            if (_index >= _source.Length || _source[_index] != '}')
            {
                return false;
            }

            var hex = _source[hexStart.._index];
            _index++;
            _column++;
            if (hex.Length == 0 || hex.Length > 6)
            {
                return false;
            }

            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var codePoint))
            {
                return false;
            }

            value = (char)codePoint;
            return true;
        }

        if (_index + 3 >= _source.Length)
        {
            return false;
        }

        var digits = _source.Substring(_index, 4);
        for (var i = 0; i < digits.Length; i++)
        {
            if (!IsHexDigit(digits[i]))
            {
                return false;
            }
        }

        _index += 4;
        _column += 4;
        var code = int.Parse(digits, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        value = (char)code;
        return true;
    }

    private static bool IsLineTerminator(char ch) => ch == '\n' || ch == '\r' || ch == '\u2028' || ch == '\u2029';

    private static bool IsWhiteSpace(char ch) =>
        ch is '\t' or '\v' or '\f' or ' ' or '\u00A0' or '\uFEFF' ||
        (!IsLineTerminator(ch) && CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.SpaceSeparator);

    private static bool IsIdentifierStart(char ch) => char.IsLetter(ch) || ch == '_' || ch == '$';

    private static bool IsIdentifierPart(char ch) => char.IsLetterOrDigit(ch) || ch == '_' || ch == '$';

    private void AdvanceLineTerminator()
    {
        if (_source[_index] == '\r' && _index + 1 < _source.Length && _source[_index + 1] == '\n')
        {
            _index += 2;
        }
        else
        {
            _index++;
        }

        _line++;
        _column = 1;
        _atLineStart = true;
    }

    private string ReadTemplateLiteralToken()
    {
        var start = _index;
        _index++;
        _column++;

        var escaped = false;
        while (_index < _source.Length)
        {
            var c = _source[_index];
            if (IsLineTerminator(c))
            {
                AdvanceLineTerminator();
            }
            else
            {
                _index++;
                _column++;
            }

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '`')
            {
                break;
            }
        }

        return _source[start.._index];
    }
}
