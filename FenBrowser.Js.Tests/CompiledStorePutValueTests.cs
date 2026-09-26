using System.Reflection;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 6.2.5.6 PutValue from baseline-compiled code: a [[Set]] that
// returns false throws a TypeError in strict code, and a null or undefined base
// is a TypeError about setting. The compiled store's slow paths used to drop
// the [[Set]] result, so a strict write to a read-only property in a function
// hot enough to compile was silently lost. Each case compiles `write`, whose
// loop is what makes a call run it compiled, and calls it.
public sealed class CompiledStorePutValueTests
{
    private static string RunWithCompiledWrite(string source)
    {
        var script = new BytecodeCompiler().CompileScript(new SourceText(source));
        var write = script.NestedFunctions.Single(f => f.Name == "write");
        var compiled = Assert.IsType<JitCompiler.JitDelegate>(JitCompiler.TryCompile(write));
        typeof(BytecodeFunction)
            .GetField("JitDelegate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(write, compiled);
        // A call runs a compiled body only when the body loops enough to be
        // worth it (JitCompiler.PrefersCompiled); say that it has.
        write.BackEdges = 1 << 20;
        Assert.True(JitCompiler.PrefersCompiled(write), "the call would not run the compiled body");
        return new BytecodeInterpreter().Execute(script).AsString();
    }

    private static string Attempt(string strictness, string setup, string target) => RunWithCompiledWrite($@"
        {strictness}
        function write(o, v) {{ for (var i = 0; i < 1; i++) {{ }} o.p = v; }}
        {setup}
        var r = 'no-throw';
        try {{ write({target}, 1); write({target}, 2); }} catch (e) {{ r = e.constructor.name + ': ' + e.message; }}
        r;");

    [Theory]
    [InlineData("var t = {}; Object.defineProperty(t, 'p', { value: 0, writable: false });")]
    [InlineData("var t = { get p() { return 0; } };")]
    [InlineData("var t = Object.preventExtensions({});")]
    [InlineData("var t = Object.freeze({ p: 0 });")]
    [InlineData("var t = Object.create(Object.defineProperty({}, 'p', { value: 0, writable: false }));")]
    public void AStrictWriteThatSetRefusesThrows(string setup)
    {
        Assert.Equal("TypeError: Cannot assign to read-only property 'p'.", Attempt("'use strict';", setup, "t"));
    }

    [Fact]
    public void AStrictWriteThroughAProxyWhoseTrapRefusesThrows()
    {
        Assert.Equal(
            "TypeError: Cannot assign to read-only property 'p'.",
            Attempt("'use strict';", "var t = new Proxy({}, { set() { return false; } });", "t"));
    }

    [Theory]
    [InlineData("var t = {}; Object.defineProperty(t, 'p', { value: 0, writable: false });")]
    [InlineData("var t = Object.freeze({ p: 0 });")]
    [InlineData("var t = new Proxy({}, { set() { return false; } });")]
    public void ASloppyWriteThatSetRefusesIsDropped(string setup)
    {
        Assert.Equal("no-throw", Attempt("", setup, "t"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("undefined")]
    public void ANullishBaseIsATypeErrorAboutSetting(string target)
    {
        Assert.Equal(
            $"TypeError: Cannot set properties of {target} (setting 'p').",
            Attempt("", "", target));
    }
}
