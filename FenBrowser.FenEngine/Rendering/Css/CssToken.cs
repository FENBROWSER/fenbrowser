using System;

namespace FenBrowser.FenEngine.Rendering.Css
{
    public enum CssTokenType
    {
        Ident,
        Function,
        AtKeyword,
        Hash,
        String,
        BadString,
        Url,
        BadUrl,
        Delim,
        Number,
        Percentage,
        Dimension,
        Whitespace,
        Comment,
        CDO,            // <!--
        CDC,            // -->
        Colon,          // :
        Semicolon,      // ;
        Comma,          // ,
        LeftBracket,    // [
        RightBracket,   // ]
        LeftParen,      // (
        RightParen,     // )
        LeftBrace,      // {
        RightBrace,     // }
        EOF
    }

    public enum HashType
    {
        Id,
        Unrestricted
    }

    public struct CssToken
    {
        public CssTokenType Type { get; }
        public string Value { get; }        // Used for Ident, String, Url, AtKeyword, Hash
        public string Unit { get; }         // Used for Dimension
        public double NumericValue { get; } // Used for Number, Percentage, Dimension
        public char Delimiter { get; }      // Used for Delim
        public HashType HashType { get; }   // Used for Hash
        public bool IsInteger { get; }      // Used for Number (type flag)

        public CssToken(CssTokenType type) : this()
        {
            Type = type;
        }

        public CssToken(CssTokenType type, string value) : this()
        {
            Type = type;
            Value = value;
        }

        public CssToken(CssTokenType type, char delimiter) : this()
        {
            Type = type;
            Delimiter = delimiter;
        }

        public CssToken(CssTokenType type, double number, bool isInteger = false) : this()
        {
            Type = type;
            NumericValue = number;
            IsInteger = isInteger;
        }

        public CssToken(CssTokenType type, double number, string unit) : this()
        {
            Type = type;
            NumericValue = number;
            Unit = unit;
        }

        public CssToken(CssTokenType type, string value, HashType hashType) : this()
        {
            Type = type;
            Value = value;
            HashType = hashType;
        }

        public override string ToString()
        {
            return Type switch
            {
                CssTokenType.Ident => $"Ident({Value})",
                CssTokenType.Function => $"Function({Value})",
                CssTokenType.AtKeyword => $"@{Value}",
                CssTokenType.Hash => $"#{Value}",
                CssTokenType.String => $"\"{Value}\"",
                CssTokenType.Url => $"url({Value})",
                CssTokenType.Delim => $"Delim({Delimiter})",
                CssTokenType.Number => $"Number({NumericValue})",
                CssTokenType.Percentage => $"Percentage({NumericValue}%)",
                CssTokenType.Dimension => $"Dimension({NumericValue}{Unit})",
                CssTokenType.Whitespace => "Whitespace",
                _ => Type.ToString()
            };
        }
    }
}
