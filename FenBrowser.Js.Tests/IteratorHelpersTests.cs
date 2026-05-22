using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class IteratorHelpersTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void IteratorFromArray()
    {
        Assert.Equal(3, RunNum("Iterator.from([1,2,3]).toArray().length;"));
    }

    [Fact]
    public void IteratorFromArrayIterator()
    {
        Assert.Equal(2, RunNum("Iterator.from([1,2,3].values()).next().value === 1 ? 2 : 0;"));
    }

    [Fact]
    public void IteratorFromUserIterable()
    {
        Assert.Equal(6, RunNum(@"
            var obj = {};
            obj[Symbol.iterator] = function(){
                var i = 0;
                return { next: function(){ i++; return { value: i, done: i > 3 }; }};
            };
            var s = 0;
            Iterator.from(obj).forEach(function(v){ s = s + v; });
            s;
        "));
    }

    [Fact]
    public void MapTransformsValues()
    {
        Assert.Equal(20, RunNum("Iterator.from([1,2,3]).map(function(v){return v*10;}).toArray()[1];"));
    }

    [Fact]
    public void FilterDropsRejected()
    {
        Assert.Equal(2, RunNum("Iterator.from([1,2,3,4]).filter(function(v){return v % 2 === 0;}).toArray().length;"));
    }

    [Fact]
    public void TakeLimitsCount()
    {
        Assert.Equal(2, RunNum("Iterator.from([1,2,3,4]).take(2).toArray().length;"));
    }

    [Fact]
    public void DropSkipsLeading()
    {
        Assert.Equal(2, RunNum("Iterator.from([1,2,3,4]).drop(2).toArray().length;"));
        Assert.Equal(3, RunNum("Iterator.from([1,2,3,4]).drop(2).toArray()[0];"));
    }

    [Fact]
    public void TakeNegativeThrows() => Assert.Throws<JsThrownException>(() => RunNum("Iterator.from([1]).take(-1);"));

    [Fact]
    public void EveryAndSomeShortCircuit()
    {
        Assert.True(RunBool("Iterator.from([1,2,3]).every(function(v){return v > 0;});"));
        Assert.False(RunBool("Iterator.from([1,-1,2]).every(function(v){return v > 0;});"));
        Assert.True(RunBool("Iterator.from([1,2,3]).some(function(v){return v === 2;});"));
        Assert.False(RunBool("Iterator.from([1,2,3]).some(function(v){return v === 99;});"));
    }

    [Fact]
    public void FindReturnsFirstMatch()
    {
        Assert.Equal(3, RunNum("Iterator.from([1,2,3,4]).find(function(v){return v > 2;});"));
    }

    [Fact]
    public void ReduceAccumulates()
    {
        Assert.Equal(10, RunNum("Iterator.from([1,2,3,4]).reduce(function(a,v){return a+v;}, 0);"));
        Assert.Equal(10, RunNum("Iterator.from([1,2,3,4]).reduce(function(a,v){return a+v;});"));
    }

    [Fact]
    public void ReduceEmptyWithoutInitialThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("Iterator.from([]).reduce(function(a,v){return v;});"));
    }

    [Fact]
    public void FlatMapFlattensOneLevel()
    {
        Assert.Equal(6, RunNum("Iterator.from([1,2,3]).flatMap(function(v){return [v, v*10];}).toArray().length;"));
    }

    [Fact]
    public void ToArrayMaterialises()
    {
        Assert.Equal(3, RunNum("Iterator.from([1,2,3]).toArray()[2];"));
    }

    [Fact]
    public void ChainedHelpersWork()
    {
        // [1..6] -> filter evens -> map *10 -> sum
        Assert.Equal(120, RunNum(@"
            Iterator.from([1,2,3,4,5,6])
                .filter(function(v){return v % 2 === 0;})
                .map(function(v){return v * 10;})
                .reduce(function(a,v){return a+v;}, 0);
        "));
    }

    [Fact]
    public void ArrayValuesHelpersChain()
    {
        // arr.values() now inherits Iterator.prototype helpers.
        Assert.Equal(20, RunNum("[1,2,3].values().map(function(v){return v*10;}).toArray()[1];"));
    }

    [Fact]
    public void IteratorIsAbstract()
    {
        Assert.Throws<JsThrownException>(() => RunNum("Iterator();"));
        Assert.Throws<JsThrownException>(() => RunNum("new Iterator();"));
    }

    [Fact]
    public void IteratorFromStringYieldsCodeUnits()
    {
        Assert.Equal(3, RunNum("Iterator.from('abc').toArray().length;"));
    }
}
