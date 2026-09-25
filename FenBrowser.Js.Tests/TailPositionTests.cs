using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 15.10 Tail Position Calls. Only strict code has them, never a
// generator or async body, and a call inside a try block is not in tail
// position: the catch or finally still has to see what it does. A try's catch
// block, when there is no finally, is (15.10.2 HasCallInTailPosition).
public sealed class TailPositionTests
{
    private static BytecodeFunction Nested(string source) =>
        new BytecodeCompiler().CompileScript(new SourceText(source)).NestedFunctions[0];

    private static bool EmitsTailCall(string source) =>
        Nested(source).Instructions.Any(i => i.OpCode is OpCode.TailCall0 or OpCode.TailCall1 or OpCode.TailCallN);

    [Theory]
    [InlineData("'use strict'; function f() { return g(); }")]
    [InlineData("'use strict'; function f(x) { return g(x, x); }")]
    [InlineData("'use strict'; function f() { try { h(); } catch (e) { return g(); } }")]
    public void AStrictReturnOfACallIsATailCall(string source)
    {
        Assert.True(EmitsTailCall(source));
    }

    [Theory]
    [InlineData("function f() { return g(); }")]
    [InlineData("'use strict'; function f() { try { return g(); } catch (e) { return 0; } }")]
    [InlineData("'use strict'; function f() { try { return g(); } finally { h(); } }")]
    [InlineData("'use strict'; function f() { try { h(); } catch (e) { return g(); } finally { h(); } }")]
    [InlineData("'use strict'; function f() { try { try { h(); } catch (e) {} return g(); } catch (e) {} }")]
    [InlineData("'use strict'; function* f() { return g(); }")]
    [InlineData("'use strict'; async function f() { return g(); }")]
    [InlineData("'use strict'; async function* f() { return g(); }")]
    public void ACallOutOfTailPositionStaysAnOrdinaryCall(string source)
    {
        Assert.False(EmitsTailCall(source));
    }

    [Fact]
    public void ACatchSeesAnErrorFromAReturnedCallInItsTryBlock()
    {
        var script = new BytecodeCompiler().CompileScript(new SourceText(
            "'use strict'; function thrower() { throw new Error('boom'); }" +
            " function guarded() { try { return thrower(); } catch (e) { return 'caught ' + e.message; } } guarded();"));

        Assert.Equal("caught boom", new BytecodeInterpreter().Execute(script).AsString());
    }
}
