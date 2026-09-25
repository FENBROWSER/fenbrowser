using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 9.1.1.2.5 SetMutableBinding on an object environment is
// Set(bindingObject, N, V, S): an identifier assignment that resolves to an
// accessor property of the global object, or of a `with` object, runs its
// setter. It used to overwrite the property's data slot instead.
public sealed class GlobalAccessorAssignmentTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void AssigningToAGlobalAccessorRunsItsSetter()
    {
        Assert.Equal("set:42,true", Run(@"
            var seen = '';
            Object.defineProperty(globalThis, 'onthing', {
                get: function () { return 'getter'; },
                set: function (v) { seen = 'set:' + v; },
                configurable: true
            });
            onthing = 42;
            seen + ',' + (onthing === 'getter');"));
    }

    [Fact]
    public void ASetterOnTheGlobalsPrototypeChainRuns()
    {
        Assert.Equal("proto:7", Run(@"
            var seen = '';
            var proto = Object.getPrototypeOf(globalThis);
            Object.defineProperty(proto, 'inheritedThing', { set: function (v) { seen = 'proto:' + v; }, configurable: true });
            inheritedThing = 7;
            delete proto.inheritedThing;
            seen;"));
    }

    [Fact]
    public void AssigningInsideWithRunsTheBindingObjectsSetter()
    {
        Assert.Equal("with:3,true", Run(@"
            var seen = '', o = {};
            Object.defineProperty(o, 'w', { set: function (v) { seen = 'with:' + v + ',' + (this === o); }, configurable: true });
            with (o) { w = 3; }
            seen;"));
    }

    [Fact]
    public void AStrictAssignmentToAGetterOnlyGlobalThrows()
    {
        Assert.Equal("TypeError", Run(@"
            'use strict';
            Object.defineProperty(globalThis, 'readOnlyThing', { get: function () { return 1; }, configurable: true });
            var name;
            try { readOnlyThing = 2; name = 'no error'; } catch (e) { name = e.constructor.name; }
            name;"));
    }
}
