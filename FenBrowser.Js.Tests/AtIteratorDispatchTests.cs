using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class AtIteratorDispatchTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact]
    public void ArrayPrototypeSymbolIteratorIsValues()
    {
        Assert.True(RunBool("Array.prototype[Symbol.iterator] === Array.prototype.values;"));
    }

    [Fact]
    public void ForOfOnUserIterableUsesSymbolIterator()
    {
        Assert.Equal(6, RunNum(@"
            var obj = {};
            obj[Symbol.iterator] = function(){
                var i = 0;
                return { next: function(){
                    i = i + 1;
                    return { value: i, done: i > 3 };
                }};
            };
            var s = 0;
            for (var v of obj) s = s + v;
            s;
        "));
    }

    [Fact]
    public void ForOfOverSetUsesValuesIterator()
    {
        Assert.Equal(6, RunNum("var s = 0; for (var v of new Set([1,2,3])) s = s + v; s;"));
    }

    [Fact]
    public void ForOfOverMapYieldsEntries()
    {
        Assert.Equal("a1b2", RunStr("var r = ''; for (var e of new Map([['a',1],['b',2]])) r = r + e[0] + e[1]; r;"));
    }

    [Fact]
    public void ArrayIteratorIsItselfIterable()
    {
        Assert.True(RunBool("var it = [1].values(); it[Symbol.iterator]() === it;"));
    }

    [Fact]
    public void ForOfOnArrayIteratorYieldsRemainingValues()
    {
        Assert.Equal(6, RunNum("var s = 0; for (var v of [1,2,3].values()) s = s + v; s;"));
    }
}
