using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 13.15.2: for `lhs op= rhs` the left-hand reference is evaluated
/// once. The object and key expressions must not run a second time when the
/// combined value is written back.
/// </summary>
public sealed class CompoundAssignmentTargetTests
{
    private static string Run(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Theory]
    [InlineData("+=", "o3")]
    [InlineData("-=", "o-1")]
    [InlineData("*=", "o2")]
    [InlineData("**=", "o1")]
    [InlineData("<<=", "o4")]
    public void MemberTargetObjectIsEvaluatedOnce(string op, string expected)
    {
        var result = Run($$"""
            var calls = ''; var obj = { n: 1 };
            function O() { calls += 'o'; return obj; }
            O().n {{op}} 2;
            calls + obj.n;
            """);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ComputedTargetObjectAndKeyAreEvaluatedOnceInOrder()
    {
        var result = Run("""
            var calls = ''; var obj = { n: 1 };
            function O() { calls += 'o'; return obj; }
            function K() { calls += 'k'; return 'n'; }
            O()[K()] += (calls += 'r', 2);
            calls + ':' + obj.n;
            """);
        Assert.Equal("okr:3", result);
    }

    [Fact]
    public void ConditionalInsideTargetRunsOnce()
    {
        // The shape reCAPTCHA's tile toggle uses: a ternary with side effects
        // inside the object expression of a compound assignment.
        var result = Run("""
            var rec = { selected: false }; var state = { count: 0 }; var log = '';
            function add() { log += '+'; } function rem() { log += '-'; }
            var e;
            (((e = !rec.selected) ? add() : rem(), rec).selected = e, state).count += e ? 1 : -1;
            log + rec.selected + state.count;
            """);
        Assert.Equal("+true1", result);
    }

    [Fact]
    public void CompoundAssignmentEvaluatesToTheStoredValue()
    {
        Assert.Equal("7", Run("var o = { v: 3 }; String(o.v += 4);"));
        Assert.Equal("ab", Run("var o = { s: 'a' }; var r = (o.s += 'b'); r;"));
    }

    [Fact]
    public void ParenthesizedMemberTargetStillWorks()
    {
        Assert.Equal("5", Run("var o = { v: 2 }; (o.v) += 3; String(o.v);"));
    }

    [Fact]
    public void GetterAndSetterEachRunOnce()
    {
        var result = Run("""
            var log = '';
            var o = { get v() { log += 'g'; return 10; }, set v(x) { log += 's' + x; } };
            o.v += 5;
            log;
            """);
        Assert.Equal("gs15", result);
    }

    [Fact]
    public void PrivateFieldCompoundAssignmentEvaluatesReceiverOnce()
    {
        var result = Run("""
            var calls = 0;
            class C {
                #n = 1;
                bump() { (calls++, this).#n += 2; return this.#n; }
            }
            new C().bump() + ':' + calls;
            """);
        Assert.Equal("3:1", result);
    }
}
