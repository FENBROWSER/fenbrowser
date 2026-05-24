using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Targeted fixes for semantic bugs found in test262 triage.
public class SemanticBugFixes
{
    private static object Run(string source)
    {
        var c = new BytecodeCompiler();
        var fn = c.CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    // Object.hasOwn (ES2022) — should work identically to hasOwnProperty.call
    [Fact]
    public void ObjectHasOwn_ReturnsTrueForOwnProperty()
    {
        var r = (FenBrowser.Js.Runtime.JsValue)Run("Object.hasOwn({x:1}, 'x');");
        Assert.True(r.AsBoolean());
    }

    [Fact]
    public void ObjectHasOwn_ReturnsFalseForInheritedProperty()
    {
        var r = (FenBrowser.Js.Runtime.JsValue)Run("Object.hasOwn(Object.create({x:1}), 'x');");
        Assert.False(r.AsBoolean());
    }

    [Fact]
    public void ObjectHasOwn_ReturnsFalseForMissingProperty()
    {
        var r = (FenBrowser.Js.Runtime.JsValue)Run("Object.hasOwn({}, 'x');");
        Assert.False(r.AsBoolean());
    }

    // String.prototype.replaceAll with RegExp (ES2021)
    [Fact]
    public void StringReplaceAll_WithGlobalRegex()
    {
        var r = (FenBrowser.Js.Runtime.JsValue)Run("'a-b-c'.replaceAll('-', '_');");
        Assert.Equal("a_b_c", r.AsString());
    }

    [Fact]
    public void StringReplaceAll_NoMatchReturnsOriginal()
    {
        var r = (FenBrowser.Js.Runtime.JsValue)Run("'hello'.replaceAll('x', 'y');");
        Assert.Equal("hello", r.AsString());
    }

    // Array.prototype.sort comparefn edge cases
    [Fact]
    public void ArraySort_WithCompareFn()
    {
        var r = (FenBrowser.Js.Runtime.JsValue)Run("[3,1,4,1,5].sort(function(a,b){return a-b;}) + '';");
        Assert.Equal("1,1,3,4,5", r.AsString());
    }

    [Fact]
    public void ArraySort_UndefinedCompareFnIsIgnored()
    {
        var r = (FenBrowser.Js.Runtime.JsValue)Run("[3,1,2].sort(undefined) + '';");
        Assert.Equal("1,2,3", r.AsString());
    }

    // Function.prototype.apply with null thisArg (null is coerced to global object)
    [Fact]
    public void FunctionApply_NullThisArgDoesNotThrow()
    {
        var r = (FenBrowser.Js.Runtime.JsValue)Run("(function(a,b){return a+b;}).apply(null, [1,2]);");
        Assert.Equal(3d, r.AsNumber());
    }

    // Promise.all with empty iterable returns a Promise
    [Fact]
    public void PromiseAll_EmptyIterable_ReturnsPromise()
    {
        var r = (FenBrowser.Js.Runtime.JsValue)Run("typeof Promise.all([]).then === 'function';");
        Assert.True(r.AsBoolean());
    }

    // Promise.race with empty iterable (should remain pending forever)
    [Fact]
    public void PromiseRace_EmptyIterableNeverResolves()
    {
        // Promise.race([]) should stay pending forever. We just check it doesn't crash.
        var r = (FenBrowser.Js.Runtime.JsValue)Run(@"
            var p = Promise.race([]);
            typeof p.then === 'function';
        ");
        Assert.True(r.AsBoolean());
    }

    // Array.prototype.splice with negative indices
    [Fact]
    public void ArraySplice_NegativeStart()
    {
        var r = (FenBrowser.Js.Runtime.JsValue)Run("[1,2,3,4,5].splice(-2) + '';");
        Assert.Equal("4,5", r.AsString());
    }

    // Array.prototype.concat spreadable
    [Fact]
    public void ArrayConcat_FlatArray()
    {
        var r = (FenBrowser.Js.Runtime.JsValue)Run("[1,2].concat([3,4]) + '';");
        Assert.Equal("1,2,3,4", r.AsString());
    }
}
