using FenBrowser.Js.Source;

namespace FenBrowser.Js.Lexer;

public sealed class JsLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete",
        "do", "else", "export", "extends", "finally", "for", "function", "if", "import", "in", "of",
        "instanceof", "let", "new", "return", "super", "switch", "this", "throw", "try", "typeof",
        "var", "void", "while", "with", "yield"
    };

    private readonly string _source;
    private int _index;
    private int _line = 1;
    private int _column = 1;
    private bool _atLineStart = true;
    private Token? _lastSignificantToken;

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

            if (char.IsLetter(ch) || ch == '_' || ch == '$')
            {
                _index++;
                _column++;
                while (_index < _source.Length)
                {
                    var c = _source[_index];
                    if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$'))
                    {
                        break;
                    }

                    _index++;
                    _column++;
                }

                var text = _source[start.._index];
                var kind = Keywords.Contains(text) ? TokenKind.Keyword : TokenKind.Identifier;
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
                        while (_index < _source.Length && IsHexDigit(_source[_index]))
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
                        while (_index < _source.Length && IsOctDigit(_source[_index]))
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
                        while (_index < _source.Length && IsBinDigit(_source[_index]))
                        {
                            _index++;
                            _column++;
                        }
                    }
                    else
                    {
                        while (_index < _source.Length && char.IsDigit(_source[_index]))
                        {
                            _index++;
                            _column++;
                        }
                    }
                }
                else
                {
                    while (_index < _source.Length && char.IsDigit(_source[_index]))
                    {
                        _index++;
                        _column++;
                    }
                }

                if (!isNonDecimalRadix)
                {
                    if (_index < _source.Length && _source[_index] == '.' && _index + 1 < _source.Length && char.IsDigit(_source[_index + 1]))
                    {
                        _index++;
                        _column++;
                        while (_index < _source.Length && char.IsDigit(_source[_index]))
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
                        while (expIndex < _source.Length && char.IsDigit(_source[expIndex]))
                        {
                            hasExponentDigits = true;
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

            if (ch == '"' || ch == '\'')
            {
                var quote = ch;
                _index++;
                _column++;
                while (_index < _source.Length)
                {
                    var c = _source[_index++];
                    _column++;
                    if (c == '\\' && _index < _source.Length)
                    {
                        _index++;
                        _column++;
                        continue;
                    }

                    if (c == quote)
                    {
                        break;
                    }
                }

                var token = new Token(TokenKind.String, _source[start.._index], new SourceSpan(start, _index - start, line, column));
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
            if (ch == ' ' || ch == '\t')
            {
                _index++;
                _column++;
                continue;
            }

            if (IsLineTerminator(ch))
            {
                _index++;
                _line++;
                _column = 1;
                _atLineStart = true;
                continue;
            }

            if (_atLineStart && _index + 2 < _source.Length && _source[_index] == '-' && _source[_index + 1] == '-' && _source[_index + 2] == '>')
            {
                _index += 3;
                _column += 3;
                while (_index < _source.Length && _source[_index] != '\n')
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
                            _index++;
                            _line++;
                            _column = 1;
                            _atLineStart = true;
                        }
                        else
                        {
                            _index++;
                            _column++;
                        }
                    }

                    if (!closed)
                    {
                        // Unterminated comment is consumed to EOF for crash-safe lexing.
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
            if (three is "===" or "!==" or "..." or "&&=" or "||=" or "??=")
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
            if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||" or "??" or "+=" or "-=" or "*=" or "/=" or "%=" or "++" or "--")
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

    private static bool IsLineTerminator(char ch) => ch == '\n' || ch == '\r' || ch == '\u2028' || ch == '\u2029';

    private string ReadTemplateLiteralToken()
    {
        var start = _index;
        _index++;
        _column++;

        var escaped = false;
        while (_index < _source.Length)
        {
            var c = _source[_index++];
            if (IsLineTerminator(c))
            {
                _line++;
                _column = 1;
            }
            else
            {
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
