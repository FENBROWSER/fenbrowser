using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// The default interpreter must allow ordinary recursion (the old default of 20
// frames threw on fib(21)) and still turn a runaway recursion into a catchable
// RangeError, including when every level re-enters the interpreter through a
// native and so spends CLR stack rather than frame-stack slots.
public sealed class CallDepthTests
{
    private static string Run(string source, BytecodeInterpreter? interpreter = null)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        var result = (interpreter ?? new BytecodeInterpreter()).Execute(fn);
        return result.Tag == FenBrowser.Js.Runtime.JsValueTag.String ? result.AsString() : result.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void RecursionPastTheOldLimitRuns()
    {
        Assert.Equal("75025", Run("function fib(n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); } fib(25);"));
    }

    [Fact]
    public void ThousandsOfFramesDeepRuns()
    {
        Assert.Equal("5000", Run("function d(n) { return n === 0 ? 0 : 1 + d(n - 1); } d(5000);"));
    }

    [Fact]
    public void RunawayRecursionThrowsRangeError()
    {
        Assert.Equal("RangeError", Run("function r() { r(); } var n; try { r(); } catch (e) { n = e.constructor.name; } n;"));
    }

    [Fact]
    public void RunawayRecursionThroughANativeThrowsRangeError()
    {
        Assert.Equal("RangeError", Run("function r() { [1].map(r); } var n; try { r(); } catch (e) { n = e.constructor.name; } n;"));
    }

    [Fact]
    public void RunawayRecursionThroughAProxyTrapThrowsRangeError()
    {
        Assert.Equal("RangeError", Run(
            "function r() { return new Proxy({}, { get: r }).x; } var n; try { r(); } catch (e) { n = e.constructor.name; } n;"));
    }

    [Fact]
    public void RunawayGeneratorDelegationThrowsRangeError()
    {
        Assert.Equal("RangeError", Run(
            "function* g() { yield* g(); } var n; try { g().next(); } catch (e) { n = e.constructor.name; } n;"));
    }

    [Fact]
    public void GeneratorThatThrewIsCompleted()
    {
        Assert.Equal("true", Run(
            "function* g() { throw 1; } var it = g(); try { it.next(); } catch (e) {} String(it.next().done);"));
    }

    [Fact]
    public void CatchInsideANativeReentryStillRuns()
    {
        // A throw passes several re-entries with no handler before reaching the
        // try at the level that should catch it.
        Assert.Equal("caught3", Run(@"
            function r(n) {
                if (n === 0) throw new Error('bottom');
                if (n === 3) {
                    try { [1].map(function () { r(n - 1); }); } catch (e) { return 'caught' + n; }
                }
                return [1].map(function () { return r(n - 1); })[0];
            }
            r(10);"));
    }

    [Fact]
    public void FinallyBlocksRunWhileAnOverflowUnwinds()
    {
        Assert.Equal("true", Run(@"
            var entered = 0, left = 0;
            function r() { entered++; try { [1].forEach(r); } finally { left++; } }
            try { r(); } catch (e) {}
            String(entered > 10 && entered === left);"));
    }

    [Fact]
    public void InterpreterIsUsableAfterAnOverflow()
    {
        var interpreter = new BytecodeInterpreter();
        Run("function r() { [1].map(r); } try { r(); } catch (e) {} 0;", interpreter);
        Assert.Equal("75025", Run("function fib(n) { return n < 2 ? n : fib(n - 1) + fib(n - 2); } fib(25);", interpreter));
    }
}
