using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 13.2.8.4 GetTemplateObject (audit JSFE-008). The template object is
/// built once per source site, frozen, and cooks illegal escape sequences to
/// undefined rather than to whatever the escape happened to decode to.
/// </summary>
public sealed class TaggedTemplateObjectTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    private static bool RunBool(string source) => Run(source).AsBoolean();

    private static string RunString(string source) => Run(source).AsString();

    private const string Tag = "function tag(strings) { return strings; } ";

    [Fact]
    public void SameSiteYieldsTheSameObjectAcrossEvaluations()
    {
        Assert.True(RunBool(Tag + "function go(x) { return tag`a${x}`; } go(1) === go(2);"));
    }

    [Fact]
    public void SameSiteYieldsTheSameObjectAcrossSeparateClosures()
    {
        // Each factory() call makes a new function object, but they share one
        // compiled body and so one template site.
        Assert.True(RunBool(Tag +
            "function factory() { return function () { return tag`h${1}t`; }; } " +
            "factory()() === factory()();"));
    }

    [Fact]
    public void DifferentSitesYieldDifferentObjects()
    {
        Assert.True(RunBool(Tag + "tag`a${1}` !== tag`b${1}`;"));
    }

    [Fact]
    public void EachEvalIsItsOwnSite()
    {
        Assert.True(RunBool(Tag + "eval('tag`x${1}`') !== eval('tag`x${1}`');"));
    }

    [Fact]
    public void TemplateObjectAndItsRawArrayAreFrozen()
    {
        Assert.True(RunBool(Tag + "var t = tag`a${1}`; Object.isFrozen(t) && Object.isFrozen(t.raw);"));
    }

    [Fact]
    public void RawIsNonWritableNonEnumerableNonConfigurable()
    {
        Assert.Equal("false,false,false", RunString(Tag +
            "var d = Object.getOwnPropertyDescriptor(tag`a${1}`, 'raw'); " +
            "'' + d.writable + ',' + d.enumerable + ',' + d.configurable;"));
    }

    [Fact]
    public void CookedAndRawCarryEveryQuasi()
    {
        Assert.Equal("3:a|b|c:a|b|c", RunString(Tag +
            "var t = tag`a${1}b${2}c`; t.length + ':' + t.join('|') + ':' + t.raw.join('|');"));
    }

    [Theory]
    [InlineData(@"\unicode")]
    [InlineData(@"\u{110000}")]
    [InlineData(@"\xg1")]
    [InlineData(@"\01")]
    [InlineData(@"\8")]
    public void IllegalEscapeSequenceCooksToUndefinedAndKeepsRaw(string quasi)
    {
        Assert.Equal("undefined|" + quasi, RunString(
            "function t(s) { return String(s[0]) + '|' + s.raw[0]; } t`" + quasi + "`;"));
    }

    [Fact]
    public void LegalEscapeSequencesStillCook()
    {
        Assert.Equal("abcd", RunString(
            "function t(s) { return s[0]; } t`a\u0062c\x64`;"));
    }
}
