using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Assignments to variables a function does not declare go through the same
// per-slot site cache as reads (Interp2StoreFreeCached). Each case writes
// enough times for the site to be recorded, then changes what the site
// describes and checks the write still lands, or fails, where the
// specification says.
public sealed class FreeVariableStoreTests
{
    private static JsValueAssert Run(string source)
    {
        var interpreter = new BytecodeInterpreter();
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new JsValueAssert(interpreter.Execute(fn), interpreter);
    }

    private sealed record JsValueAssert(FenBrowser.Js.Runtime.JsValue Value, BytecodeInterpreter Interpreter)
    {
        public string AsString() => Value.AsString();
    }

    [Fact]
    public void AClosureWriteIsSeenByEveryReaderOfTheVariable()
    {
        Assert.Equal("3000,3000", Run(@"
            function make() {
                var c = 0;
                return [function () { c = c + 1; }, function () { return c; }];
            }
            var pair = make(), other = make();
            for (var i = 0; i < 3000; i++) pair[0]();
            [pair[1](), pair[1]()].join() + (other[1]() === 0 ? '' : ' leaked');").AsString());
    }

    [Fact]
    public void WritingAConstClosureVariableStillThrowsAfterReadsCachedIt()
    {
        Assert.Equal("TypeError", Run(@"
            function make() {
                const c = 1;
                return function (write) { var t = 0; for (var i = 0; i < 100; i++) t += c; if (write) c = 2; return t; };
            }
            var f = make();
            for (var i = 0; i < 50; i++) f(false);
            var name;
            try { f(true); } catch (e) { name = e.constructor.name; }
            name;").AsString());
    }

    [Fact]
    public void WritingALetClosureVariableInItsDeadZoneThrows()
    {
        Assert.Equal("ReferenceError", Run(@"
            var name;
            function outer() {
                function early() { x = 1; }
                try { early(); } catch (e) { name = e.constructor.name; }
                let x = 0;
                for (var i = 0; i < 100; i++) early();
                return x;
            }
            outer() === 1 ? name : 'wrong value';").AsString());
    }

    [Fact]
    public void AGlobalWriteFollowsTheGlobalWhenItIsDeletedAndRecreated()
    {
        Assert.Equal("ReferenceError,5", Run(@"
            implicitGlobal = 0;
            function strictWrite(v) { 'use strict'; implicitGlobal = v; }
            function sloppyWrite(v) { implicitGlobal = v; }
            for (var i = 0; i < 100; i++) strictWrite(i);
            delete globalThis.implicitGlobal;
            var seen = [];
            try { strictWrite(1); seen.push('no error'); } catch (e) { seen.push(e.constructor.name); }
            sloppyWrite(5);
            seen.push(globalThis.implicitGlobal);
            seen.join();").AsString());
    }

    [Fact]
    public void AGlobalWriteRunsASetterDefinedAfterTheSiteWasCached()
    {
        Assert.Equal("set:42", Run(@"
            var seen = '';
            globalThis.target = 0;
            function write(v) { target = v; }
            for (var i = 0; i < 100; i++) write(i);
            Object.defineProperty(globalThis, 'target', { set: function (v) { seen = 'set:' + v; }, configurable: true });
            write(42);
            seen;").AsString());
    }

    [Fact]
    public void AStrictGlobalWriteToAPropertyMadeReadOnlyThrows()
    {
        Assert.Equal("TypeError,99", Run(@"
            var fixedLater = 0;
            function write(v) { 'use strict'; fixedLater = v; }
            for (var i = 0; i < 100; i++) write(i);
            Object.defineProperty(globalThis, 'fixedLater', { writable: false });
            var name;
            try { write(7); } catch (e) { name = e.constructor.name; }
            name + ',' + fixedLater;").AsString());
    }

    [Fact]
    public void AGlobalLexicalDeclaredLaterShadowsTheCachedGlobalProperty()
    {
        var result = Run(@"
            shadowMe = 0;
            function write(v) { shadowMe = v; }
            for (var i = 0; i < 100; i++) write(i);
            0;");
        // A later script's top-level let shadows the global property for every
        // function, including one whose site already resolved the property.
        result.Interpreter.EvaluateScript("let shadowMe = 'lexical';", "second.js");
        result.Interpreter.EvaluateScript("write('through the let');", "third.js");
        Assert.Equal(
            "through the let,99",
            result.Interpreter.EvaluateScript("shadowMe + ',' + globalThis.shadowMe;", "check.js").AsString());
    }
}
