using FenBrowser.Js.Source;

namespace FenBrowser.Js.Lexer;

public sealed class JsLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete",
        "do", "else", "export", "extends", "finally", "for", "function", "if", "import", "in",
        "instanceof", "let", "new", "return", "super", "switch", "this", "throw", "try", "typeof",
        "var", "void", "while", "with", "yield"
    };

    private readonly string _source;
    private int _index;
    private int _line = 1;
    private int _column = 1;

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

            var start = _index;
            var line = _line;
            var column = _column;
            var ch = _source[_index];

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
                tokens.Add(new Token(kind, text, new SourceSpan(start, _index - start, line, column)));
                continue;
            }

            if (char.IsDigit(ch))
            {
                _index++;
                _column++;
                while (_index < _source.Length && char.IsDigit(_source[_index]))
                {
                    _index++;
                    _column++;
                }

                tokens.Add(new Token(TokenKind.Number, _source[start.._index], new SourceSpan(start, _index - start, line, column)));
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

                tokens.Add(new Token(TokenKind.String, _source[start.._index], new SourceSpan(start, _index - start, line, column)));
                continue;
            }

            if (IsPunctuator(ch))
            {
                _index++;
                _column++;
                tokens.Add(new Token(TokenKind.Punctuator, ch.ToString(), new SourceSpan(start, 1, line, column)));
                continue;
            }

            _index++;
            _column++;
            tokens.Add(new Token(TokenKind.Unknown, ch.ToString(), new SourceSpan(start, 1, line, column)));
        }
    }

    private void SkipTrivia()
    {
        while (_index < _source.Length)
        {
            var ch = _source[_index];
            if (ch == ' ' || ch == '\t' || ch == '\r')
            {
                _index++;
                _column++;
                continue;
            }

            if (ch == '\n')
            {
                _index++;
                _line++;
                _column = 1;
                continue;
            }

            if (ch == '/' && _index + 1 < _source.Length)
            {
                var next = _source[_index + 1];
                if (next == '/')
                {
                    _index += 2;
                    _column += 2;
                    while (_index < _source.Length && _source[_index] != '\n')
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

                        if (_source[_index] == '\n')
                        {
                            _index++;
                            _line++;
                            _column = 1;
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

    private static bool IsPunctuator(char ch)
    {
        return ch is '{' or '}' or '(' or ')' or '[' or ']' or ';' or ',' or '.' or ':' or '?' or '+' or '-' or '*' or '/' or '%' or '=' or '!' or '<' or '>' or '&' or '|';
    }
}
