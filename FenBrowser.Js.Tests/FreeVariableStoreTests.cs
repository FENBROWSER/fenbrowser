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

    // A site is only safe while no scope between the function and the binding
    // can change shape. A sloppy direct eval in an enclosing function can add a
    // var there, and a `with` object can gain a property; reads and writes that
    // cached the outer binding must see the new one.
    [Fact]
    public void AVarAddedByEvalInAnEnclosingFunctionShadowsCachedSites()
    {
        Assert.Equal("global|outer|w|g49", Run(@"
            var x = 'global';
            function outer() {
                var read = function () { return x; };
                var write = function (v) { x = v; };
                var before = '';
                for (var i = 0; i < 50; i++) before = read();
                for (var j = 0; j < 50; j++) write('g' + j);
                eval('var x = ""outer""');
                var after = read();
                write('w');
                return before + '|' + after + '|' + x + '|' + globalThis.x;
            }
            outer();").AsString());
    }

    [Fact]
    public void APropertyAddedToAWithObjectShadowsCachedSites()
    {
        Assert.Equal("global|obj|w|g49", Run(@"
            var y = 'global';
            var o = {};
            var read, write;
            with (o) { read = function () { return y; }; write = function (v) { y = v; }; }
            var before = '';
            for (var i = 0; i < 50; i++) before = read();
            for (var j = 0; j < 50; j++) write('g' + j);
            o.y = 'obj';
            var after = read();
            write('w');
            before + '|' + after + '|' + o.y + '|' + globalThis.y;").AsString());
    }
    // `x++` and `x += e` resolve the binding before evaluating the operand
    // (ECMA-262 13.4, 13.15.2) and go through the same sites.
    [Fact]
    public void IncrementsOfClosureAndGlobalVariablesLandAfterSitesAreCached()
    {
        Assert.Equal("300,300,300", Run(@"
            var g = 0;
            function make() {
                var c = 0, d = 0;
                return function (n) { for (var i = 0; i < n; i++) { c++; d += 1; g++; } return c + ',' + d + ',' + g; };
            }
            var f = make(), r;
            for (var k = 0; k < 3; k++) r = f(100);
            r;").AsString());
    }

    [Fact]
    public void IncrementingAConstClosureVariableThrowsAfterReadsCachedIt()
    {
        Assert.Equal("TypeError", Run(@"
            function make() {
                const c = 1;
                return function (bump) { var t = 0; for (var i = 0; i < 50; i++) t += c; if (bump) c++; return t; };
            }
            var f = make(), name;
            for (var i = 0; i < 20; i++) f(false);
            try { f(true); } catch (e) { name = e.constructor.name; }
            name;").AsString());
    }

    [Fact]
    public void AnIncrementWhoseOperandDeletesTheGlobalWritesTheResolvedReference()
    {
        // Sloppy: the write re-creates the property it resolved to. Strict: the
        // resolved binding no longer exists, which is a ReferenceError.
        Assert.Equal("sloppy:6,strict:ReferenceError", Run(@"
            function sloppyInc() { counter++; }
            function strictInc() { 'use strict'; counter++; }
            counter = 0;
            for (var i = 0; i < 50; i++) { sloppyInc(); strictInc(); }
            var out = [];
            counter = { valueOf: function () { delete globalThis.counter; return 5; } };
            sloppyInc();
            out.push('sloppy:' + counter);
            counter = { valueOf: function () { delete globalThis.counter; return 5; } };
            try { strictInc(); out.push('strict:no error'); } catch (e) { out.push('strict:' + e.constructor.name); }
            out.join();").AsString());
    }
}
