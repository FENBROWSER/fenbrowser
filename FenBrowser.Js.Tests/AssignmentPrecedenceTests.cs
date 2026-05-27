using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 13.15 — AssignmentExpression : LeftHandSideExpression
// AssignmentOperator AssignmentExpression. The RHS of `=` is itself an
// AssignmentExpression (right-associative, includes every operator
// tighter than the comma operator). These tests pin both the
// right-associativity and the precedence interaction with logical /
// bitwise binary operators that sit between assignment and the unary
// chain in the Pratt-style binding table.
public class AssignmentPrecedenceTests
{
    private static double Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact]
    public void ChainedAssign_TwoTargets_AssignsRightToLeft()
    {
        // a = b = 5  ⇒ b becomes 5, a becomes 5 (right-assoc).
        Assert.Equal(5.0, Run("var a, b; a = b = 5; a;"));
        Assert.Equal(5.0, Run("var a, b; a = b = 5; b;"));
    }

    [Fact]
    public void ChainedAssign_ComputedMember_TypedArrayConcatPattern()
    {
        // Exact shape from test262 Array.prototype.concat_small-typed-array.js:
        //   ta_by_len[i] = items[i] = elems % modulo;
        Assert.Equal(7.0, Run(
            "var items = [], ta = []; var i = 0; ta[i] = items[i] = 7; ta[0];"));
        Assert.Equal(7.0, Run(
            "var items = [], ta = []; var i = 0; ta[i] = items[i] = 7; items[0];"));
    }

    [Fact]
    public void ChainedAssign_ThreeTargets_AllReceiveSameValue()
    {
        Assert.Equal(9.0, Run("var a, b, c; a = b = c = 9; a;"));
        Assert.Equal(9.0, Run("var a, b, c; a = b = c = 9; b;"));
        Assert.Equal(9.0, Run("var a, b, c; a = b = c = 9; c;"));
    }

    [Fact]
    public void AssignWithLogicalOrRhs_ParsesAsRightSideExpression()
    {
        // a = false || 7  must be  a = (false || 7)  ⇒ a = 7.
        // Pre-fix this parsed as (a = false) || 7 leaving a = false.
        Assert.Equal(7.0, Run("var a = 0; a = false || 7; a;"));
    }

    [Fact]
    public void AssignWithNullishCoalescingRhs()
    {
        Assert.Equal(7.0, Run("var a; a = null ?? 7; a;"));
    }

    [Fact]
    public void AssignWithLogicalAndRhs()
    {
        Assert.Equal(7.0, Run("var a; a = 1 && 7; a;"));
    }

    [Fact]
    public void AssignWithBitwiseOrRhs()
    {
        Assert.Equal(7.0, Run("var a; a = 4 | 3; a;"));
    }

    [Fact]
    public void AssignWithBitwiseXorRhs()
    {
        Assert.Equal(6.0, Run("var a; a = 5 ^ 3; a;"));
    }

    [Fact]
    public void AssignWithBitwiseAndRhs()
    {
        // & has leftBp=10 — already worked, but pin it.
        Assert.Equal(1.0, Run("var a; a = 5 & 3; a;"));
    }

    [Fact]
    public void AssignWithArithmeticRhs_Unchanged()
    {
        Assert.Equal(11.0, Run("var a; a = 4 + 7; a;"));
        Assert.Equal(20.0, Run("var a; a = 4 * 5; a;"));
    }

    [Fact]
    public void AssignWithEqualityRhs_Unchanged()
    {
        Assert.True(RunBool("var a; a = (1 == 1); a;"));
    }

    [Fact]
    public void CompoundAssignChained_RightAssociative()
    {
        // a = b += 2  ⇒  b = b + 2, then a = b.
        Assert.Equal(7.0, Run("var a, b = 5; a = b += 2; a;"));
        Assert.Equal(7.0, Run("var a, b = 5; a = b += 2; b;"));
    }

    [Fact]
    public void AssignWithCommaOnRhs_RequiresParens()
    {
        // a = (b, c)  ⇒ a = c.  Bare comma binds looser than `=`, so
        // `a = 1, 2;` is `(a = 1), 2` — a stays 1. Pin both shapes.
        Assert.Equal(2.0, Run("var a; a = (1, 2); a;"));
        Assert.Equal(1.0, Run("var a; a = 1, 2; a;"));
    }
}
