using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Guards that convert pure-C# infinite recursion (no JS frames, so MaxCallDepth
// and the dispatch-loop budgets never fire) into a catchable RangeError instead
// of a process-fatal StackOverflowException. Mirror V8's "Maximum call stack
// size exceeded" behavior for these hostile shapes.
public sealed class StackOverflowGuardTests
{
    // Returns the constructor name of any thrown error, else "<no throw>".
    private static string CtorName(string body)
    {
        var src = "var n = '<no throw>'; try { " + body + " } catch (e) { n = e.constructor.name; } n;";
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact]
    public void ProxyCycleSetupItselfIsLegal()
    {
        // React/ember-style code sometimes builds proxy cycles indirectly; the
        // setPrototypeOf call that creates the cycle must complete normally.
        // Only a property walk on the finished cycle is fatal (see below).
        Assert.True(RunBool(@"
            var o = {};
            var p = new Proxy(o, {});
            o === Object.setPrototypeOf(o, p);
        "));
    }

    [Fact]
    public void DeepProxyChainFailsCleanly()
    {
        // A trap-less proxy whose target sits in its own prototype chain makes the
        // [[Get]] walk bounce between TryGetPropertyValue and ProxyGet forever in
        // pure C#. The guard trips with a catchable RangeError, not a process kill.
        Assert.Equal("RangeError", CtorName(@"
            var o = {}; var p = new Proxy(o, {});
            Object.setPrototypeOf(o, p);
            o.x;
        "));
    }

    [Fact]
    public void MethodLookupInProxyCycleFailsCleanly()
    {
        // Same walk cycle reached through an inherited method lookup.
        Assert.Equal("RangeError", CtorName(@"
            var o = {}; var p = new Proxy(o, {});
            Object.setPrototypeOf(o, p);
            o.toString();
        "));
    }

    [Fact]
    public void ReverseProxyCycleFailsCleanly()
    {
        // Proxy whose target's prototype is the proxy itself (p -> o -> p).
        Assert.Equal("RangeError", CtorName(@"
            var o = {};
            var p = new Proxy(o, {});
            Object.setPrototypeOf(o, p);
            p.y;
        "));
    }

    [Fact]
    public void FlatCycleWithHugeDepthFailsCleanly()
    {
        // a[0] = a with a huge depth recurses until the FlattenInto guard trips.
        Assert.Equal("RangeError", CtorName(@"
            var a = []; a[0] = a; a.flat(1e9);
        "));
    }

    [Fact]
    public void FlatSmallDepthCycleStaysUsable()
    {
        // A cycle that bottoms out below the guard must still work: flat(1) on a
        // self-referencing array flattens one level and keeps the outer element.
        Assert.True(RunBool(@"
            var a = [1];
            a.push(a);
            var r = a.flat();
            r[0] === 1 && r[1] === 1 && r[2] === a;
        "));
    }

    [Fact]
    public void FlatMapCycleStaysUsable()
    {
        Assert.True(RunBool(@"
            var a = [1];
            a.push(a);
            var r = a.flatMap(function (x) { return x; });
            r[0] === 1 && r[1] === 1 && r[2] === a;
        "));
    }

    [Fact]
    public void TrampolineRecursionFailsCleanly()
    {
        // JS-frame recursion still trips the interpreter call-depth guard.
        Assert.Equal("RangeError", CtorName(@"
            function foo() { return bar(); }
            function bar() { return foo(); }
            foo();
        "));
    }
}
