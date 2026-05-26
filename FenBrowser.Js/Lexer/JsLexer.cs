using FenBrowser.Js.Source;
using System.Globalization;
using System.Text;

namespace FenBrowser.Js.Lexer;

public sealed class JsLexer
{
    private enum NumericDigitKind
    {
        Decimal,
        Binary,
        Octal,
        Hex
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "async", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete",
        "do", "else", "export", "extends", "finally", "for", "function", "if", "import", "in", "of",
        "instanceof", "let", "new", "return", "super", "switch", "this", "throw", "try", "typeof", "enum",
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

            var canStartIdentifier = TryPeekRune(_index, out var startRune, out var startRuneLength) && IsIdentifierStart(startRune);
            if (canStartIdentifier || (ch == '\\' && IsUnicodeEscapeStart(_index)))
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

                    builder.Append(escapedStart.ToString());
                }
                else
                {
                    builder.Append(_source.AsSpan(_index, startRuneLength));
                    _index += startRuneLength;
                    _column += startRuneLength;
                }

                while (_index < _source.Length)
                {
                    var c = _source[_index];
                    if (TryPeekRune(_index, out var partRune, out var partRuneLength) && IsIdentifierPart(partRune))
                    {
                        builder.Append(_source.AsSpan(_index, partRuneLength));
                        _index += partRuneLength;
                        _column += partRuneLength;
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

                        builder.Append(escaped.ToString());
                        continue;
                    }

                    break;
                }

                var decodedText = builder.ToString();
                var text = malformedIdentifier ? _source[start.._index] : decodedText;
                var kind = malformedIdentifier
                    ? TokenKind.Unknown
                    : !hadEscape && Keywords.Contains(text)
                        ? TokenKind.Keyword
                        : TokenKind.Identifier;
                var token = new Token(kind, text, new SourceSpan(start, _index - start, line, column), hadEscape);
                tokens.Add(token);
                _lastSignificantToken = token;
                continue;
            }

            if (ch == '#')
            {
                _index++;
                _column++;

                var builder = new StringBuilder("#");
                var hadEscape = false;
                var malformedIdentifier = false;
                if (_index >= _source.Length)
                {
                    var hashToken = new Token(TokenKind.Unknown, "#", new SourceSpan(start, 1, line, column));
                    tokens.Add(hashToken);
                    _lastSignificantToken = hashToken;
                    continue;
                }

                var privateStart = _source[_index];
                if (privateStart == '\\' && IsUnicodeEscapeStart(_index))
                {
                    hadEscape = true;
                    if (!TryReadUnicodeEscape(out var escapedStart) ||
                        IsLineTerminator(escapedStart) ||
                        IsWhiteSpace(escapedStart) ||
                        !IsIdentifierStart(escapedStart))
                    {
                        malformedIdentifier = true;
                    }
                    else
                    {
                        builder.Append(escapedStart.ToString());
                    }
                }
                else if (TryPeekRune(_index, out var privateStartRune, out var privateStartRuneLength) &&
                    IsIdentifierStart(privateStartRune))
                {
                    builder.Append(_source.AsSpan(_index, privateStartRuneLength));
                    _index += privateStartRuneLength;
                    _column += privateStartRuneLength;
                }
                else
                {
                    var hashToken = new Token(TokenKind.Unknown, "#", new SourceSpan(start, 1, line, column));
                    tokens.Add(hashToken);
                    _lastSignificantToken = hashToken;
                    continue;
                }

                while (!malformedIdentifier && _index < _source.Length)
                {
                    var c = _source[_index];
                    if (TryPeekRune(_index, out var partRune, out var partRuneLength) && IsIdentifierPart(partRune))
                    {
                        builder.Append(_source.AsSpan(_index, partRuneLength));
                        _index += partRuneLength;
                        _column += partRuneLength;
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

                        builder.Append(escaped.ToString());
                        continue;
                    }

                    break;
                }

                var text = malformedIdentifier ? _source[start.._index] : builder.ToString();
                var privateToken = new Token(
                    malformedIdentifier ? TokenKind.Unknown : TokenKind.PrivateIdentifier,
                    text,
                    new SourceSpan(start, _index - start, line, column),
                    hadEscape);
                tokens.Add(privateToken);
                _lastSignificantToken = privateToken;
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

                var isBigInt = false;
                if (_index < _source.Length && _source[_index] == 'n')
                {
                    isBigInt = true;
                    _index++;
                    _column++;
                }

                var numberText = _source[start.._index];
                var numberKind = HasInvalidNumericLiteralBoundary() || !HasValidNumericLiteralSeparators(numberText)
                    ? TokenKind.Unknown
                    : isBigInt ? TokenKind.BigInt : TokenKind.Number;
                var token = new Token(numberKind, numberText, new SourceSpan(start, _index - start, line, column));
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

                var numberText = _source[start.._index];
                var numberKind = HasInvalidNumericLiteralBoundary() || !HasValidNumericLiteralSeparators(numberText)
                    ? TokenKind.Unknown
                    : TokenKind.Number;
                var token = new Token(numberKind, numberText, new SourceSpan(start, _index - start, line, column));
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
                    if (c is '\r' or '\n')
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
                        else if (!ConsumeStringEscapeSequence())
                        {
                            malformed = true;
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
                var text = ReadTemplateLiteralToken(out var containsEscape, out var containsInvalidEscape, out var terminated);
                var token = new Token(
                    terminated ? TokenKind.Template : TokenKind.Unknown,
                    text,
                    new SourceSpan(start, text.Length, line, column),
                    ContainsEscape: containsEscape,
                    ContainsInvalidEscape: containsInvalidEscape);
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
        if (_index + 3 < _source.Length)
        {
            var four = _source.Substring(_index, 4);
            if (four is ">>>=")
            {
                _index += 4;
                _column += 4;
                text = four;
                return true;
            }
        }

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
            if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||" or "??" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "^=" or "|=" or "++" or "--" or "<<" or ">>" or "**")
            {
                _index += 2;
                _column += 2;
                text = two;
                return true;
            }
        }

        var ch = _source[_index];
        if (ch is '{' or '}' or '(' or ')' or '[' or ']' or ';' or ',' or '.' or ':' or '?' or '+' or '-' or '*' or '/' or '%' or '=' or '!' or '<' or '>' or '&' or '|' or '^' or '~' or '@')
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

    private bool TryReadUnicodeEscape(out Rune value)
    {
        value = default;
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

            return Rune.TryCreate(codePoint, out value);
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
        return Rune.TryCreate(code, out value);
    }

    private static bool IsLineTerminator(char ch) => ch == '\n' || ch == '\r' || ch == '\u2028' || ch == '\u2029';

    private static bool IsLineTerminator(Rune rune) =>
        rune.Value <= char.MaxValue && IsLineTerminator((char)rune.Value);

    private static bool IsWhiteSpace(char ch) =>
        ch is '\t' or '\v' or '\f' or ' ' or '\u00A0' or '\uFEFF' ||
        (!IsLineTerminator(ch) && CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.SpaceSeparator);

    private static bool IsWhiteSpace(Rune rune) =>
        rune.Value <= char.MaxValue && IsWhiteSpace((char)rune.Value);

    private static bool IsIdentifierStart(Rune rune)
    {
        if (rune.Value == 0x2E2F)
        {
            return false;
        }

        if (rune.Value is 0x5F or 0x24 || IsOtherIdentifierStart(rune))
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.UppercaseLetter => true,
            UnicodeCategory.LowercaseLetter => true,
            UnicodeCategory.TitlecaseLetter => true,
            UnicodeCategory.ModifierLetter => true,
            UnicodeCategory.OtherLetter => true,
            UnicodeCategory.LetterNumber => true,
            _ => false
        };
    }

    private static bool IsIdentifierPart(Rune rune)
    {
        if (IsIdentifierStart(rune) || IsOtherIdentifierContinue(rune) || rune.Value is 0x200C or 0x200D)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.NonSpacingMark => true,
            UnicodeCategory.SpacingCombiningMark => true,
            UnicodeCategory.DecimalDigitNumber => true,
            UnicodeCategory.ConnectorPunctuation => true,
            _ => false
        };
    }

    private static bool IsOtherIdentifierStart(Rune rune) =>
        rune.Value is 0x088F or 0x0C5C or 0x0CDC or 0x1885 or 0x1886 or 0x2118 or 0x212E or 0x309B or 0x309C ||
        rune.Value is >= 0xA7CE and <= 0xA7CF ||
        rune.Value is 0xA7D2 or 0xA7D4 or 0xA7F1 ||
        rune.Value is >= 0x10940 and <= 0x10959 ||
        rune.Value is >= 0x10EC5 and <= 0x10EC7 ||
        rune.Value is >= 0x11DB0 and <= 0x11DDB ||
        rune.Value is >= 0x16EA0 and <= 0x16EB8 ||
        rune.Value is >= 0x16EBB and <= 0x16ED3 ||
        rune.Value is >= 0x16FF2 and <= 0x16FF6 ||
        rune.Value is >= 0x187F8 and <= 0x187FF ||
        rune.Value is >= 0x18D09 and <= 0x18D1E ||
        rune.Value is >= 0x18D80 and <= 0x18DF2 ||
        rune.Value is >= 0x1E6C0 and <= 0x1E6DE ||
        rune.Value is >= 0x1E6E0 and <= 0x1E6E2 ||
        rune.Value is >= 0x1E6E4 and <= 0x1E6E5 ||
        rune.Value is >= 0x1E6E7 and <= 0x1E6ED ||
        rune.Value is >= 0x1E6F0 and <= 0x1E6F4 ||
        rune.Value is >= 0x1E6FE and <= 0x1E6FF ||
        rune.Value is >= 0x2B73A and <= 0x2B73F ||
        rune.Value is >= 0x2CEA2 and <= 0x2CEAD ||
        rune.Value is >= 0x323B0 and <= 0x33479;

    private static bool IsOtherIdentifierContinue(Rune rune) =>
        rune.Value is 0x00B7 or 0x0387 or 0x19DA or 0x30FB or 0xFF65 ||
        rune.Value is >= 0x1ACF and <= 0x1ADD ||
        rune.Value is >= 0x1AE0 and <= 0x1AEB ||
        rune.Value is >= 0x10EFA and <= 0x10EFB ||
        rune.Value is >= 0x11B60 and <= 0x11B67 ||
        rune.Value is >= 0x11DE0 and <= 0x11DE9 ||
        rune.Value is 0x1E6E3 or 0x1E6E6 or 0x1E6F5 ||
        rune.Value is >= 0x1E6EE and <= 0x1E6EF ||
        rune.Value is >= 0x1369 and <= 0x1371;

    private bool TryPeekRune(int index, out Rune rune, out int utf16Length)
    {
        rune = default;
        utf16Length = 0;
        if (index >= _source.Length)
        {
            return false;
        }

        var ch = _source[index];
        if (char.IsHighSurrogate(ch))
        {
            if (index + 1 >= _source.Length || !char.IsLowSurrogate(_source[index + 1]))
            {
                return false;
            }

            var codePoint = char.ConvertToUtf32(ch, _source[index + 1]);
            if (!Rune.TryCreate(codePoint, out rune))
            {
                return false;
            }

            utf16Length = 2;
            return true;
        }

        if (char.IsLowSurrogate(ch) || !Rune.TryCreate((int)ch, out rune))
        {
            return false;
        }

        utf16Length = 1;
        return true;
    }

    private static bool HasValidNumericLiteralSeparators(string text)
    {
        if (!text.Contains('_'))
        {
            return true;
        }

        var literal = text.EndsWith('n') ? text[..^1] : text;
        if (literal.Length == 0)
        {
            return false;
        }

        if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return HasValidSeparatedDigits(literal.AsSpan(2), NumericDigitKind.Hex);
        }

        if (literal.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
        {
            return HasValidSeparatedDigits(literal.AsSpan(2), NumericDigitKind.Octal);
        }

        if (literal.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            return HasValidSeparatedDigits(literal.AsSpan(2), NumericDigitKind.Binary);
        }

        return HasValidDecimalLiteralSeparators(literal);
    }

    private static bool HasValidDecimalLiteralSeparators(string literal)
    {
        var lowerExponentIndex = literal.IndexOf('e');
        var upperExponentIndex = literal.IndexOf('E');
        var exponentIndex = lowerExponentIndex >= 0 && upperExponentIndex >= 0
            ? Math.Min(lowerExponentIndex, upperExponentIndex)
            : Math.Max(lowerExponentIndex, upperExponentIndex);
        var significand = exponentIndex >= 0 ? literal.AsSpan(0, exponentIndex) : literal.AsSpan();
        if (exponentIndex >= 0)
        {
            var exponent = literal.AsSpan(exponentIndex + 1);
            if (exponent.Length > 0 && (exponent[0] == '+' || exponent[0] == '-'))
            {
                exponent = exponent[1..];
            }

            if (!HasValidSeparatedDigits(exponent, NumericDigitKind.Decimal))
            {
                return false;
            }
        }

        var dotIndex = significand.IndexOf('.');
        if (dotIndex >= 0)
        {
            var integerPart = significand[..dotIndex];
            var fractionPart = significand[(dotIndex + 1)..];
            if (integerPart.Length == 0)
            {
                return HasValidSeparatedDigits(fractionPart, NumericDigitKind.Decimal);
            }

            if (!HasValidDecimalIntegerPart(integerPart))
            {
                return false;
            }

            return fractionPart.Length == 0 || HasValidSeparatedDigits(fractionPart, NumericDigitKind.Decimal);
        }

        return HasValidDecimalIntegerPart(significand);
    }

    private static bool HasValidDecimalIntegerPart(ReadOnlySpan<char> digits)
    {
        if (!HasValidSeparatedDigits(digits, NumericDigitKind.Decimal))
        {
            return false;
        }

        return !digits.Contains('_') || digits[0] != '0';
    }

    private static bool HasValidSeparatedDigits(ReadOnlySpan<char> digits, NumericDigitKind kind)
    {
        if (digits.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < digits.Length; i++)
        {
            var ch = digits[i];
            if (ch == '_')
            {
                if (i == 0 ||
                    i == digits.Length - 1 ||
                    !IsDigitForNumericKind(digits[i - 1], kind) ||
                    !IsDigitForNumericKind(digits[i + 1], kind))
                {
                    return false;
                }

                continue;
            }

            if (!IsDigitForNumericKind(ch, kind))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDigitForNumericKind(char ch, NumericDigitKind kind) =>
        kind switch
        {
            NumericDigitKind.Decimal => ch >= '0' && ch <= '9',
            NumericDigitKind.Binary => IsBinDigit(ch),
            NumericDigitKind.Octal => IsOctDigit(ch),
            NumericDigitKind.Hex => IsHexDigit(ch),
            _ => false
        };

    private bool ConsumeStringEscapeSequence()
    {
        var escape = _source[_index];
        if (escape == 'u')
        {
            return ConsumeStringUnicodeEscape();
        }

        if (escape == 'x')
        {
            return ConsumeFixedHexEscape(escapeDigitCount: 2);
        }

        _index++;
        _column++;
        return true;
    }

    private bool ConsumeStringUnicodeEscape()
    {
        _index++;
        _column++;
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

            if (_index >= _source.Length || _source[_index] != '}' || _index == hexStart)
            {
                return false;
            }

            var hex = _source[hexStart.._index];
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint) ||
                codePoint > 0x10FFFF)
            {
                return false;
            }

            _index++;
            _column++;
            return true;
        }

        return ConsumeFixedHexDigits(escapeDigitCount: 4);
    }

    private bool ConsumeFixedHexEscape(int escapeDigitCount)
    {
        _index++;
        _column++;
        return ConsumeFixedHexDigits(escapeDigitCount);
    }

    private bool ConsumeFixedHexDigits(int escapeDigitCount)
    {
        if (_index + escapeDigitCount > _source.Length)
        {
            return false;
        }

        for (var i = 0; i < escapeDigitCount; i++)
        {
            if (!IsHexDigit(_source[_index + i]))
            {
                return false;
            }
        }

        _index += escapeDigitCount;
        _column += escapeDigitCount;
        return true;
    }

    private bool HasInvalidNumericLiteralBoundary()
    {
        if (_index >= _source.Length)
        {
            return false;
        }

        var ch = _source[_index];
        return char.IsDigit(ch) ||
               (TryPeekRune(_index, out var rune, out _) && IsIdentifierStart(rune)) ||
               (ch == '\\' && IsUnicodeEscapeStart(_index));
    }

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

    private string ReadTemplateLiteralToken(out bool containsEscape, out bool containsInvalidEscape, out bool terminated)
    {
        var start = _index;
        _index++;
        _column++;

        containsEscape = false;
        containsInvalidEscape = false;
        terminated = ScanTemplateLiteralBody(ref containsEscape, ref containsInvalidEscape);
        return _source[start.._index];
    }

    private bool ScanTemplateLiteralBody(ref bool containsEscape, ref bool containsInvalidEscape)
    {
        while (_index < _source.Length)
        {
            var c = _source[_index];
            if (c == '\\')
            {
                containsEscape = true;
                _index++;
                _column++;

                if (_index >= _source.Length)
                {
                    containsInvalidEscape = true;
                    break;
                }

                if (IsLineTerminator(_source[_index]))
                {
                    AdvanceLineTerminator();
                    continue;
                }

                if (!ConsumeTemplateEscapeSequence())
                {
                    containsInvalidEscape = true;
                }

                continue;
            }

            if (c == '`')
            {
                _index++;
                _column++;
                return true;
            }

            if (c == '$' && _index + 1 < _source.Length && _source[_index + 1] == '{')
            {
                _index += 2;
                _column += 2;
                if (!ScanTemplateSubstitution(ref containsEscape, ref containsInvalidEscape))
                {
                    return false;
                }

                continue;
            }

            if (IsLineTerminator(c))
            {
                AdvanceLineTerminator();
            }
            else
            {
                _index++;
                _column++;
            }
        }

        return false;
    }

    private bool ScanTemplateSubstitution(ref bool containsEscape, ref bool containsInvalidEscape)
    {
        var braceDepth = 1;
        while (_index < _source.Length)
        {
            var c = _source[_index];
            if (c == '\'' || c == '"')
            {
                if (!SkipStringLiteralInTemplateSubstitution(c))
                {
                    return false;
                }

                continue;
            }

            if (c == '`')
            {
                _index++;
                _column++;
                if (!ScanTemplateLiteralBody(ref containsEscape, ref containsInvalidEscape))
                {
                    return false;
                }

                continue;
            }

            if (c == '\\')
            {
                _index++;
                _column++;
                if (_index < _source.Length && IsLineTerminator(_source[_index]))
                {
                    AdvanceLineTerminator();
                    continue;
                }

                if (_index < _source.Length)
                {
                    _index++;
                    _column++;
                }

                continue;
            }

            if (c == '{')
            {
                _index++;
                _column++;
                braceDepth++;
                continue;
            }

            if (c == '}')
            {
                _index++;
                _column++;
                braceDepth--;
                if (braceDepth == 0)
                {
                    return true;
                }

                continue;
            }

            if (IsLineTerminator(c))
            {
                AdvanceLineTerminator();
            }
            else
            {
                _index++;
                _column++;
            }
        }

        return false;
    }

    private bool SkipStringLiteralInTemplateSubstitution(char quote)
    {
        _index++;
        _column++;
        while (_index < _source.Length)
        {
            var c = _source[_index];
            if (IsLineTerminator(c))
            {
                return false;
            }

            _index++;
            _column++;
            if (c == '\\')
            {
                if (_index < _source.Length && IsLineTerminator(_source[_index]))
                {
                    AdvanceLineTerminator();
                }
                else if (_index < _source.Length)
                {
                    _index++;
                    _column++;
                }

                continue;
            }

            if (c == quote)
            {
                return true;
            }
        }

        return false;
    }

    private bool ConsumeTemplateEscapeSequence()
    {
        var escape = _source[_index];
        if (escape == 'u')
        {
            return ConsumeStringUnicodeEscape();
        }

        if (escape == 'x')
        {
            return ConsumeFixedHexEscape(escapeDigitCount: 2);
        }

        if (escape is >= '1' and <= '9')
        {
            _index++;
            _column++;
            return false;
        }

        if (escape == '0')
        {
            _index++;
            _column++;
            return _index >= _source.Length || !char.IsDigit(_source[_index]);
        }

        _index++;
        _column++;
        return true;
    }
}
