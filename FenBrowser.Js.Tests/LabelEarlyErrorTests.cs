using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class LabelEarlyErrorTests
{
    [Theory]
    [InlineData("duplicate: duplicate: ;")]
    [InlineData("function nested() { duplicate: duplicate: ; }")]
    [InlineData("const nested = () => { duplicate: duplicate: ; };")]
    [InlineData("async function nested(values) { duplicate: for await (const value of values) { duplicate: ; } }")]
    public void ScriptRejectsDuplicateActiveLabels(string source)
    {
        Assert.Throws<JsParserException>(() => JsParser.ParseScript(new SourceText(source)));
    }

    [Theory]
    [InlineData("reused: ; reused: ;")]
    [InlineData("outer: { function nested() { outer: ; } }")]
    [InlineData("outer: { class C { static { outer: ; } } }")]
    public void LabelsMayBeReusedOutsideTheirActiveBoundary(string source)
    {
        var program = JsParser.ParseScript(new SourceText(source));

        Assert.NotEmpty(program.Body);
    }
}
