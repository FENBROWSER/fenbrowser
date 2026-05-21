using FenBrowser.Js.Lexer;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class LexerTests
{
    [Fact]
    public void LexesKeywordsIdentifiersAndPunctuators()
    {
        var lexer = new JsLexer(new SourceText("let x = 1;"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Keyword, tokens[0].Kind);
        Assert.Equal("let", tokens[0].Text);
        Assert.Equal(TokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("x", tokens[1].Text);
        Assert.Equal(TokenKind.Punctuator, tokens[2].Kind);
        Assert.Equal("=", tokens[2].Text);
        Assert.Equal(TokenKind.Number, tokens[3].Kind);
        Assert.Equal("1", tokens[3].Text);
        Assert.Equal(TokenKind.Punctuator, tokens[4].Kind);
        Assert.Equal(";", tokens[4].Text);
        Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
    }

    [Fact]
    public void SkipsLineAndBlockComments()
    {
        var source = "// line\nconst /* block */ y = 2";
        var lexer = new JsLexer(new SourceText(source));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Keyword, tokens[0].Kind);
        Assert.Equal("const", tokens[0].Text);
        Assert.Equal(TokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("y", tokens[1].Text);
        Assert.Equal(TokenKind.Punctuator, tokens[2].Kind);
        Assert.Equal("=", tokens[2].Text);
        Assert.Equal(TokenKind.Number, tokens[3].Kind);
        Assert.Equal("2", tokens[3].Text);
    }

    [Fact]
    public void UnterminatedStringDoesNotCrashAndProducesToken()
    {
        var lexer = new JsLexer(new SourceText("'abc"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.String, tokens[0].Kind);
        Assert.Equal("'abc", tokens[0].Text);
        Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
    }

    [Fact]
    public void StringWithUnescapedLineTerminatorProducesUnknownToken()
    {
        var lexer = new JsLexer(new SourceText("\"a\r\nb\";"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Unknown, tokens[0].Kind);
        Assert.Equal("\"a\r\n", tokens[0].Text);
        Assert.Equal(2, tokens[1].Span.Line);
        Assert.Equal(1, tokens[1].Span.Column);
    }

    [Fact]
    public void IdentifierUnicodeEscapeCannotEncodeLineTerminator()
    {
        var lexer = new JsLexer(new SourceText("var\\u000Ax;"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Unknown, tokens[0].Kind);
        Assert.Equal("var\\u000A", tokens[0].Text);
    }

    [Fact]
    public void IdentifierUnicodeEscapeCannotEncodeWhiteSpace()
    {
        var lexer = new JsLexer(new SourceText("var\\u0009x;"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Unknown, tokens[0].Kind);
        Assert.Equal("var\\u0009", tokens[0].Text);
    }

    [Fact]
    public void IdentifierUnicodeEscapeCannotEncodePunctuator()
    {
        var lexer = new JsLexer(new SourceText("\\u0023\\u0021"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Unknown, tokens[0].Kind);
        Assert.Equal("\\u0023\\u0021", tokens[0].Text);
    }

    [Fact]
    public void NumericLiteralFollowedByIdentifierStartIsMalformed()
    {
        var lexer = new JsLexer(new SourceText("3in []"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Unknown, tokens[0].Kind);
        Assert.Equal("3", tokens[0].Text);
    }

    [Fact]
    public void UnterminatedBlockCommentDoesNotCrash()
    {
        var lexer = new JsLexer(new SourceText("/* unclosed"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Unknown, tokens[0].Kind);
        Assert.Equal("/* unclosed", tokens[0].Text);
        Assert.Equal(TokenKind.EndOfFile, tokens[^1].Kind);
    }

    [Fact]
    public void SkipsHtmlCloseCommentAtLineStart()
    {
        var source = "--> hidden\nlet x = 1;";
        var lexer = new JsLexer(new SourceText(source));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Keyword, tokens[0].Kind);
        Assert.Equal("let", tokens[0].Text);
        Assert.Equal(TokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("x", tokens[1].Text);
    }

    [Fact]
    public void SkipsHtmlOpenCommentAtLineStart()
    {
        var source = "<!-- hidden\nlet y = 2;";
        var lexer = new JsLexer(new SourceText(source));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Keyword, tokens[0].Kind);
        Assert.Equal("let", tokens[0].Text);
        Assert.Equal(TokenKind.Identifier, tokens[1].Kind);
        Assert.Equal("y", tokens[1].Text);
    }

    [Fact]
    public void TreatsCrLfAsSingleLineTerminatorForTokenSpans()
    {
        var lexer = new JsLexer(new SourceText("let a = 1;\r\nlet b = 2;"));
        var tokens = lexer.LexAll();

        var secondLet = tokens.First(t => t.Text == "let" && t.Span.Start > 0);
        Assert.Equal(2, secondLet.Span.Line);
        Assert.Equal(1, secondLet.Span.Column);
    }

    [Fact]
    public void SkipsEcmaScriptWhiteSpaceCharacters()
    {
        var lexer = new JsLexer(new SourceText("/x/g\u00A0\u2003\f;"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.RegularExpression, tokens[0].Kind);
        Assert.Equal(TokenKind.Punctuator, tokens[1].Kind);
        Assert.Equal(";", tokens[1].Text);
    }

    [Fact]
    public void HtmlCloseCommentStopsAtCarriageReturnLineTerminator()
    {
        var lexer = new JsLexer(new SourceText("--> hidden\rlet x = 1;"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Keyword, tokens[0].Kind);
        Assert.Equal("let", tokens[0].Text);
        Assert.Equal(2, tokens[0].Span.Line);
        Assert.Equal(1, tokens[0].Span.Column);
    }

    [Fact]
    public void SkipsHashbangCommentAtSourceStart()
    {
        var lexer = new JsLexer(new SourceText("#! /usr/bin/env fenjs\nlet x = 1;"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Keyword, tokens[0].Kind);
        Assert.Equal("let", tokens[0].Text);
        Assert.Equal(2, tokens[0].Span.Line);
        Assert.Equal(1, tokens[0].Span.Column);
    }

    [Fact]
    public void HashbangCommentStopsAtCarriageReturn()
    {
        var lexer = new JsLexer(new SourceText("#! comment\r{}"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Punctuator, tokens[0].Kind);
        Assert.Equal("{", tokens[0].Text);
        Assert.Equal(2, tokens[0].Span.Line);
        Assert.Equal(1, tokens[0].Span.Column);
    }

    [Fact]
    public void LexesTemplateLiteralToken()
    {
        var lexer = new JsLexer(new SourceText("`a ${b}`"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Template, tokens[0].Kind);
        Assert.Equal("`a ${b}`", tokens[0].Text);
    }
}
