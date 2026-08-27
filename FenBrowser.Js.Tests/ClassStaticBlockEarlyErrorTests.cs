using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ClassStaticBlockEarlyErrorTests
{
    [Theory]
    [InlineData("class Derived extends Base { static { super(); } }")]
    [InlineData("class C { static { arguments; } }")]
    [InlineData("class C { static { (() => arguments)(); } }")]
    public void RejectsForbiddenStaticBlockSyntax(string source)
    {
        Assert.Throws<JsParserException>(() => JsParser.ParseScript(new SourceText(source)));
    }

    [Theory]
    [InlineData("class Derived extends Base { static { super.value; } }")]
    [InlineData("class C { static { function nested() { return arguments; } } }")]
    public void AllowsStaticBlockSyntaxAcrossValidBoundaries(string source)
    {
        Assert.Single(JsParser.ParseScript(new SourceText(source)).Body);
    }
}
