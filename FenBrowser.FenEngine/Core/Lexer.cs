using System;
using System.Collections.Generic;
using System.Text;

namespace FenBrowser.FenEngine.Core
{
    public enum TokenType
    {
        // End of file
        Eof,
        
        // Error
        Illegal,

        // Identifiers + Literals
        Identifier,
        Number,
        String,

        // Operators
        Assign,
        Plus,
        Minus,
        Bang,
        Asterisk,
        Slash,
        Percent,      // %
        
        // Increment/Decrement
        Increment,    // ++
        Decrement,    // --
        
        // Compound assignments
        PlusAssign,   // +=
        MinusAssign,  // -=
        MulAssign,    // *=
        DivAssign,    // /=
        ModuloAssign, // %= (ES2015)
        BitwiseAndAssign,  // &= (ES2015)
        BitwiseOrAssign,   // |= (ES2015)
        BitwiseXorAssign,  // ^= (ES2015)
        LeftShiftAssign,   // <<= (ES2015)
        RightShiftAssign,  // >>= (ES2015)
        UnsignedRightShiftAssign, // >>>= (ES2015)
        
        // ES2021 Logical Assignment
        OrAssign,      // ||=
        AndAssign,     // &&=
        // NullCoalescingAssign, // ??= (Redundant)
        // NullCoalescing,       // ?? (Redundant)
        Question,             // ?
        
        // Comparison
        Lt,
        Gt,
        Eq,
        NotEq,
        LtEq,
        GtEq,
        StrictEq,     // ===
        StrictNotEq,  // !==
        And,
        Or,

        // Delimiters
        Comma,
        Semicolon,
        Colon,
        Dot,

        // Brackets
        LParen,
        RParen,
        LBrace,
        RBrace,
        LBracket,
        RBracket,

        // Keywords
        Function,
        Var,
        Let,
        Const,
        True,
        False,
        If,
        Else,
        Return,
        Try,
        Catch,
        Finally,
        While,
        For,
        Null,
        Undefined,
        Class,
        New,
        This,

        Throw,
        Async,
        Await,
        Typeof,       // typeof
        Instanceof,   // instanceof
        In,           // in
        Of,           // of
        Delete,       // delete
        Void,         // void
        Break,        // break
        Continue,     // continue
        Switch,       // switch
        Case,         // case
        Default,      // default
        Do,           // do
        With,         // with (ES5.1)

        Extends,      // extends
        Super,        // super
        Static,       // static
        Import,       // import
        Export,       // export
        From,         // from
        As,           // as
        Yield,        // yield (for generators)

        // JavaScript special operators
        // Question,   // ? (Duplicate)
        Arrow,      // =>
        Regex,      // /pattern/flags
        Backtick,   // ` (template literal start)
        TemplateString, // Template literal content
        TemplateExprStart, // ${
        TemplateExprEnd,   // }
        Ellipsis,          // ...
        PrivateIdentifier, // #field (for private class fields)
        At,                // @ (for decorators)

        // Template Literal Tokens
        TemplateHead,      // `head${
        TemplateMiddle,    // }middle${
        TemplateTail,      // }tail`
        TemplateNoSubst,   // `nosubst`
        
        // ES6+ operators
        OptionalChain,     // ?.
        NullishCoalescing, // ??
        NullishAssign,     // ??=
        // OrAssign,          // ||= (Duplicate)
        // AndAssign,         // &&= (Duplicate)
        Exponent,          // **
        ExponentAssign,    // **=
        BigInt,            // 123n
        
        // Bitwise operators
        BitwiseAnd,        // &
        BitwiseOr,         // |
        BitwiseXor,        // ^
        BitwiseNot,        // ~
        LeftShift,         // <<
        RightShift,        // >>
        UnsignedRightShift // >>>
    }

    public class Token
    {
        public TokenType Type { get; }
        public string Literal { get; }
        public int Line { get; }
        public int Column { get; }
        public int Position { get; set; } // Added for source slicing
        public bool HadLineTerminatorBefore { get; set; }

        public Token(TokenType type, string literal, int line, int column, bool hadLineTerminatorBefore = false)
        {
            Type = type;
            Literal = literal;
            Line = line;
            Column = column;
            HadLineTerminatorBefore = hadLineTerminatorBefore;
        }

        public override string ToString()
        {
            return $"Token({Type}, \"{Literal}\")";
        }
    }

    public class Lexer
    {
        private readonly string _input;
        public string Source => _input; // Expose source for Parser
        private int _position;      // current position in input (points to current char)
        private int _readPosition;  // current reading position in input (after current char)
        private char _ch;           // current char under examination
        private int _line = 1;
        private int _column = 0;
        private bool _hasEscapeInLastIdent = false;
        private bool _hasInvalidEscapeInLastIdent = false;
        private Token _prevToken; // Track previous token for regex vs division context
        // Set to true when preceding whitespace/comments contained a line terminator.
        // Used by --> HTML close comment detection.
        private bool _precedingLineTerminator = false;
        private static readonly Dictionary<string, TokenType> Keywords;

        public static bool DebugMode = false;

        static Lexer()
        {
            try
            {
                Keywords = new Dictionary<string, TokenType>
                {
                    { "function", TokenType.Function },
                    { "var", TokenType.Var },
                    { "let", TokenType.Let },
                    { "const", TokenType.Const },
                    { "true", TokenType.True },
                    { "false", TokenType.False },
                    { "if", TokenType.If },
                    { "else", TokenType.Else },
                    { "return", TokenType.Return },
                    { "while", TokenType.While },
                    { "for", TokenType.For },
                    { "null", TokenType.Null },
                    { "undefined", TokenType.Undefined },
                    { "class", TokenType.Class },
                    { "new", TokenType.New },
                    { "this", TokenType.This },
                    { "try", TokenType.Try },
                    { "catch", TokenType.Catch },
                    { "finally", TokenType.Finally },
                    { "throw", TokenType.Throw },
                    { "async", TokenType.Async },
                    { "await", TokenType.Await },
                    { "typeof", TokenType.Typeof },
                    { "instanceof", TokenType.Instanceof },
                    { "in", TokenType.In },
                    { "of", TokenType.Of },
                    { "delete", TokenType.Delete },
                    { "void", TokenType.Void },
                    { "break", TokenType.Break },
                    { "continue", TokenType.Continue },
                    { "switch", TokenType.Switch },
                    { "extends", TokenType.Extends },
                    { "super", TokenType.Super },
                    { "static", TokenType.Static },
                    { "import", TokenType.Import },
                    { "export", TokenType.Export },
                    { "from", TokenType.From },
                    { "as", TokenType.As },
                    { "case", TokenType.Case },
                    { "default", TokenType.Default },
                    { "do", TokenType.Do },
                    { "with", TokenType.With },
                    { "yield", TokenType.Yield },
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing Lexer keywords: {ex}");
                Keywords = new Dictionary<string, TokenType>();
            }
        }

        public Lexer(string input)
        {
            // ES2023: Hashbang grammar — strip leading #! line before tokenizing
            if (input.Length >= 2 && input[0] == '#' && input[1] == '!')
            {
                int eol = input.IndexOf('\n');
                _input = eol >= 0 ? input.Substring(eol + 1) : "";
            }
            else
            {
                _input = input;
            }
            ReadChar();
        }

        private void ReadChar()
        {
            if (_readPosition >= _input.Length)
            {
                _ch = '\0';
            }
            else
            {
                _ch = _input[_readPosition];
            }
            _position = _readPosition;
            _readPosition++;
            _column++;
        }

        private char PeekChar()
        {
            if (_readPosition >= _input.Length)
            {
                return '\0';
            }
            return _input[_readPosition];
        }

        public Token NextToken()
        {
            var token = NextTokenInternal();
            if (DebugMode && token.Type != TokenType.Eof)
            {
                 Console.WriteLine($"[LEXER-DEBUG] Token: {token.Type} '{token.Literal}' Line: {token.Line} Col: {token.Column}");
            }
            return token;
        }

        private Token NextTokenInternal(bool resetLineTerminator = true)
        {
            if (DebugMode) Console.WriteLine($"[LEXER-TRACE] Start NextTokenInternal. _ch: '{_ch}' ({(int)_ch})");
            // Only reset at the top-level call; recursive calls from comment handling preserve the flag.
            if (resetLineTerminator) _precedingLineTerminator = false;
            bool hadLineTerminator = SkipWhitespace();
            if (hadLineTerminator) _precedingLineTerminator = true;
            // Also pick up any line terminators tracked by preceding block comments.
            hadLineTerminator |= _precedingLineTerminator;

            int startPos = _position; // Capture start position
            Token token;
            int startColumn = _column;

            switch (_ch)
            {
                // ... (rest of switch is unchanged)

                case '=':
                    if (PeekChar() == '=')
                    {
                        ReadChar();
                        if (PeekChar() == '=')
                        {
                            ReadChar();
                            token = new Token(TokenType.StrictEq, "===", _line, startColumn, hadLineTerminator);
                        }
                        else
                        {
                            token = new Token(TokenType.Eq, "==", _line, startColumn, hadLineTerminator);
                        }
                    }
                    else if (PeekChar() == '>')
                    {
                        ReadChar();
                        token = new Token(TokenType.Arrow, "=>", _line, startColumn, hadLineTerminator);
                    }
                    else
                    {
                        token = new Token(TokenType.Assign, "=", _line, startColumn, hadLineTerminator);
                    }
                    break;
                case '?':
                    if (PeekChar() == '.')
                    {
                        // Check if next char after . is a digit (ternary + decimal, not optional chain)
                        if (_readPosition + 1 < _input.Length && IsDigit(_input[_readPosition + 1]))
                        {
                            token = new Token(TokenType.Question, "?", _line, startColumn, hadLineTerminator);
                        }
                        else
                        {
                            ReadChar();
                            token = new Token(TokenType.OptionalChain, "?.", _line, startColumn, hadLineTerminator);
                        }
                    }
                    else if (PeekChar() == '?')
                    {
                        ReadChar();
                        if (PeekChar() == '=')
                        {
                            ReadChar();
                            token = new Token(TokenType.NullishAssign, "??=", _line, startColumn, hadLineTerminator);
                        }
                        else
                        {
                            token = new Token(TokenType.NullishCoalescing, "??", _line, startColumn, hadLineTerminator);
                        }
                    }
                    else
                    {
                        token = new Token(TokenType.Question, "?", _line, startColumn, hadLineTerminator);
                    }
                    break;
                case '+':
                    if (PeekChar() == '+')
                    {
                        ReadChar();
                        token = new Token(TokenType.Increment, "++", _line, startColumn);
                    }
                    else if (PeekChar() == '=')
                    {
                        ReadChar();
                        token = new Token(TokenType.PlusAssign, "+=", _line, startColumn);
                    }
                    else
                    {
                        token = new Token(TokenType.Plus, "+", _line, startColumn);
                    }
                    break;
                case '-':
                    if (PeekChar() == '-')
                    {
                        ReadChar();
                        // HTML close comment: --> at start of a line (after a line terminator or
                        // a multi-line block comment) is a line comment per Annex B.
                        if (hadLineTerminator && PeekChar() == '>')
                        {
                            ReadChar(); // consume >
                            // skip to end of line
                            while (_ch != '\0' && !IsLineTerminator(_ch))
                                ReadChar();
                            return NextTokenInternal(resetLineTerminator: false);
                        }
                        token = new Token(TokenType.Decrement, "--", _line, startColumn);
                    }
                    else if (PeekChar() == '=')
                    {
                        ReadChar();
                        token = new Token(TokenType.MinusAssign, "-=", _line, startColumn);
                    }
                    else
                    {
                        token = new Token(TokenType.Minus, "-", _line, startColumn);
                    }
                    break;
                case '!':
                    if (PeekChar() == '=')
                    {
                        ReadChar();
                        if (PeekChar() == '=')
                        {
                            ReadChar();
                            token = new Token(TokenType.StrictNotEq, "!==", _line, startColumn);
                        }
                        else
                        {
                            token = new Token(TokenType.NotEq, "!=", _line, startColumn);
                        }
                    }
                    else
                    {
                        token = new Token(TokenType.Bang, "!", _line, startColumn);
                    }
                    break;
                case '/':
                    // Handle comments
                    if (PeekChar() == '/')
                    {
                        SkipLineComment();
                        return NextTokenInternal(resetLineTerminator: false);
                    }
                    else if (PeekChar() == '*')
                    {
                        bool terminated = SkipBlockComment();
                        if (!terminated)
                        {
                            token = new Token(TokenType.Illegal, "/*", _line, startColumn, hadLineTerminator);
                            break;
                        }
                        return NextTokenInternal(resetLineTerminator: false);
                    }
                    else if (PeekChar() == '=')
                    {
                        ReadChar();
                        token = new Token(TokenType.DivAssign, "/=", _line, startColumn);
                    }
                    else
                    {
                        // Check for regex literal
                        if (IsRegexStart(_prevToken))
                        {
                            bool regexValid;
                            string regex = ReadRegexLiteral(out regexValid);
                            token = new Token(regexValid ? TokenType.Regex : TokenType.Illegal, regex, _line, startColumn);
                            token.HadLineTerminatorBefore = hadLineTerminator;
                            _prevToken = token;
                            return token;
                        }
                        else
                        {
                            token = new Token(TokenType.Slash, "/", _line, startColumn);
                        }
                    }
                    break;
                case '*':
                    if (PeekChar() == '*')
                    {
                        ReadChar();
                        if (PeekChar() == '=')
                        {
                            ReadChar();
                            token = new Token(TokenType.ExponentAssign, "**=", _line, startColumn);
                        }
                        else
                        {
                            token = new Token(TokenType.Exponent, "**", _line, startColumn);
                        }
                    }
                    else if (PeekChar() == '=')
                    {
                        ReadChar();
                        token = new Token(TokenType.MulAssign, "*=", _line, startColumn);
                    }
                    else
                    {
                        token = new Token(TokenType.Asterisk, "*", _line, startColumn);
                    }
                    break;
                case '%':
                    if (PeekChar() == '=')
                    {
                        ReadChar();
                        token = new Token(TokenType.ModuloAssign, "%=", _line, startColumn);
                    }
                    else
                    {
                        token = new Token(TokenType.Percent, "%", _line, startColumn);
                    }
                    break;
                case '<':
                    // HTML open comment: <!-- treated as a single-line comment (Annex B)
                    // Safe multi-char peek using _input/_readPosition without consuming.
                    if (_readPosition < _input.Length && _input[_readPosition] == '!' &&
                        _readPosition + 1 < _input.Length && _input[_readPosition + 1] == '-' &&
                        _readPosition + 2 < _input.Length && _input[_readPosition + 2] == '-')
                    {
                        // Consume !, -, -
                        ReadChar(); ReadChar(); ReadChar();
                        // skip to end of line
                        while (_ch != '\0' && !IsLineTerminator(_ch))
                            ReadChar();
                        return NextTokenInternal(resetLineTerminator: false);
                    }
                    else if (PeekChar() == '=')
                    {
                        ReadChar();
                        token = new Token(TokenType.LtEq, "<=", _line, startColumn);
                    }
                    else if (PeekChar() == '<')
                    {
                        ReadChar();
                        if (PeekChar() == '=')
                        {
                            ReadChar();
                            token = new Token(TokenType.LeftShiftAssign, "<<=", _line, startColumn);
                        }
                        else
                        {
                            token = new Token(TokenType.LeftShift, "<<", _line, startColumn);
                        }
                    }
                    else
                    {
                        token = new Token(TokenType.Lt, "<", _line, startColumn);
                    }
                    break;
                case '>':
                    if (PeekChar() == '=')
                    {
                        ReadChar();
                        token = new Token(TokenType.GtEq, ">=", _line, startColumn);
                    }
                    else if (PeekChar() == '>')
                    {
                        ReadChar();
                        if (PeekChar() == '>')
                        {
                            ReadChar();
                            if (PeekChar() == '=')
                            {
                                ReadChar();
                                token = new Token(TokenType.UnsignedRightShiftAssign, ">>>=", _line, startColumn);
                            }
                            else
                            {
                                token = new Token(TokenType.UnsignedRightShift, ">>>", _line, startColumn);
                            }
                        }
                        else if (PeekChar() == '=')
                        {
                            ReadChar();
                            token = new Token(TokenType.RightShiftAssign, ">>=", _line, startColumn);
                        }
                        else
                        {
                            token = new Token(TokenType.RightShift, ">>", _line, startColumn);
                        }
                    }
                    else
                    {
                        token = new Token(TokenType.Gt, ">", _line, startColumn);
                    }
                    break;
                case '&':
                    if (PeekChar() == '&')
                    {
                        ReadChar();
                        if (PeekChar() == '=')
                        {
                            ReadChar();
                            token = new Token(TokenType.AndAssign, "&&=", _line, startColumn);
                        }
                        else
                        {
                            token = new Token(TokenType.And, "&&", _line, startColumn);
                        }
                    }
                    else if (PeekChar() == '=')
                    {
                        ReadChar();
                        token = new Token(TokenType.BitwiseAndAssign, "&=", _line, startColumn);
                    }
                    else
                    {
                        token = new Token(TokenType.BitwiseAnd, "&", _line, startColumn);
                    }
                    break;
                case '|':
                    if (PeekChar() == '|')
                    {
                        ReadChar();
                        if (PeekChar() == '=')
                        {
                            ReadChar();
                            token = new Token(TokenType.OrAssign, "||=", _line, startColumn);
                        }
                        else
                        {
                            token = new Token(TokenType.Or, "||", _line, startColumn);
                        }
                    }
                    else if (PeekChar() == '=')
                    {
                        ReadChar();
                        token = new Token(TokenType.BitwiseOrAssign, "|=", _line, startColumn);
                    }
                    else
                    {
                        token = new Token(TokenType.BitwiseOr, "|", _line, startColumn);
                    }
                    break;
                case '^':
                    if (PeekChar() == '=')
                    {
                        ReadChar();
                        token = new Token(TokenType.BitwiseXorAssign, "^=", _line, startColumn);
                    }
                    else
                    {
                        token = new Token(TokenType.BitwiseXor, "^", _line, startColumn);
                    }
                    break;
                case '~':
                    token = new Token(TokenType.BitwiseNot, "~", _line, startColumn);
                    break;
                case ';':
                    token = new Token(TokenType.Semicolon, ";", _line, startColumn);
                    break;
                case ',':
                    token = new Token(TokenType.Comma, ",", _line, startColumn);
                    break;
                case ':':
                    token = new Token(TokenType.Colon, ":", _line, startColumn);
                    break;
                case '.':
                    if (IsDigit(PeekChar()))
                    {
                        bool isBigInt;
                        bool isValid;
                        string numStr = ReadNumber(startsWithDot: true, out isBigInt, out isValid);
                        var numberToken = new Token(isValid ? TokenType.Number : TokenType.Illegal, numStr, _line, startColumn, hadLineTerminator);
                        _prevToken = numberToken;
                        return numberToken;
                    }
                    if (PeekChar() == '.' && _readPosition + 1 < _input.Length && _input[_readPosition + 1] == '.')
                    {
                        ReadChar();
                        ReadChar();
                        token = new Token(TokenType.Ellipsis, "...", _line, startColumn);
                    }
                    else
                    {
                        token = new Token(TokenType.Dot, ".", _line, startColumn);
                    }
                    break;
                case '(':
                    token = new Token(TokenType.LParen, "(", _line, startColumn);
                    break;
                case ')':
                    token = new Token(TokenType.RParen, ")", _line, startColumn);
                    break;
                case '{':
                    token = new Token(TokenType.LBrace, "{", _line, startColumn);
                    break;
                case '}':
                    token = new Token(TokenType.RBrace, "}", _line, startColumn);
                    break;
                case '[':
                    token = new Token(TokenType.LBracket, "[", _line, startColumn);
                    break;
                case ']':
                    token = new Token(TokenType.RBracket, "]", _line, startColumn);
                    break;
                case '"':
                case '\'':
                    bool stringValid;
                    string str = ReadString(_ch, out stringValid);
                    token = new Token(stringValid ? TokenType.String : TokenType.Illegal, str, _line, startColumn);
                    token.HadLineTerminatorBefore = hadLineTerminator;
                    token.Position = startPos;
                    _prevToken = token;
                    return token;
                case '`':
                    token = ReadTemplateStart();
                    token.HadLineTerminatorBefore = hadLineTerminator;
                    token.Position = startPos;
                    _prevToken = token;
                    return token;
                case '#':
                    // Private identifier (e.g., #field for private class fields)
                    ReadChar(); // consume #
                    if (IsIdentifierStart(_ch) || _ch == '\\')
                    {
                        string privateName = ReadIdentifier();
                        var t = new Token(TokenType.PrivateIdentifier, "#" + privateName, _line, startColumn);
                        _prevToken = t;
                        return t;
                    }
                    token = new Token(TokenType.Illegal, "#", _line, startColumn);
                    break;
                case '@':
                    token = new Token(TokenType.At, "@", _line, startColumn);
                    ReadChar();
                    break;
                    break;
                case '\0':
                    token = new Token(TokenType.Eof, "", _line, startColumn);
                    break;
                case '\\':
                    if (PeekChar() == 'u')
                    {
                        // Start of unicode escape in identifier
                        string literal = ReadIdentifier();
                        if (_hasInvalidEscapeInLastIdent)
                        {
                            token = new Token(TokenType.Illegal, literal, _line, startColumn, hadLineTerminator);
                            break;
                        }
                        if (string.IsNullOrEmpty(literal) || !IsIdentifierStart(literal[0]))
                        {
                            token = new Token(TokenType.Illegal, literal, _line, startColumn, hadLineTerminator);
                            break;
                        }
                        // If identifier contains escapes, it cannot be a keyword
                        TokenType type = _hasEscapeInLastIdent ? TokenType.Identifier : LookupIdent(literal);
                        var t = new Token(type, literal, _line, startColumn, hadLineTerminator);
                        _prevToken = t;
                        if (DebugMode) Console.WriteLine($"[LEXER-INTERNAL-UNICODE] Identifier/Keyword: {t.Type} '{t.Literal}'");
                        return t;
                    }
                    else
                    {
                         token = new Token(TokenType.Illegal, "\\", _line, startColumn, hadLineTerminator);
                    }
                    break;
                default:
                    if (IsIdentifierStart(_ch))
                    {
                        string literal = ReadIdentifier();
                        if (_hasInvalidEscapeInLastIdent)
                        {
                            token = new Token(TokenType.Illegal, literal, _line, startColumn, hadLineTerminator);
                            break;
                        }
                        // If identifier contains escapes, it cannot be a keyword
                        TokenType type = _hasEscapeInLastIdent ? TokenType.Identifier : LookupIdent(literal);
                        var t = new Token(type, literal, _line, startColumn, hadLineTerminator);
                        _prevToken = t;
                        if (DebugMode) Console.WriteLine($"[LEXER-INTERNAL-DEFAULT] Identifier/Keyword: {t.Type} '{t.Literal}'");
                        return t;
                    }
                    else if (IsDigit(_ch))
                    {
                        bool isBigInt;
                        bool isValid;
                        string numStr = ReadNumber(startsWithDot: false, out isBigInt, out isValid);
                        var tokenType = isValid ? (isBigInt ? TokenType.BigInt : TokenType.Number) : TokenType.Illegal;
                        var t = new Token(tokenType, numStr, _line, startColumn, hadLineTerminator);
                        _prevToken = t;
                        return t;
                    }
                    else
                    {
                        token = new Token(TokenType.Illegal, _ch.ToString(), _line, startColumn, hadLineTerminator);
                    }
                    break;
            }

            ReadChar();
            
            token.Position = startPos;
            
            
            token.HadLineTerminatorBefore = hadLineTerminator;
            _prevToken = token;
            // Console.WriteLine($"[DEBUG] Lexer.NextToken returning: {token.Type} ({token.Literal})");
            return token;
        }

        private bool SkipWhitespace()
        {
            bool hadLineTerminator = false;
            while (true)
            {
                if (IsJsWhiteSpace(_ch))
                {
                    ReadChar();
                    continue;
                }

                if (_ch == '\r')
                {
                    hadLineTerminator = true;
                    _line++;
                    _column = 0;
                    ReadChar();
                    if (_ch == '\n')
                    {
                        ReadChar();
                    }
                    continue;
                }

                if (_ch == '\n' || _ch == '\u2028' || _ch == '\u2029')
                {
                    hadLineTerminator = true;
                    _line++;
                    _column = 0;
                    ReadChar();
                    continue;
                }

                break;
            }
            return hadLineTerminator;
        }

        private void SkipLineComment()
        {
            // Consume first slash
            ReadChar();
            // Consume second slash
            ReadChar();
            
            while (_ch != '\0' && !IsLineTerminator(_ch))
            {
                ReadChar();
            }
            // Whitespace skipping in NextToken will handle the newline
        }

        // Returns true if comment was properly terminated; sets _precedingLineTerminator if
        // the block comment spanned multiple lines (needed for --> HTML close comment detection).
        private bool SkipBlockComment()
        {
             // Consume /
            ReadChar();
            // Consume *
            ReadChar();

            while (true)
            {
                if (_ch == '\0') return false;
                if (_ch == '*' && PeekChar() == '/')
                {
                    ReadChar(); // *
                    ReadChar(); // /
                    return true;
                }
                if (_ch == '\r')
                {
                    _line++;
                    _column = 0;
                    _precedingLineTerminator = true;
                    ReadChar();
                    if (_ch == '\n')
                    {
                        ReadChar();
                    }
                    continue;
                }

                if (_ch == '\n' || _ch == '\u2028' || _ch == '\u2029')
                {
                    _line++;
                    _column = 0;
                    _precedingLineTerminator = true;
                }
                ReadChar();
            }
        }

        private string ReadIdentifier()
        {
            int startPos = _position;
            StringBuilder sb = null;
            _hasEscapeInLastIdent = false;
            _hasInvalidEscapeInLastIdent = false;

            while (IsIdentifierPart(_ch) || _ch == '\\')
            {
                if (_ch == '\\')
                {
                    // Check for unicode escape sequence \uXXXX
                    if (PeekChar() == 'u')
                    {
                        _hasEscapeInLastIdent = true;
                        if (sb == null)
                        {
                            sb = new StringBuilder();
                            if (_position > startPos)
                            {
                                sb.Append(_input.Substring(startPos, _position - startPos));
                            }
                        }
                        
                        ReadChar(); // consume \
                        ReadChar(); // consume u
                        
                        // Parse Unicode escape (similar to ReadString)
                        if (_ch == '{')
                        {
                            // \u{XXXX} - variable length code point
                            var hexBuf = new StringBuilder();
                            ReadChar();
                            while (_ch != '}' && _ch != '\0' && IsHexDigit(_ch))
                            {
                                hexBuf.Append(_ch);
                                ReadChar();
                            }
                            if (_ch == '}' && hexBuf.Length > 0)
                            {
                                try 
                                {
                                    int cp = Convert.ToInt32(hexBuf.ToString(), 16);
                                    string decoded = char.ConvertFromUtf32(cp);
                                    foreach (char c in decoded)
                                    {
                                        if (!IsIdentifierPart(c))
                                        {
                                            _hasInvalidEscapeInLastIdent = true;
                                            break;
                                        }
                                    }
                                    sb.Append(decoded);
                                }
                                catch 
                                {
                                    // Invalid code point
                                    sb.Append('\uFFFD'); // Replacement char
                                    _hasInvalidEscapeInLastIdent = true;
                                }
                            }
                            ReadChar(); // consume closing '}'
                        }
                        else if (IsHexDigit(_ch))
                        {
                            // \uHHHH — 4 fixed hex digits
                            char h1 = _ch;
                            ReadChar(); char h2 = _ch;
                            ReadChar(); char h3 = _ch;
                            ReadChar(); char h4 = _ch;
                           
                            if (IsHexDigit(h1) && IsHexDigit(h2) && IsHexDigit(h3) && IsHexDigit(h4))
                            {
                                int code = Convert.ToInt32(new string(new[] { h1, h2, h3, h4 }), 16);
                                char decoded = (char)code;
                                if (!IsIdentifierPart(decoded))
                                {
                                    _hasInvalidEscapeInLastIdent = true;
                                }
                                sb.Append(decoded);
                                ReadChar();
                            }
                            else
                            {
                                // Malformed escape
                                // We essentially consumed characters we shouldn't have if it's invalid.
                                // But JS syntax error usually.
                                // Recover by appending what we have?
                                sb.Append("\\u");
                                sb.Append(h1);
                                if (IsHexDigit(h2)) sb.Append(h2);
                                if (IsHexDigit(h3)) sb.Append(h3);
                                if (IsHexDigit(h4)) sb.Append(h4);
                                _hasInvalidEscapeInLastIdent = true;
                                continue; 
                            }
                        }
                        else
                        {
                            // \u followed by non-hex?
                            sb.Append("\\u");
                            // Backtrack or just append current? 
                            // Current is the char after u.
                            // We already consumed \ and u.
                            // Current _ch is NOT part of escape.
                            // So append \u and process _ch in next loop?
                            // But we are in while loop.
                            // Let's just append \u and let the loop continue with _ch
                            // But wait, IsLetter/Digit check at start of loop.
                            // If _ch is not letter/digit, we break.
                            // So we append \u and if _ch is invalid ident part, we break.
                            _hasInvalidEscapeInLastIdent = true;
                            continue;
                        }
                    }
                    else
                    {
                        // Backslash not followed by u is not allowed in identifier start/part
                        // It IS allowed in string, but here we are in identifier.
                        break; 
                    }
                }
                else
                {
                    if (sb != null) sb.Append(_ch);
                    ReadChar();
                }
            }
            
            if (sb != null) return sb.ToString();
            return _input.Substring(startPos, _position - startPos);
        }

        private string ReadNumber(bool startsWithDot, out bool isBigInt, out bool isValid)
        {
            int startPos = _position;
            isBigInt = false;
            isValid = true;
            var literal = new StringBuilder();
            bool hasDot = false;
            bool hasExponent = false;
            bool isNonDecimalPrefixed = false;

            if (startsWithDot)
            {
                hasDot = true;
                literal.Append('0');
                literal.Append('.');
                ReadChar(); // consume '.'

                bool fracSeparatorsValid;
                bool hasFractionDigits = ReadDigitsWithSeparators(IsDigit, literal, out fracSeparatorsValid);
                if (!hasFractionDigits || !fracSeparatorsValid)
                {
                    isValid = false;
                }

                if (_ch == 'e' || _ch == 'E')
                {
                    hasExponent = true;
                    literal.Append(_ch);
                    ReadChar(); // consume e/E

                    if (_ch == '+' || _ch == '-')
                    {
                        literal.Append(_ch);
                        ReadChar();
                    }

                    bool expSeparatorsValid;
                    bool hasExpDigits = ReadDigitsWithSeparators(IsDigit, literal, out expSeparatorsValid);
                    if (!hasExpDigits || !expSeparatorsValid)
                    {
                        isValid = false;
                    }
                }
            }
            else if (_ch == '0' && (PeekChar() == 'x' || PeekChar() == 'X' || PeekChar() == 'o' || PeekChar() == 'O' || PeekChar() == 'b' || PeekChar() == 'B'))
            {
                isNonDecimalPrefixed = true;
                char prefix = PeekChar();
                ReadChar(); // consume 0
                ReadChar(); // consume prefix

                literal.Append('0');
                literal.Append(prefix);

                Func<char, bool> isBaseDigit = prefix == 'x' || prefix == 'X'
                    ? new Func<char, bool>(IsHexDigit)
                    : (prefix == 'o' || prefix == 'O'
                        ? new Func<char, bool>(c => c >= '0' && c <= '7')
                        : new Func<char, bool>(c => c == '0' || c == '1'));

                bool separatorsValid;
                bool hasDigits = ReadDigitsWithSeparators(isBaseDigit, literal, out separatorsValid);
                if (!hasDigits || !separatorsValid)
                {
                    isValid = false;
                }

                if (_ch == 'n')
                {
                    isBigInt = true;
                    ReadChar();
                }
            }
            else
            {
                bool intSeparatorsValid;
                bool hasIntDigits = ReadDigitsWithSeparators(IsDigit, literal, out intSeparatorsValid);
                if (!hasIntDigits || !intSeparatorsValid)
                {
                    isValid = false;
                }

                if (_ch == '.')
                {
                    hasDot = true;
                    literal.Append('.');
                    ReadChar(); // consume '.'

                    bool fracSeparatorsValid;
                    ReadDigitsWithSeparators(IsDigit, literal, out fracSeparatorsValid);
                    if (!fracSeparatorsValid)
                    {
                        isValid = false;
                    }
                }

                if (_ch == 'e' || _ch == 'E')
                {
                    hasExponent = true;
                    literal.Append(_ch);
                    ReadChar(); // consume e/E

                    if (_ch == '+' || _ch == '-')
                    {
                        literal.Append(_ch);
                        ReadChar();
                    }

                    bool expSeparatorsValid;
                    bool hasExpDigits = ReadDigitsWithSeparators(IsDigit, literal, out expSeparatorsValid);
                    if (!hasExpDigits || !expSeparatorsValid)
                    {
                        isValid = false;
                    }
                }

                if (_ch == 'n')
                {
                    if (hasDot || hasExponent)
                    {
                        isValid = false;
                    }
                    isBigInt = true;
                    ReadChar();
                }
            }

            if (isBigInt && !isNonDecimalPrefixed && literal.Length > 1 && literal[0] == '0')
            {
                isValid = false;
            }

            string rawLiteral = _input.Substring(startPos, Math.Max(0, _position - startPos));
            if (!isBigInt &&
                !isNonDecimalPrefixed &&
                !hasDot &&
                !hasExponent &&
                rawLiteral.Contains('_') &&
                literal.Length > 1 &&
                literal[0] == '0')
            {
                isValid = false;
            }

            if (IsDigit(_ch) || IsIdentifierStart(_ch) || _ch == '\\')
            {
                isValid = false;
                ConsumeInvalidNumericTail();
            }

            return literal.ToString();
        }

        private bool ReadDigitsWithSeparators(Func<char, bool> isDigitFn, StringBuilder output, out bool separatorsValid)
        {
            separatorsValid = true;
            bool hadDigit = false;
            bool previousUnderscore = false;

            while (true)
            {
                if (isDigitFn(_ch))
                {
                    hadDigit = true;
                    previousUnderscore = false;
                    output.Append(_ch);
                    ReadChar();
                    continue;
                }

                if (_ch == '_')
                {
                    if (!hadDigit || previousUnderscore)
                    {
                        separatorsValid = false;
                    }
                    previousUnderscore = true;
                    ReadChar();
                    continue;
                }

                break;
            }

            if (previousUnderscore)
            {
                separatorsValid = false;
            }

            return hadDigit;
        }

        private void ConsumeInvalidNumericTail()
        {
            while (IsIdentifierPart(_ch) || _ch == '\\' || _ch == '_')
            {
                ReadChar();
            }
        }

        private bool IsHexDigit(char ch)
        {
            return (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
        }

        private string ReadString(char quote, out bool isValid)
        {
            isValid = true;
            var sb = new StringBuilder();
            ReadChar(); // Move past opening quote
            
            while (_ch != quote && _ch != '\0')
            {
                if (IsLineTerminator(_ch))
                {
                    // Unescaped line terminators are not allowed in string literals.
                    isValid = false;
                    break;
                }

                if (_ch == '\\')
                {
                    ReadChar(); // Consume backslash
                    switch (_ch)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'v': sb.Append('\v'); break;
                        case '0': sb.Append('\0'); break;
                        case '\\': sb.Append('\\'); break;
                        case '\'': sb.Append('\''); break;
                        case '"': sb.Append('"'); break;
                        case '/': sb.Append('/'); break;
                        case 'x': // \xHH - 2 hex digits
                            {
                                ReadChar();
                                if (IsHexDigit(_ch))
                                {
                                    char h1 = _ch;
                                    ReadChar();
                                    if (IsHexDigit(_ch))
                                    {
                                        char h2 = _ch;
                                        int code = Convert.ToInt32(new string(new[] { h1, h2 }), 16);
                                        sb.Append((char)code);
                                    }
                                    else
                                    {
                                        sb.Append('x');
                                        sb.Append(h1);
                                        continue; // Don't ReadChar at end
                                    }
                                }
                                else
                                {
                                    sb.Append('x');
                                    continue; // Don't ReadChar at end
                                }
                            }
                            break;
                        case 'u': // \uHHHH or \u{HHHH} - ES2015 Unicode code point escape
                            {
                                ReadChar();
                                if (_ch == '{')
                                {
                                    // \u{XXXX} - variable length code point
                                    var hexBuf = new StringBuilder();
                                    ReadChar();
                                    while (_ch != '}' && _ch != '\0' && IsHexDigit(_ch))
                                    {
                                        hexBuf.Append(_ch);
                                        ReadChar();
                                    }
                                    if (_ch == '}' && hexBuf.Length > 0)
                                    {
                                        int cp = Convert.ToInt32(hexBuf.ToString(), 16);
                                        sb.Append(char.ConvertFromUtf32(cp));
                                    }
                                }
                                else if (IsHexDigit(_ch))
                                {
                                    // \uHHHH — 4 fixed hex digits, we already consumed the first
                                    char h1 = _ch;
                                    ReadChar(); char h2 = _ch;
                                    ReadChar(); char h3 = _ch;
                                    ReadChar(); char h4 = _ch;
                                    if (IsHexDigit(h1) && IsHexDigit(h2) && IsHexDigit(h3) && IsHexDigit(h4))
                                    {
                                        int code = Convert.ToInt32(new string(new[] { h1, h2, h3, h4 }), 16);
                                        sb.Append((char)code);
                                    }
                                    else
                                    {
                                        sb.Append('u');
                                        sb.Append(h1);
                                        continue;
                                    }
                                }
                                else
                                {
                                    sb.Append('u');
                                    continue;
                                }
                            }
                            break;
                        case '\r': // Line continuation
                            if (PeekChar() == '\n') ReadChar();
                            break;
                        case '\n': // Line continuation
                        case '\u2028':
                        case '\u2029':
                            break;
                        default:
                            // Unknown escape: produce literal character (ES5.1 non-strict)
                            sb.Append(_ch);
                            break;
                    }
                }
                else
                {
                    sb.Append(_ch);
                }
                ReadChar();
            }
            if (_ch == quote)
            {
                ReadChar(); // consume closing quote
            }
            else
            {
                isValid = false;
            }

            return sb.ToString();
        }

        private bool IsLetter(char ch)
        {
            return IsIdentifierStart(ch);
        }

        private bool IsDigit(char ch)
        {
            return '0' <= ch && ch <= '9';
        }

        private bool IsLineTerminator(char ch)
        {
            return ch == '\n' || ch == '\r' || ch == '\u2028' || ch == '\u2029';
        }

        private bool IsJsWhiteSpace(char ch)
        {
            return ch == ' ' ||
                   ch == '\t' ||
                   ch == '\v' ||
                   ch == '\f' ||
                   ch == '\u00A0' ||
                   ch == '\uFEFF' ||
                   ch == '\u1680' ||
                   (ch >= '\u2000' && ch <= '\u200A') ||
                   ch == '\u202F' ||
                   ch == '\u205F' ||
                   ch == '\u3000';
        }

        /// <summary>
        /// Check if character is valid as identifier continuation (UnicodeIDContinue)
        /// Includes Unicode combining marks, connector punctuation, ZWNJ, ZWJ
        /// </summary>
        private bool IsUnicodeIdentPart(char ch)
        {
            if (ch <= 127) return false; // ASCII already handled by IsLetter/IsDigit
            var cat = char.GetUnicodeCategory(ch);
            return cat == System.Globalization.UnicodeCategory.NonSpacingMark ||
                   cat == System.Globalization.UnicodeCategory.SpacingCombiningMark ||
                   cat == System.Globalization.UnicodeCategory.DecimalDigitNumber ||
                   cat == System.Globalization.UnicodeCategory.ConnectorPunctuation ||
                   ch == '\u200C' || ch == '\u200D'; // ZWNJ, ZWJ
        }

        private bool IsIdentifierStart(char ch)
        {
            if (ch == '_' || ch == '$') return true;
            if (ch >= 'a' && ch <= 'z') return true;
            if (ch >= 'A' && ch <= 'Z') return true;
            if (ch <= 127) return false;

            // Approximate ECMAScript ID_Start + Other_ID_Start + astral surrogate handling.
            // Surrogates are accepted so astral identifier code points can flow through tokenizer.
            if (char.IsHighSurrogate(ch) || char.IsLowSurrogate(ch)) return true;
            if (ch == '\u2118' || ch == '\u212E' || ch == '\u309B' || ch == '\u309C' || ch == '\u1885' || ch == '\u1886') return true; // Other_ID_Start

            var cat = char.GetUnicodeCategory(ch);
            return cat == System.Globalization.UnicodeCategory.UppercaseLetter ||
                   cat == System.Globalization.UnicodeCategory.LowercaseLetter ||
                   cat == System.Globalization.UnicodeCategory.TitlecaseLetter ||
                   cat == System.Globalization.UnicodeCategory.ModifierLetter ||
                   cat == System.Globalization.UnicodeCategory.OtherLetter ||
                   cat == System.Globalization.UnicodeCategory.LetterNumber;
        }

        private bool IsIdentifierPart(char ch)
        {
            if (IsIdentifierStart(ch)) return true;
            if (ch >= '0' && ch <= '9') return true;
            if (ch == '\u200C' || ch == '\u200D') return true;
            if (ch == '\u00B7' || ch == '\u0387' || ch == '\u19DA' || ch == '\u30FB' || ch == '\uFF65') return true; // Other_ID_Continue
            if (ch >= '\u1369' && ch <= '\u1371') return true; // Other_ID_Continue

            if (ch <= 127) return false;
            var cat = char.GetUnicodeCategory(ch);
            return cat == System.Globalization.UnicodeCategory.NonSpacingMark ||
                   cat == System.Globalization.UnicodeCategory.SpacingCombiningMark ||
                   cat == System.Globalization.UnicodeCategory.DecimalDigitNumber ||
                   cat == System.Globalization.UnicodeCategory.ConnectorPunctuation ||
                   cat == System.Globalization.UnicodeCategory.OtherNotAssigned;
        }

        public static TokenType LookupIdent(string ident)
        {
            if (Keywords.TryGetValue(ident, out TokenType type))
            {
                return type;
            }
            return TokenType.Identifier;
        }

                // Read template literal start: `...` or `...${
        private Token ReadTemplateStart()
        {
            int startLine = _line;
            int startColumn = _column;

            ReadChar(); // consume `

            var sb = new StringBuilder();
            while (_ch != '\0')
            {
                if (_ch == '`')
                {
                    ReadChar();
                    return new Token(TokenType.TemplateNoSubst, sb.ToString(), startLine, startColumn);
                }

                if (_ch == '$' && PeekChar() == '{')
                {
                    ReadChar(); // consume $
                    ReadChar(); // consume {
                    return new Token(TokenType.TemplateHead, sb.ToString(), startLine, startColumn);
                }

                if (_ch == '\\')
                {
                    if (!TryReadTemplateEscape(sb, out var escapeError))
                    {
                        return new Token(TokenType.Illegal, escapeError, startLine, startColumn);
                    }
                    continue;
                }

                sb.Append(_ch);
                ReadChar();
            }

            return new Token(TokenType.Illegal, "Unterminated template literal", startLine, startColumn);
        }

        public Token ReadTemplateContinuation()
        {
            // Parser calls this after finishing a ${...} expression.
            int startLine = _line;
            int startColumn = _column;
            var sb = new StringBuilder();

            while (_ch != '\0')
            {
                if (_ch == '`')
                {
                    ReadChar();
                    return new Token(TokenType.TemplateTail, sb.ToString(), startLine, startColumn);
                }

                if (_ch == '$' && PeekChar() == '{')
                {
                    ReadChar(); // consume $
                    ReadChar(); // consume {
                    return new Token(TokenType.TemplateMiddle, sb.ToString(), startLine, startColumn);
                }

                if (_ch == '\\')
                {
                    if (!TryReadTemplateEscape(sb, out var escapeError))
                    {
                        return new Token(TokenType.Illegal, escapeError, startLine, startColumn);
                    }
                    continue;
                }

                sb.Append(_ch);
                ReadChar();
            }

            return new Token(TokenType.Illegal, "Unterminated template continuation", startLine, startColumn);
        }

        private bool TryReadTemplateEscape(StringBuilder sb, out string error)
        {
            error = null;
            ReadChar(); // consume backslash, _ch now points to escaped char

            if (_ch == '\0')
            {
                error = "Unterminated escape sequence in template literal";
                return false;
            }

            if (IsLineTerminator(_ch))
            {
                // Line continuation in template literals.
                if (_ch == '\r' && PeekChar() == '\n')
                {
                    ReadChar();
                }
                ReadChar();
                return true;
            }

            switch (_ch)
            {
                case 'n': sb.Append('\n'); ReadChar(); return true;
                case 'r': sb.Append('\r'); ReadChar(); return true;
                case 't': sb.Append('\t'); ReadChar(); return true;
                case 'b': sb.Append('\b'); ReadChar(); return true;
                case 'f': sb.Append('\f'); ReadChar(); return true;
                case 'v': sb.Append('\v'); ReadChar(); return true;
                case '\\': sb.Append('\\'); ReadChar(); return true;
                case '`': sb.Append('`'); ReadChar(); return true;
                case '$': sb.Append('$'); ReadChar(); return true;
                case '"': sb.Append('"'); ReadChar(); return true;
                case '\'': sb.Append('\''); ReadChar(); return true;
                case '0':
                    if (IsDigit(PeekChar()))
                    {
                        error = "Invalid legacy octal escape in template literal";
                        return false;
                    }
                    sb.Append('\0');
                    ReadChar();
                    return true;
                case 'x':
                {
                    ReadChar();
                    if (!IsHexDigit(_ch))
                    {
                        error = "Invalid hexadecimal escape sequence in template literal";
                        return false;
                    }
                    char h1 = _ch;
                    ReadChar();
                    if (!IsHexDigit(_ch))
                    {
                        error = "Invalid hexadecimal escape sequence in template literal";
                        return false;
                    }
                    char h2 = _ch;
                    int code = Convert.ToInt32(new string(new[] { h1, h2 }), 16);
                    sb.Append((char)code);
                    ReadChar();
                    return true;
                }
                case 'u':
                {
                    ReadChar();
                    if (_ch == '{')
                    {
                        var hex = new StringBuilder();
                        ReadChar();
                        while (_ch != '}' && _ch != '\0')
                        {
                            if (!IsHexDigit(_ch))
                            {
                                error = "Invalid Unicode escape sequence in template literal";
                                return false;
                            }
                            hex.Append(_ch);
                            if (hex.Length > 6)
                            {
                                error = "Unicode code point escape out of range in template literal";
                                return false;
                            }
                            ReadChar();
                        }

                        if (_ch != '}' || hex.Length == 0)
                        {
                            error = "Invalid Unicode escape sequence in template literal";
                            return false;
                        }

                        int codePoint = Convert.ToInt32(hex.ToString(), 16);
                        if (codePoint > 0x10FFFF)
                        {
                            error = "Unicode code point escape out of range in template literal";
                            return false;
                        }

                        sb.Append(char.ConvertFromUtf32(codePoint));
                        ReadChar();
                        return true;
                    }

                    char[] digits = new char[4];
                    for (int i = 0; i < 4; i++)
                    {
                        if (!IsHexDigit(_ch))
                        {
                            error = "Invalid Unicode escape sequence in template literal";
                            return false;
                        }
                        digits[i] = _ch;
                        if (i < 3)
                        {
                            ReadChar();
                        }
                    }

                    int unit = Convert.ToInt32(new string(digits), 16);
                    sb.Append((char)unit);
                    ReadChar();
                    return true;
                }
                default:
                    if (_ch >= '1' && _ch <= '9')
                    {
                        error = "Invalid legacy octal escape in template literal";
                        return false;
                    }
                    // Non-special escapes are identity escapes in untagged templates.
                    sb.Append(_ch);
                    ReadChar();
                    return true;
            }
        }
        private bool IsRegexStart(Token prev)
        {
            if (prev  == null) return true; // Start of file
            
            switch (prev.Type)
            {
                case TokenType.Assign:
                case TokenType.PlusAssign:
                case TokenType.MinusAssign:
                case TokenType.MulAssign:
                case TokenType.DivAssign:
                case TokenType.LParen:
                case TokenType.LBrace:
                case TokenType.LBracket:
                case TokenType.Comma:
                case TokenType.Colon:
                case TokenType.Question:
                case TokenType.Return:
                case TokenType.Throw:
                case TokenType.Case:
                case TokenType.New:
                case TokenType.Delete:
                case TokenType.Void:
                case TokenType.Typeof:
                case TokenType.Bang:
                case TokenType.NotEq:
                case TokenType.StrictNotEq:
                case TokenType.And:
                case TokenType.Or:
                case TokenType.Arrow:
                case TokenType.Semicolon:
                case TokenType.Else:
                case TokenType.Do:
                case TokenType.While:
                case TokenType.If:
                case TokenType.Await:
                    return true;
                default:
                    return false;
            }
        }

        private string ReadRegexLiteral(out bool isValid)
        {
            isValid = true;
            int start = _position;
            // We are at the first '/'
            ReadChar(); // Consume '/'
            
            bool escape = false;
            bool inClass = false;
            int patternStart = _position;
            
            while (_ch != '\0')
            {
                if (IsLineTerminator(_ch))
                {
                    isValid = false;
                    break;
                }

                if (escape)
                {
                    escape = false;
                }
                else if (_ch == '\\')
                {
                    escape = true;
                }
                else if (_ch == '[')
                {
                    inClass = true;
                }
                else if (_ch == ']')
                {
                    inClass = false;
                }
                else if (_ch == '/' && !inClass)
                {
                    break; // End of pattern
                }
                
                ReadChar();
            }
            
            if (_ch != '/')
            {
                isValid = false;
                return _input.Substring(start, Math.Max(0, _position - start));
            }

            string pattern = _input.Substring(patternStart, _position - patternStart);
            ReadChar(); // Consume closing '/'

            var flags = new HashSet<char>();
            // Parse flags (g, i, m, s, u, y, d, v)
            while (IsIdentifierPart(_ch))
            {
                char flag = _ch;
                if (!(flag == 'g' || flag == 'i' || flag == 'm' || flag == 's' || flag == 'u' || flag == 'y' || flag == 'd' || flag == 'v'))
                {
                    isValid = false;
                }
                if (!flags.Add(flag))
                {
                    isValid = false;
                }
                ReadChar();
            }

            if (_ch == '\\' && PeekChar() == 'u')
            {
                isValid = false;
            }

            if (HasQuantifiedLookbehindAssertion(pattern))
            {
                isValid = false;
            }

            return _input.Substring(start, _position - start);
        }

        private bool HasQuantifiedLookbehindAssertion(string pattern)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(pattern, @"\(\?<=[^)]*\)[?*+{]"))
            {
                return true;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(pattern, @"\(\?<![^)]*\)[?*+{]"))
            {
                return true;
            }

            return false;
        }

        public string GetCodeContext(int line, int column = -1, int contextLines = 2)
        {
            try
            {
                var lines = _input.Split(new[] { "\n", "\r\n" }, StringSplitOptions.None);
                
                var startLine = Math.Max(0, line - contextLines - 1);
                var endLine = Math.Min(lines.Length - 1, line + contextLines - 1);
                
                var sb = new StringBuilder();
                for (int i = startLine; i <= endLine; i++)
                {
                    string prefix = (i + 1) == line ? ">> " : "   ";
                    string lineStr = lines[i];
                    
                    // Truncate non-error lines or extremely long lines
                    if (lineStr.Length > 200)
                    {
                        lineStr = lineStr.Substring(0, 197) + "...";
                    }
                    
                    sb.AppendLine($"{prefix}{i + 1}: {lineStr}");
                }
                
                // If column is provided, show precise location snippet
                if (column >= 0 && line > 0 && line <= lines.Length)
                {
                     sb.AppendLine($"   Column: {column}");
                     
                     string errorLine = lines[line - 1];
                     // Get 50 chars before and after
                     if (errorLine.Length > 0)
                     {
                         int start = Math.Max(0, column - 50);
                         int length = Math.Min(errorLine.Length - start, 100);
                         if (start < errorLine.Length)
                         {
                             string snippet = errorLine.Substring(start, length);
                             
                             sb.AppendLine("   Snippet:");
                             sb.AppendLine("   " + snippet);
                             
                             // Pointer
                             int pointerPos = column - start;
                             if (pointerPos >= 0 && pointerPos <= snippet.Length)
                             {
                                 sb.AppendLine("   " + new string('-', pointerPos) + "^");
                             }
                         }
                     }
                }
                
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error retrieving context: {ex.Message}";
            }
        }
    }
}



