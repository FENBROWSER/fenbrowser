using System;
using System.Diagnostics;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Tier-5 #27 wall-clock timeout + Plan §14.2 instruction budget + the
// interrupt-callback escape hatch. The audit (docs/fenjs_gap_audit_2026-05-28.md
// §8) called out that enforcement worked but had no dedicated unit-test
// coverage proving the surfaced exception type, message shape, "zero =
// disabled" contract, and per-run-isolation of the deadline state.
public class ExecutionBudgetTests
{
    private static BytecodeFunction Compile(string src)
        => new BytecodeCompiler().CompileScript(new SourceText(src));

    // ---- InstructionBudget ----

    [Fact]
    public void InstructionBudget_Zero_ImposesNoLimit()
    {
        // Regression: default budget of 0 must allow long-running scripts.
        var interp = new BytecodeInterpreter();
        Assert.Equal(0, interp.InstructionBudget);
        var v = interp.Execute(Compile("var x=0; for(var i=0;i<5000;i++) x++; x;"));
        Assert.Equal(5000.0, v.AsNumber());
    }

    [Fact]
    public void InstructionBudget_Overrun_IsUncatchableFromScript()
    {
        // Security contract: the kill switch must NOT be catchable from JS
        // -- otherwise a malicious script could wrap an infinite loop in
        // try/catch and run forever. The JsThrownException leaks past any
        // JS-level try/catch all the way out of Execute().
        var fn = Compile(@"
            try { while (true) {} }
            catch (e) { /* must NEVER reach this */ }
            'reached-end';
        ");
        var interp = new BytecodeInterpreter { InstructionBudget = 1000 };
        Assert.Throws<JsThrownException>(() => interp.Execute(fn));
    }

    [Fact]
    public void InstructionBudget_State_IsResetPerExecute()
    {
        // Per-Execute reset: a second call with a fresh script under the
        // same interpreter instance must not inherit the prior run's
        // instruction count.
        var interp = new BytecodeInterpreter { InstructionBudget = 100000 };
        interp.Execute(Compile("var x=0; for(var i=0;i<500;i++) x++;"));
        var v = interp.Execute(Compile("var y=0; for(var i=0;i<500;i++) y++; y;"));
        Assert.Equal(500.0, v.AsNumber());
    }

    // ---- WallClockTimeoutMs (Tier-5 #27) ----

    [Fact]
    public void WallClockTimeout_Zero_ImposesNoLimit()
    {
        var interp = new BytecodeInterpreter();
        Assert.Equal(0L, interp.WallClockTimeoutMs);
        var v = interp.Execute(Compile("var x=0; for(var i=0;i<5000;i++) x++; x;"));
        Assert.Equal(5000.0, v.AsNumber());
    }

    [Fact]
    public void WallClockTimeout_StopsRunawayLoop()
    {
        // 50ms budget against an infinite loop. The check fires every
        // WallClockCheckInterval (1024) instructions, so a small overshoot
        // beyond 50ms is acceptable -- we just need it to terminate and
        // we cap the assertion well above the budget.
        var interp = new BytecodeInterpreter { WallClockTimeoutMs = 50 };
        var sw = Stopwatch.StartNew();
        Assert.Throws<JsThrownException>(() => interp.Execute(Compile("while(true){}")));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"wall-clock deadline didn't fire within 5s -- elapsed={sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void WallClockTimeout_Overrun_IsUncatchableFromScript()
    {
        var fn = Compile(@"
            try { while (true) {} } catch (e) {}
            'reached-end';
        ");
        var interp = new BytecodeInterpreter { WallClockTimeoutMs = 50 };
        Assert.Throws<JsThrownException>(() => interp.Execute(fn));
    }

    [Fact]
    public void WallClockTimeout_State_IsResetPerExecute()
    {
        // A fresh Execute after the deadline was set in a prior run must
        // recompute the deadline -- otherwise a still-active deadline
        // would carry over and kill an unrelated subsequent script.
        var interp = new BytecodeInterpreter { WallClockTimeoutMs = 50 };
        Assert.Throws<JsThrownException>(() => interp.Execute(Compile("while(true){}")));

        // Clear the deadline; the next run must complete normally.
        interp.WallClockTimeoutMs = 0;
        var v = interp.Execute(Compile("var x=0; for(var i=0;i<5000;i++) x++; x;"));
        Assert.Equal(5000.0, v.AsNumber());
    }

    [Fact]
    public void WallClockTimeout_StopsRunawayLoop_AfterJitTierUp()
    {
        var interp = new BytecodeInterpreter { WallClockTimeoutMs = 100 };
        var fn = Compile(@"
            function hot(skip) {
                if (skip) return 1;
                while (true) {}
            }
            for (var i = 0; i < 100; i++) hot(true);
            hot(false);
        ");

        var sw = Stopwatch.StartNew();
        Assert.Throws<JsThrownException>(() => interp.Execute(fn));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"JIT execution ignored the wall-clock deadline -- elapsed={sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void WallClockTimeout_StopsSelfReplenishingMicrotaskCheckpoint()
    {
        var interp = new BytecodeInterpreter { WallClockTimeoutMs = 100 };
        var fn = Compile(@"
            function again() { queueMicrotask(again); }
            queueMicrotask(again);
        ");

        var sw = Stopwatch.StartNew();
        Assert.Throws<JsThrownException>(() => interp.Execute(fn));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 5000,
            $"microtask checkpoint ignored the wall-clock deadline -- elapsed={sw.ElapsedMilliseconds}ms");
    }

    // ---- InterruptCallback ----

    [Fact]
    public void InterruptCallback_NotInvoked_AllowsCompletion()
    {
        var interp = new BytecodeInterpreter
        {
            InterruptCallback = () => true,
        };
        var v = interp.Execute(Compile("var x=0; for(var i=0;i<100;i++) x++; x;"));
        Assert.Equal(100.0, v.AsNumber());
    }

    [Fact]
    public void InterruptCallback_FalseReturn_IsUncatchableFromScript()
    {
        // Same security contract as the budget kill switch: a script-side
        // try/catch must NOT swallow the interrupt.
        var fn = Compile(@"
            try { while (true) {} } catch (e) {}
            'reached-end';
        ");
        var calls = 0;
        var interp = new BytecodeInterpreter
        {
            InterruptCallback = () => ++calls <= 50,
        };
        Assert.Throws<JsThrownException>(() => interp.Execute(fn));
    }

    // ---- Interaction ----

    [Fact]
    public void BudgetAndWallClock_BothActive_FirstToFireWins()
    {
        // Tight budget (50 instructions) vs generous wall-clock (10s).
        // Budget must fire first; the script never reaches the wall.
        var interp = new BytecodeInterpreter
        {
            InstructionBudget = 50,
            WallClockTimeoutMs = 10000,
        };
        var sw = Stopwatch.StartNew();
        Assert.Throws<JsThrownException>(() => interp.Execute(Compile("while(true){}")));
        sw.Stop();
        // Budget hits inside ~microseconds; wall-clock won't have fired.
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"budget should have fired first -- elapsed={sw.ElapsedMilliseconds}ms");
    }
}
