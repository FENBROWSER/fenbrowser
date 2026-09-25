using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 23.1.3.21 Array.prototype.map and 23.1.3.8 Array.prototype.filter:
// the species result is created before the first callback, each index is
// checked with HasProperty when it is reached, and map leaves holes as holes.
public sealed class ArrayMapFilterTests
{
    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact]
    public void MapKeepsHolesAsHoles()
    {
        Assert.True(RunBool("var a = [1, , 3].map(function (x) { return x * 2; }); a.length === 3 && !(1 in a) && a[0] === 2 && a[2] === 6;"));
    }

    [Fact]
    public void MapKeepsTrailingHolesInTheLength()
    {
        Assert.True(RunBool("var a = [1, 2, , ,].map(function (x) { return x; }); a.length === 4 && !(3 in a) && a[1] === 2;"));
    }

    [Fact]
    public void MapStopsVisitingIndicesTheCallbackRemoved()
    {
        Assert.True(RunBool(@"
            var arr = [1, 2, 3, 4], seen = [];
            var out = arr.map(function (x, i) { seen.push(x); if (i === 0) arr.length = 2; return x; });
            seen.join() === '1,2' && out.length === 4 && !(2 in out);"));
    }

    [Fact]
    public void MapCreatesTheSpeciesResultBeforeAnyCallback()
    {
        Assert.True(RunBool(@"
            var order = [], src = [1, 2];
            src.constructor = {};
            src.constructor[Symbol.species] = function (n) { order.push('species:' + n); return {}; };
            var out = src.map(function (x) { order.push('cb'); return x + 1; });
            order.join() === 'species:2,cb,cb' && out[0] === 2 && out[1] === 3;"));
    }

    [Fact]
    public void FilterCreatesTheSpeciesResultBeforeAnyCallback()
    {
        Assert.True(RunBool(@"
            var order = [], src = [1, 2, 3];
            src.constructor = {};
            src.constructor[Symbol.species] = function (n) { order.push('species:' + n); return {}; };
            var out = src.filter(function (x) { order.push('cb'); return x !== 2; });
            order.join() === 'species:0,cb,cb,cb' && out[0] === 1 && out[1] === 3 && !('2' in out);"));
    }

    [Fact]
    public void FilterSkipsHolesAndRemovedIndices()
    {
        Assert.True(RunBool(@"
            var arr = [1, , 2, 3, 4];
            var out = arr.filter(function (x) { if (x === 2) arr.length = 3; return true; });
            out.join() === '1,2';"));
    }

    [Fact]
    public void MapOverAnArrayLikeReadsInheritedIndices()
    {
        Assert.True(RunBool(@"
            var proto = { 1: 'b' };
            var o = Object.create(proto); o[0] = 'a'; o.length = 3;
            var out = Array.prototype.map.call(o, function (x) { return x + '!'; });
            out.length === 3 && out[0] === 'a!' && out[1] === 'b!' && !(2 in out);"));
    }

    [Fact]
    public void MapOverADenseArrayStaysDense()
    {
        Assert.True(RunBool("var out = [1, 2, 3].map(function (x) { return x * 10; }); out.join() === '10,20,30' && Array.isArray(out);"));
    }

    // Without a species constructor the result is a plain Array of this realm.
    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("({ length: 2, 0: 1, 1: 2 })")]
    [InlineData("(function () { var a = [1, 2]; a.constructor = { [Symbol.species]: null }; return a; })()")]
    [InlineData("(function () { var a = [1, 2]; a.constructor = undefined; return a; })()")]
    public void DefaultResultIsAnArray(string receiver)
    {
        Assert.True(RunBool($@"
            var o = {receiver};
            var m = Array.prototype.map.call(o, function (x) {{ return x; }});
            var f = Array.prototype.filter.call(o, function () {{ return true; }});
            Object.getPrototypeOf(m) === Array.prototype && m.hasOwnProperty(0) &&
            Object.getPrototypeOf(f) === Array.prototype && f.length === 2;"));
    }
}
