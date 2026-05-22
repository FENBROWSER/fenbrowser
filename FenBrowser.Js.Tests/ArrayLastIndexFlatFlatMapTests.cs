using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayLastIndexFlatFlatMapTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Theory]
    [InlineData("[1,2,3,2,1].lastIndexOf(2);", 3.0)]
    [InlineData("[1,2,3].lastIndexOf(99);", -1.0)]
    [InlineData("[1,2,3,2,1].lastIndexOf(2, 2);", 1.0)]
    [InlineData("[1,2,3,2,1].lastIndexOf(2, -2);", 3.0)]
    [InlineData("[].lastIndexOf(1);", -1.0)]
    [InlineData("[NaN].lastIndexOf(NaN);", -1.0)]   // strict-eq
    public void LastIndexOf(string source, double expected) => Assert.Equal(expected, RunNum(source));

    [Fact]
    public void FlatDefaultDepthOne()
    {
        // depth=1: outer array of [1, [2, [3,[4]]]] expands the second element one
        // level, producing [1, 2, [3, [4]]] -> length 3.
        Assert.Equal(3, RunNum("[1,[2,[3,[4]]]].flat().length;"));
        Assert.Equal(1, RunNum("[1,[2,[3,[4]]]].flat()[0];"));
        Assert.Equal(2, RunNum("[1,[2,[3,[4]]]].flat()[1];"));
    }

    [Fact]
    public void FlatExplicitDepth()
    {
        // depth=2: [1, [2, [3, [4]]]] -> [1, 2, [3, [4]]] -> [1, 2, 3, [4]] -> length 4.
        Assert.Equal(4, RunNum("[1,[2,[3,[4]]]].flat(2).length;"));
    }

    [Fact]
    public void FlatInfiniteByLargeDepth()
    {
        Assert.Equal(4, RunNum("[1,[2,[3,[4]]]].flat(99).length;"));
        Assert.Equal(4, RunNum("[1,[2,[3,[4]]]].flat(99)[3];"));
    }

    [Fact]
    public void FlatZeroDepthCopiesShallow()
    {
        Assert.Equal(2, RunNum("[1,[2,3]].flat(0).length;"));
    }

    [Fact]
    public void FlatMapMapsThenFlattensOneLevel()
    {
        Assert.Equal(6, RunNum("[1,2,3].flatMap(function(v){return [v, v*2];}).length;"));
        Assert.Equal(1, RunNum("[1,2,3].flatMap(function(v){return [v, v*2];})[0];"));
        Assert.Equal(2, RunNum("[1,2,3].flatMap(function(v){return [v, v*2];})[1];"));
    }

    [Fact]
    public void FlatMapNonArrayReturnsKept()
    {
        Assert.Equal(3, RunNum("[1,2,3].flatMap(function(v){return v*10;}).length;"));
        Assert.Equal(20, RunNum("[1,2,3].flatMap(function(v){return v*10;})[1];"));
    }

    [Fact]
    public void FlatMapOnlyFlatensOneLevel()
    {
        // Callback returns [[10,20]] - a 1-element array whose single element is itself
        // an Array. flatMap flattens one level, producing the nested array as a
        // single retained element. Length 1.
        Assert.Equal(1, RunNum("[1].flatMap(function(){return [[10,20]];}).length;"));
    }
}
