using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 23.1.3.2 Array.prototype.concat ( ...arguments ).
// Step 5.c.iii: "If n + len > 2^53 - 1, throw a TypeError exception."
// Pre-fix the engine looped up to int.MaxValue (~2.1B iterations) when
// a spreadable source reported length = Number.MAX_SAFE_INTEGER,
// triggering test262 timeouts on
//   built-ins/Array/prototype/concat/arg-length-{exceeding,near}-integer-limit.js.
public class ArrayConcatOverflowTests
{
    private static (bool ok, string? msg) RunCatching(string src)
    {
        try
        {
            var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
            new BytecodeInterpreter().Execute(fn);
            return (true, null);
        }
        catch (JsThrownException e)
        {
            return (false, e.Message);
        }
    }

    [Fact]
    public void Concat_SpreadableSourceWithMaxSafeLength_ThrowsTypeErrorEarly()
    {
        // Reproduces test262 arg-length-exceeding-integer-limit.js (subset:
        // spreadable plain object, no Proxy). End-to-end: rely on the test
        // catching the TypeError JS-side via assert.throws-like pattern.
        var fn = new BytecodeCompiler().CompileScript(new SourceText(@"
            var src = {};
            src.length = Number.MAX_SAFE_INTEGER;
            src[Symbol.isConcatSpreadable] = true;
            var threwTypeError = false;
            try { [1].concat(src); }
            catch (e) { threwTypeError = (e instanceof TypeError); }
            threwTypeError;
        "));
        Assert.True(new BytecodeInterpreter().Execute(fn).AsBoolean());
    }

    [Fact]
    public void Concat_TwoSpreadablesNearLimit_ThrowsTypeError()
    {
        // Sum exceeds 2^53 - 1 even if each individual length is fine.
        var fn = new BytecodeCompiler().CompileScript(new SourceText(@"
            var a = { length: 9007199254740990 };
            a[Symbol.isConcatSpreadable] = true;
            var b = { length: 5 };
            b[Symbol.isConcatSpreadable] = true;
            var threwTypeError = false;
            try { [].concat(a, b); }
            catch (e) { threwTypeError = (e instanceof TypeError); }
            threwTypeError;
        "));
        Assert.True(new BytecodeInterpreter().Execute(fn).AsBoolean());
    }

    [Fact]
    public void Concat_NormalArrays_StillWorks()
    {
        // Regression: ordinary concat must remain green.
        var fn = new BytecodeCompiler().CompileScript(
            new SourceText("var c = [1,2].concat([3,4]); c.length;"));
        var v = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(4.0, v.AsNumber());
    }

    [Fact]
    public void Concat_NonSpreadableObject_IsAppendedAsSingleElement()
    {
        // Regression: plain object without @@isConcatSpreadable goes in as
        // one element; we must not iterate its `length`.
        var fn = new BytecodeCompiler().CompileScript(
            new SourceText("var c = [1].concat({length: 5}); c.length;"));
        var v = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(2.0, v.AsNumber());
    }
}
