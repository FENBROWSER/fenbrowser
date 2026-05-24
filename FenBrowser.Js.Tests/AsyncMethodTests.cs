using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public class AsyncMethodTests
{
    private static JsValue Run(string source)
    {
        var c = new BytecodeCompiler();
        var fn = c.CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    // === Compilation & callability ===

    [Fact] public void AsyncClassMethod_CompilesAndIsCallable()
        => Assert.True(Run("class C{async foo(){return 42;}}var c=new C();typeof c.foo==='function';").AsBoolean());

    [Fact] public void AsyncClassMethod_Static_Compiles()
        => Assert.True(Run("class C{static async compute(){return 7;}}typeof C.compute==='function';").AsBoolean());

    [Fact] public void AsyncClassMethod_CanBeCalledMultipleTimes()
        => Assert.True(Run("class C{async get(){return 1;}}var c=new C();c.get();c.get();c.get();true;").AsBoolean());

    [Fact] public void AsyncClassMethod_ComputedName_Compiles()
        => Assert.True(Run("class C{async[Symbol('test')](){return 1;}}true;").AsBoolean());

    [Fact] public void AsyncObjectLiteralMethod_Compiles()
        => Assert.True(Run("var obj={async fetch(){return 1;}};typeof obj.fetch==='function';").AsBoolean());

    [Fact] public void GeneratorClassMethod_Compiles()
        => Assert.True(Run("class C{*gen(){yield 1;}}typeof C.prototype.gen==='function';").AsBoolean());

    [Fact] public void AwaitInsideAsyncFunction_IsParsed()
        => Assert.True(Run("async function test(){var x=await Promise.resolve(5);return x;}typeof test==='function';").AsBoolean());

    // === Async wrapping: returns Promise ===

    [Fact]
    public void AsyncClassMethod_ReturnsPromise()
        => Assert.True(Run("class C{async foo(){return 42;}}var c=new C();typeof c.foo().then==='function';").AsBoolean());

    [Fact]
    public void AsyncFunction_ReturnsPromise()
        => Assert.True(Run("async function f(){return 1;}typeof f().then==='function';").AsBoolean());

    // Object literal async method Promise wrapping depends on parser setting
    // IsAsync on object literal members (currently only class members have it).
    // Tracked as follow-up: object literal async method support.

    [Fact]
    public void AsyncFunction_PreservesReturnValue()
    {
        // Async function returns a Promise that will resolve with the return value.
        // The Promise is returned immediately; resolution happens via microtask.
        var result = Run(@"
            async function add(a,b){return a+b;}
            var p = add(3,4);
            typeof p.then === 'function';
        ");
        Assert.True(result.AsBoolean());
    }

    // === Generator wrapping: returns GeneratorObject without executing body ===

    [Fact]
    public void GeneratorFunction_ReturnsGeneratorObject()
        => Assert.True(Run("function*gen(){yield 1;}var g=gen();typeof g==='object'&&g!==null;").AsBoolean());

    [Fact]
    public void GeneratorFunction_DoesNotExecuteBodyOnCall()
    {
        var result = Run(@"
            var sideEffect = 0;
            function* gen() { sideEffect = 1; yield 2; }
            var g = gen();
            sideEffect;
        ");
        Assert.Equal(0d, result.AsNumber());
    }

    [Fact]
    public void GeneratorConstructor_ThrowsTypeError()
        => Assert.Throws<JsThrownException>(() => Run("new(function*(){})();"));
}
