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

        Extends,      // extends
        Super,        // super
        Static,       // static
        Import,       // import
        Export,       // export
        From,         // from
        As,           // as

        // JavaScript special operators
        Question,   // ?
        Arrow,      // =>
        Regex,      // /pattern/flags
        Backtick,   // ` (template literal start)
        TemplateString, // Template literal content
        TemplateExprStart, // ${
        TemplateExprEnd,   // }
        Ellipsis           // ...
    }

    public class Token
    {
        public TokenType Type { get; }
        public string Literal { get; }
        public int Line { get; }
        public int Column { get; }

        public Token(TokenType type, string literal, int line, int column)
        {
            Type = type;
            Literal = literal;
            Line = line;
            Column = column;
        }

        public override string ToString()
        {
            return $"Token({Type}, \"{Literal}\")";
        }
    }

    public class Lexer
    {
        private readonly string _input;
        private int _position;      // current position in input (points to current char)
        private int _readPosition;  // current reading position in input (after current char)
        private char _ch;           // current char under examination
        private int _line = 1;
        private int _column = 0;
        private Token _prevToken; // Track previous token for regex vs division context

        private static readonly Dictionary<string, TokenType> Keywords;

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
                };
            }
            catch (Exception ex)
            {
                 // Ensure we don't crash, but parsing will be broken for keywords
                System.Diagnostics.Debug.WriteLine($"Error initializing Lexer keywords: {ex}");
                Keywords = new Dictionary<string, TokenType>();
            }
        }

        public Lexer(string input)
        {
            _input = input;
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
            SkipWhitespace();

            Token token;
            int startColumn = _column;

            switch (_ch)
            {
                case '=':
                    if (PeekChar() == '=')
                    {
                        ReadChar();
                        if (PeekChar() == '=')
                        {
                            ReadChar();
                            token = new Token(TokenType.StrictEq, "===", _line, startColumn);
                        }
                        else
                        {
                            token = new Token(TokenType.Eq, "==", _line, startColumn);
                        }
                    }
                    else if (PeekChar() == '>')
                    {
                        ReadChar();
                        token = new Token(TokenType.Arrow, "=>", _line, startColumn);
                    }
                    else
                    {
                        token = new Token(TokenType.Assign, "=", _line, startColumn);
                    }
                    break;
                case '?':
                    token = new Token(TokenType.Question, "?", _line, startColumn);
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
                        return NextToken();
                    }
                    else if (PeekChar() == '*')
                    {
                        SkipBlockComment();
                        return NextToken();
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
                            string regex = ReadRegexLiteral();
                            token = new Token(TokenType.Regex, regex, _line, startColumn);
                        }
                        else
                        {
                            token = new Token(TokenType.Slash, "/", _line, startColumn);
                        }
                    }
                    break;
                case '*':
                    if (PeekChar() == '=')
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
                    token = new Token(TokenType.Percent, "%", _line, startColumn);
                    break;
                case '<':
                    if (PeekChar() == '=')
                    {
                        ReadChar();
                        token = new Token(TokenType.LtEq, "<=", _line, startColumn);
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
                    else
                    {
                        token = new Token(TokenType.Gt, ">", _line, startColumn);
                    }
                    break;
                case '&':
                    if (PeekChar() == '&')
                    {
                        ReadChar();
                        token = new Token(TokenType.And, "&&", _line, startColumn);
                    }
                    else
                    {
                        token = new Token(TokenType.Illegal, "&", _line, startColumn);
                    }
                    break;
                case '|':
                    if (PeekChar() == '|')
                    {
                        ReadChar();
                        token = new Token(TokenType.Or, "||", _line, startColumn);
                    }
                    else
                    {
                        token = new Token(TokenType.Illegal, "|", _line, startColumn);
                    }
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
                    token = new Token(TokenType.String, ReadString(_ch), _line, startColumn);
                    break;
                case '`':
                    token = new Token(TokenType.String, ReadTemplateLiteral(), _line, startColumn);
                    break;
                case '\0':
                    token = new Token(TokenType.Eof, "", _line, startColumn);
                    break;
                default:
                    if (IsLetter(_ch))
                    {
                        string literal = ReadIdentifier();
                        TokenType type = LookupIdent(literal);
                        var t = new Token(type, literal, _line, startColumn);
                        _prevToken = t;
                        return t;
                    }
                    else if (IsDigit(_ch))
                    {
                        var t = new Token(TokenType.Number, ReadNumber(), _line, startColumn);
                        _prevToken = t;
                        return t;
                    }
                    else
                    {
                        token = new Token(TokenType.Illegal, _ch.ToString(), _line, startColumn);
                    }
                    break;
            }

            ReadChar();
            _prevToken = token;
            return token;
        }

        private void SkipWhitespace()
        {
            while (_ch == ' ' || _ch == '\t' || _ch == '\n' || _ch == '\r')
            {
                if (_ch == '\n')
                {
                    _line++;
                    _column = 0;
                }
                ReadChar();
            }
        }

        private void SkipLineComment()
        {
            // Consume first slash
            ReadChar();
            // Consume second slash
            ReadChar();
            
            while (_ch != '\n' && _ch != '\0')
            {
                ReadChar();
            }
            // Whitespace skipping in NextToken will handle the newline
        }

        private void SkipBlockComment()
        {
             // Consume /
            ReadChar();
            // Consume *
            ReadChar();

            while (true)
            {
                if (_ch == '\0') break;
                if (_ch == '*' && PeekChar() == '/')
                {
                    ReadChar(); // *
                    ReadChar(); // /
                    break;
                }
                if (_ch == '\n')
                {
                    _line++;
                    _column = 0;
                }
                ReadChar();
            }
        }

        private string ReadIdentifier()
        {
            int position = _position;
            while (IsLetter(_ch) || IsDigit(_ch))
            {
                ReadChar();
            }
            return _input.Substring(position, _position - position);
        }

        private string ReadNumber()
        {
            int position = _position;
            while (IsDigit(_ch))
            {
                ReadChar();
            }
            if (_ch == '.' && IsDigit(PeekChar()))
            {
                ReadChar();
                while (IsDigit(_ch))
                {
                    ReadChar();
                }
            }
            return _input.Substring(position, _position - position);
        }

        private string ReadString(char quote)
        {
            int position = _position + 1;
            while (true)
            {
                ReadChar();
                if (_ch == quote || _ch == '\0')
                {
                    break;
                }
                if (_ch == '\\')
                {
                    ReadChar(); // Skip escape
                }
            }
            return _input.Substring(position, _position - position); // Exclude quotes
        }

        private bool IsLetter(char ch)
        {
            return 'a' <= ch && ch <= 'z' || 'A' <= ch && ch <= 'Z' || ch == '_' || ch == '$';
        }

        private bool IsDigit(char ch)
        {
            return '0' <= ch && ch <= '9';
        }

        private TokenType LookupIdent(string ident)
        {
            if (Keywords.TryGetValue(ident, out TokenType type))
            {
                return type;
            }
            return TokenType.Identifier;
        }

        // Read template literal: `Hello ${name}!`
        // For simplicity, we'll evaluate ${} expressions and return the concatenated string
        private string ReadTemplateLiteral()
        {
            var result = new StringBuilder();
            ReadChar(); // consume opening `
            
            while (_ch != '`' && _ch != '\0')
            {
                if (_ch == '$' && PeekChar() == '{')
                {
                    // Found ${...} - need to read the expression
                    ReadChar(); // consume $
                    ReadChar(); // consume {
                    
                    // Read until matching }
                    int depth = 1;
                    var expr = new StringBuilder();
                    while (depth > 0 && _ch != '\0')
                    {
                        if (_ch == '{') depth++;
                        if (_ch == '}') depth--;
                        if (depth > 0)
                        {
                            expr.Append(_ch);
                            ReadChar();
                        }
                    }
                    
                    // For now, just include a placeholder - the interpreter will handle this
                    result.Append("${");
                    result.Append(expr.ToString());
                    result.Append("}");
                    
                    if (_ch == '}') ReadChar(); // consume closing }
                }
                else if (_ch == '\\')
                {
                    // Handle escape sequences
                    ReadChar();
                    switch (_ch)
                    {
                        case 'n': result.Append('\n'); break;
                        case 't': result.Append('\t'); break;
                        case 'r': result.Append('\r'); break;
                        case '\\': result.Append('\\'); break;
                        case '`': result.Append('`'); break;
                        case '$': result.Append('$'); break;
                        default: result.Append(_ch); break;
                    }
                    ReadChar();
                }
                else
                {
                    result.Append(_ch);
                    ReadChar();
                }
            }
            
            // Don't consume closing backtick - let it be consumed by ReadChar after
            return result.ToString();
        }
        private bool IsRegexStart(Token prev)
        {
            if (prev == null) return true; // Start of file
            
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
                    return true;
                default:
                    return false;
            }
        }

        private string ReadRegexLiteral()
        {
            int start = _position;
            // We are at the first '/'
            ReadChar(); // Consume '/'
            
            bool escape = false;
            bool inClass = false;
            
            while (_ch != '\0')
            {
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
            
            if (_ch == '/')
            {
                ReadChar(); // Consume closing '/'
            }
            
            // Parse flags (g, i, m, u, y)
            while (IsLetter(_ch))
            {
                ReadChar();
            }
            
            return _input.Substring(start, _position - start);
        }
    }
}
