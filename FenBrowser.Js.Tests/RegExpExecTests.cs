using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RegExpExecTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void ExecReturnsNullOnNoMatch()
    {
        var result = Run("/abc/.exec('xyz');");
        Assert.True(result.Tag == JsValueTag.Null);
    }

    [Fact]
    public void ExecReturnsArrayOnMatch()
    {
        var result = Run("var r = /abc/.exec('abc'); r !== null && r[0] === 'abc';");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ExecResultHasIndex()
    {
        var result = Run("var r = /abc/.exec('xxabc'); r.index;");
        Assert.Equal(2.0, result.AsNumber(), 4);
    }

    [Fact]
    public void ExecResultHasInput()
    {
        var result = Run("var r = /abc/.exec('xxabc'); r.input;");
        Assert.Equal("xxabc", result.AsString());
    }

    [Fact]
    public void ExecResultHasGroups()
    {
        var result = Run("var r = /abc/.exec('abc'); r.groups;");
        Assert.True(result.Tag == JsValueTag.Undefined);
    }

    [Fact]
    public void ExecCapturesGroups()
    {
        var result = Run("var r = /a(b)c/.exec('abc'); r[1];");
        Assert.Equal("b", result.AsString());
    }

    [Fact]
    public void ExecGlobalAdvancesLastIndex()
    {
        var result = Run("var r = /a/g; r.exec('aba'); var li = r.lastIndex; r.exec('aba'); r.lastIndex > li;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ExecLastIndexUpdated()
    {
        var result = Run("var r = /abc/g; r.exec('abcabc'); r.lastIndex;");
        Assert.Equal(3.0, result.AsNumber(), 4);
    }

    [Fact]
    public void ExecGlobalNullResetsLastIndex()
    {
        var result = Run("var r = /x/g; r.lastIndex = 5; r.exec('abc'); r.lastIndex;");
        Assert.Equal(0.0, result.AsNumber(), 4);
    }

    [Fact]
    public void ExecStickyMustMatchAtLastIndex()
    {
        // sticky (y) requires match at EXACTLY lastIndex, not later.
        // lastIndex=0, text='xabc' — 'abc' is at pos 1, so /abc/y won't match.
        var result = Run("var r = /abc/y; r.lastIndex = 0; var m = r.exec('xabc'); m === null;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ExecStickyMatchesAtLastIndex()
    {
        // sticky should match when pattern starts exactly at lastIndex.
        var result = Run("var r = /bc/y; r.lastIndex = 1; var m = r.exec('abc'); m !== null && m[0] === 'bc';");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ExecNonGlobalDoesNotAdvanceLastIndex()
    {
        var result = Run("var r = /abc/; r.exec('abcabc'); r.lastIndex;");
        Assert.Equal(0.0, result.AsNumber(), 4);
    }

    [Fact]
    public void ExecNoArgDefaultString()
    {
        var result = Run("var r = /undefined/.exec(); r !== null && r[0] === 'undefined';");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ExecOnRegexLiteral()
    {
        var result = Run("/abc/.exec('abc')[0];");
        Assert.Equal("abc", result.AsString());
    }

    [Fact]
    public void ReplaceUsesOwnExecOverride()
    {
        var result = Run("""
            var r = /./;
            r.exec = function() {
              return { length: 1, 0: "x", index: 0 };
            };
            r[Symbol.replace]("a", "b");
            """);
        Assert.Equal("b", result.AsString());
    }

    [Fact]
    public void ReplaceReadsNamedCapturesOnSpecPath()
    {
        var result = Run("""
            var r = /./;
            r.exec = function() {
              return { length: 1, 0: "", index: 0, groups: { foo: "bar" } };
            };
            r[Symbol.replace]("a", "$<foo>");
            """);
        Assert.Equal("bara", result.AsString());
    }

    [Fact]
    public void ReplaceBoxesPrimitiveNamedCapturesOnSpecPath()
    {
        var result = Run("""
            var r = /./;
            r.exec = function() {
              return { length: 1, 0: "b", index: 1, groups: "123" };
            };
            r[Symbol.replace]("ab", "[$<length>]");
            """);
        Assert.Equal("a[3]", result.AsString());
    }

    [Fact]
    public void ReplacePropagatesNamedCaptureToStringErrors()
    {
        var result = Run("""
            var r = /./;
            r.exec = function() {
              return {
                length: 1,
                0: "",
                index: 0,
                groups: { foo: { toString: function() { throw "boom"; } } }
              };
            };
            try {
              r[Symbol.replace]("a", "$<foo>");
              false;
            } catch (e) {
              e === "boom";
            }
            """);
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ReplaceThrowsWhenNamedCapturesCannotBeBoxed()
    {
        var result = Run("""
            var r = /./;
            r.exec = function() {
              return { length: 1, 0: "", index: 0, groups: null };
            };
            try {
              r[Symbol.replace]("bar", "");
              false;
            } catch (e) {
              e instanceof TypeError;
            }
            """);
        Assert.True(result.AsBoolean());
    }
}
