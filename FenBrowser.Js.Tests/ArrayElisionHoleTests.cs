using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 13.2.4 ArrayLiteral elisions produce true holes: the index is absent
// (HasProperty false), and array iteration methods skip holes per their spec
// algorithms (HasProperty(O, Pk) gate).
public sealed class ArrayElisionHoleTests
{
    private static string Eval(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void HoleIsAbsent() => Assert.Equal("false", Eval("String(1 in [0, , 2]);"));

    [Fact]
    public void ExplicitUndefinedIsPresent() => Assert.Equal("true", Eval("String(1 in [0, undefined, 2]);"));

    [Fact]
    public void LengthCountsInteriorHoles() => Assert.Equal("3", Eval("String([0, , 2].length);"));

    [Fact]
    public void LengthCountsTrailingHole() => Assert.Equal("2", Eval("String([1, , ].length);"));

    [Fact]
    public void ForEachSkipsHoles()
        => Assert.Equal("0,2", Eval("var s = []; [0, , 2].forEach(function(v, i){ s.push(i); }); s.join(',');"));

    [Fact]
    public void MapSkipsHolesButPreservesLength()
        => Assert.Equal("0,2", Eval("var s = []; [0, , 2].map(function(v, i){ s.push(i); return v; }); s.join(',');"));

    [Fact]
    public void ReduceSkipsHoles()
        => Assert.Equal("0,2", Eval("var s = []; [5, , 7].reduce(function(a, v, i){ s.push(i); return a; }, 0); s.join(',');"));

    [Fact]
    public void IndexOfDoesNotMatchHoles()
        => Assert.Equal("-1", Eval("String([0, , 2].indexOf(undefined));"));

    [Fact]
    public void DenseArraysStillFullyIterated()
        => Assert.Equal("0,1,2", Eval("var s = []; [5, 6, 7].forEach(function(v, i){ s.push(i); }); s.join(',');"));

    [Fact]
    public void ArrayDestructuringElisionStillSkips()
        => Assert.Equal("3", Eval("var [, b] = [2, 3]; String(b);"));
}
