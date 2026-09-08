using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Compiled code keeps registers in CLR locals and writes them back to the frame
// only where something else looks: before a call, and wherever a frame enters or
// re-enters the body. These pin the places where the two views have to agree.
public class RegisterPromotionTests
{
    private const int TierUp = 2000;

    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void AValueLiveAcrossAnOperatorsGeneralPath_IsReadBack()
    {
        // The fast path leaves its result in a local and the general path leaves
        // it in the frame. Taking the general path after the fast path was
        // emitted must still read the frame back.
        var v = Run($@"
            function label(n) {{ return 'n=' + n + '!'; }}
            var last = '';
            for (var i = 0; i < {TierUp}; i++) last = label(i);
            last;
        ");
        Assert.Equal("n=" + (TierUp - 1) + "!", v);
    }

    [Fact]
    public void ConstantsSurviveSeveralLoopsInOneFunction()
    {
        var v = Run($@"
            function one() {{ return 1; }}
            function two() {{ return 2; }}
            var a = 0;
            var b = 0;
            for (var i = 0; i < {TierUp}; i++) a += one();
            for (var j = 0; j < {TierUp}; j++) b += two();
            'a=' + a + ' b=' + b;
        ");
        Assert.Equal($"a={TierUp} b={TierUp * 2}", v);
    }

    [Fact]
    public void AValueComputedBeforeALoop_SurvivesEntryIntoIt()
    {
        // The loop is long enough that a running frame hands over at its header,
        // and `kept` is live across that point.
        var v = Run(@"
            function once() {
                var kept = { tag: 'held' };
                var total = 0;
                for (var i = 0; i < 200000; i++) { total += i & 3; }
                return kept.tag + ':' + total;
            }
            once();
        ");

        var expected = 0;
        for (var i = 0; i < 200000; i++) expected += i & 3;
        Assert.Equal("held:" + expected, v);
    }

    [Fact]
    public void AValueLiveAcrossARoutedThrow_SurvivesTheHandler()
    {
        var v = Run($@"
            function guarded(n) {{
                var before = 'kept' + n;
                var seen = '';
                try {{
                    var o = null;
                    seen = o.missing;
                }} catch (e) {{
                    seen = 'caught';
                }}
                return before + '/' + seen;
            }}
            var last = '';
            for (var i = 0; i < {TierUp}; i++) last = guarded(i);
            last;
        ");
        Assert.Equal("kept" + (TierUp - 1) + "/caught", v);
    }

    [Fact]
    public void ManyValuesLiveAcrossACall_AllComeBack()
    {
        var v = Run($@"
            function noise() {{ return 0; }}
            function wide(n) {{
                var a = n + 1, b = n + 2, c = n + 3, d = n + 4;
                var e = n + 5, f = n + 6, g = n + 7, h = n + 8;
                noise();
                return a + b + c + d + e + f + g + h;
            }}
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = wide(i);
            String(last);
        ");
        Assert.Equal((8 * (TierUp - 1) + 36).ToString(), v);
    }

    [Fact]
    public void ARegisterAValueWasReadIntoIsNotClobberedByTheCallThatFollows()
    {
        // The call writes its own destination in the frame; everything else the
        // caller is holding has to survive it untouched.
        var v = Run($@"
            function tag(n) {{ return n * 2; }}
            function chain(n) {{
                var first = n;
                var second = tag(n);
                var third = tag(second);
                return first + ',' + second + ',' + third;
            }}
            var last = '';
            for (var i = 0; i < {TierUp}; i++) last = chain(i);
            last;
        ");
        var n = TierUp - 1;
        Assert.Equal($"{n},{n * 2},{n * 4}", v);
    }

    [Fact]
    public void AnObjectHeldOnlyInARegisterAcrossAllocation_IsStillTraced()
    {
        var v = Run($@"
            function make(n) {{ return {{ n: n }}; }}
            function hold(n) {{
                var kept = make(n);
                var churn = null;
                for (var i = 0; i < 40; i++) churn = make(i);
                return kept.n + churn.n;
            }}
            var last = 0;
            for (var i = 0; i < {TierUp}; i++) last = hold(i);
            String(last);
        ");
        Assert.Equal((TierUp - 1 + 39).ToString(), v);
    }

    [Fact]
    public void AValueLiveAcrossAScopeChange_Survives()
    {
        // Entering and leaving a block replaces the frame's environment, which
        // the compiled body re-derives; the registers it is holding are separate
        // and must be unaffected.
        var v = Run($@"
            function scoped(n) {{
                var outer = 'o' + n;
                {{
                    let inner = 'i' + n;
                    outer = outer + '/' + inner;
                }}
                return outer;
            }}
            var last = '';
            for (var i = 0; i < {TierUp}; i++) last = scoped(i);
            last;
        ");
        var n = TierUp - 1;
        Assert.Equal($"o{n}/i{n}", v);
    }
}
