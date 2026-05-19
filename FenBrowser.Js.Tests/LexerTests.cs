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
}
