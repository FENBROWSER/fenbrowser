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
        
        // Comparison
        Lt,
        Gt,
        Eq,
        NotEq,
        LtEq,
        GtEq,
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
        Await
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

        private static readonly Dictionary<string, TokenType> Keywords = new Dictionary<string, TokenType>
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
        };

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
                        char ch = _ch;
                        ReadChar();
                        token = new Token(TokenType.Eq, "==", _line, startColumn);
                    }
                    else
                    {
                        token = new Token(TokenType.Assign, "=", _line, startColumn);
                    }
                    break;
                case '+':
                    token = new Token(TokenType.Plus, "+", _line, startColumn);
                    break;
                case '-':
                    token = new Token(TokenType.Minus, "-", _line, startColumn);
                    break;
                case '!':
                    if (PeekChar() == '=')
                    {
                        char ch = _ch;
                        ReadChar();
                        token = new Token(TokenType.NotEq, "!=", _line, startColumn);
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
                    else
                    {
                        token = new Token(TokenType.Slash, "/", _line, startColumn);
                    }
                    break;
                case '*':
                    token = new Token(TokenType.Asterisk, "*", _line, startColumn);
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
                    token = new Token(TokenType.Dot, ".", _line, startColumn);
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
                case '\0':
                    token = new Token(TokenType.Eof, "", _line, startColumn);
                    break;
                default:
                    if (IsLetter(_ch))
                    {
                        string literal = ReadIdentifier();
                        TokenType type = LookupIdent(literal);
                        return new Token(type, literal, _line, startColumn);
                    }
                    else if (IsDigit(_ch))
                    {
                        return new Token(TokenType.Number, ReadNumber(), _line, startColumn);
                    }
                    else
                    {
                        token = new Token(TokenType.Illegal, _ch.ToString(), _line, startColumn);
                    }
                    break;
            }

            ReadChar();
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
    }
}
