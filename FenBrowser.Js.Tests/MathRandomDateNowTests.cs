using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class MathRandomDateNowTests
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

    [Fact]
    public void MathRandomInUnitInterval()
    {
        // Spec 21.3.2.27: 0 <= Math.random() < 1.
        for (var i = 0; i < 50; i++)
        {
            Assert.True(RunBool("var r = Math.random(); r >= 0 && r < 1;"));
        }
    }

    [Fact]
    public void MathRandomReturnsNumber()
    {
        // Multiple invocations should occasionally differ - not a deterministic
        // statement but an extraordinarily safe one for 100 samples on a non-seeded
        // PRNG. We assert "at least two different values seen", which fails only
        // with vanishing probability.
        var seen = new System.Collections.Generic.HashSet<double>();
        for (var i = 0; i < 100; i++)
        {
            seen.Add(RunNum("Math.random();"));
            if (seen.Count > 1) break;
        }

        Assert.True(seen.Count > 1);
    }

    [Fact]
    public void DateNowReturnsPositiveNumberCloseToCurrentEpoch()
    {
        var n = RunNum("Date.now();");
        // 2020-01-01T00:00:00Z = 1577836800000ms. The value should comfortably exceed it.
        Assert.True(n > 1577836800000d);
        // And not be implausibly far in the future (e.g. before year 2100).
        Assert.True(n < 4102444800000d);
    }

    [Fact]
    public void DateNowIsMonotonicWithinReason()
    {
        // Two back-to-back Date.now calls in the same script should be equal or
        // increasing.
        Assert.True(RunBool("Date.now() <= Date.now();"));
    }
}
