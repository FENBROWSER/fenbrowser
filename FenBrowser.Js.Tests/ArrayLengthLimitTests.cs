using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 ArrayCreate length limit (2^32-1): array methods that allocate a fresh
// result array sized by a source length must throw a spec RangeError for an
// over-limit length instead of crashing the host on a huge allocation.
public sealed class ArrayLengthLimitTests
{
    // Runs `call`, returning the constructor name of any thrown error (or "<no throw>").
    private static string CtorName(string call)
    {
        var src = "var n = '<no throw>'; try { " + call + " } catch (e) { n = e.constructor.name; } n;";
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Theory]
    [InlineData("Array.prototype.map.call({length: Math.pow(2,32)}, function(){});")]
    [InlineData("Array.prototype.splice.call({length: Math.pow(2,32)}, 0);")]
    [InlineData("Array.prototype.toReversed.call({length: Math.pow(2,32)});")]
    [InlineData("Array.prototype.toSorted.call({length: Math.pow(2,32)});")]
    [InlineData("Array.prototype.toSpliced.call({length: Math.pow(2,32)}, 0, 0);")]
    [InlineData("Array.prototype.with.call({length: Math.pow(2,32)}, 0, 1);")]
    public void OverLimitLengthThrowsRangeError(string call)
    {
        Assert.Equal("RangeError", CtorName(call));
    }

    [Fact]
    public void ToSplicedResultLengthOverLimitThrowsRangeError()
    {
        // Source length within the limit, but newLen = len - 0 + 1 exceeds 2^32-1.
        Assert.Equal("RangeError",
            CtorName("Array.prototype.toSpliced.call({length: Math.pow(2,32) - 1}, 0, 0, 1);"));
    }

    [Fact]
    public void ToSplicedResultLengthBeyondSafeIntegerThrowsTypeError()
    {
        Assert.Equal("TypeError",
            CtorName("Array.prototype.toSpliced.call({length: Math.pow(2,53) - 1}, 0, 0, 1);"));
    }

    [Fact]
    public void NormalArrayMethodsStillWork()
    {
        var fn = new BytecodeCompiler().CompileScript(
            new SourceText("[1,2,3].map(function(x){return x*2;}).join(',');"));
        new BytecodeVerifier().Verify(fn);
        Assert.Equal("2,4,6", new BytecodeInterpreter().Execute(fn).AsString());
    }
}
