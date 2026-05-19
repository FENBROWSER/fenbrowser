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
    public void UnterminatedBlockCommentDoesNotCrash()
    {
        var lexer = new JsLexer(new SourceText("/* unclosed"));
        var tokens = lexer.LexAll();

        Assert.Single(tokens);
        Assert.Equal(TokenKind.EndOfFile, tokens[0].Kind);
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
    public void LexesTemplateLiteralToken()
    {
        var lexer = new JsLexer(new SourceText("`a ${b}`"));
        var tokens = lexer.LexAll();

        Assert.Equal(TokenKind.Template, tokens[0].Kind);
        Assert.Equal("`a ${b}`", tokens[0].Text);
    }
}
