using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayOfAndFromTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void OfWithMultipleArgsKeepsAllElements()
    {
        Assert.Equal(3, RunNum("Array.of(1,2,3).length;"));
        Assert.Equal(1, RunNum("Array.of(1,2,3)[0];"));
    }

    [Fact]
    public void OfWithSingleNumberKeepsItAsElement()
    {
        // The whole point of Array.of vs new Array(n): a single Number argument
        // becomes an element, not a length.
        Assert.Equal(1, RunNum("Array.of(7).length;"));
        Assert.Equal(7, RunNum("Array.of(7)[0];"));
    }

    [Fact]
    public void OfWithNoArgsReturnsEmpty()
    {
        Assert.Equal(0, RunNum("Array.of().length;"));
    }

    [Fact]
    public void FromArrayCopiesElements()
    {
        Assert.Equal(3, RunNum("Array.from([1,2,3]).length;"));
        Assert.Equal(2, RunNum("Array.from([1,2,3])[1];"));
    }

    [Fact]
    public void FromArrayWithMapFn()
    {
        Assert.Equal(6, RunNum("Array.from([1,2,3], function(v){return v*2;})[2];"));
    }

    [Fact]
    public void FromArrayLikeObject()
    {
        Assert.Equal(2, RunNum("Array.from({length:2, 0:'a', 1:'b'}).length;"));
    }

    [Fact]
    public void FromNullOrUndefinedThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("Array.from(null);"));
        Assert.Throws<JsThrownException>(() => RunNum("Array.from(undefined);"));
        Assert.Throws<JsThrownException>(() => RunNum("Array.from();"));
    }

    [Fact]
    public void FromWithNonFunctionMapThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("Array.from([1,2,3], 42);"));
    }

    [Fact]
    public void FromReturnsFreshArray()
    {
        Assert.Equal(1, RunNum("var a = [1,2]; Array.from(a) === a ? 0 : 1;"));
    }
}
