using FenBrowser.Js.AstValidation;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DestructuringRuntimeTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void ArrayDestructuringDeclaration_BindsValuesDefaultsAndRest()
    {
        var value = Run("const [a, b = 5, ...rest] = [1, undefined, 3, 4]; a + b + rest[0] + rest[1];");
        Assert.Equal(13d, value.AsNumber());
    }

    [Fact]
    public void ArrayDestructuringAssignment_StoresIntoExistingBindings()
    {
        // ECMA-262 13.15.5: array-literal LHS of `=` is the assignment-pattern cover
        // grammar — stores into existing bindings, with defaults and rest.
        var value = Run("var a, b, rest; [a, b = 5, ...rest] = [1, undefined, 3, 4]; a + b + rest[0] + rest[1];");
        Assert.Equal(13d, value.AsNumber());
    }

    [Fact]
    public void ObjectDestructuringAssignment_StoresIntoExistingBindings()
    {
        var value = Run("var x, z, rest; ({ x, y: z = 7, ...rest } = { x: 2, y: undefined, k: 9 }); x + z + rest.k;");
        Assert.Equal(18d, value.AsNumber());
    }

    [Fact]
    public void ObjectAssignment_ShorthandDefaultVsAliasedTarget()
    {
        // `{a = 5}` is a CoverInitializedName (target a, default 5); `{a: x}` aliases
        // property a onto target x; `{a: x = 8}` aliases with a default. These are
        // distinguished from `{a: <member>}` (which is not a binding-pattern element).
        Assert.Equal(5d, Run("var a; ({ a = 5 } = {}); a;").AsNumber());
        Assert.Equal(7d, Run("var x; ({ a: x } = { a: 7 }); x;").AsNumber());
        Assert.Equal(8d, Run("var x; ({ a: x = 8 } = {}); x;").AsNumber());
    }

    [Fact]
    public void ObjectAssignment_MemberTargetEvaluatesAndThrowsBrandCheck()
    {
        // `{a: this.#field}` is a member assignment target, NOT a shorthand default; on
        // a non-instance receiver the private write must throw a TypeError (it must not
        // be silently mis-read as `{a = this.#field}`).
        var result = Run(@"
            class C { #field; m() { ({ a: this.#field } = { a: 0 }); } }
            var ok = false;
            try { C.prototype.m.call({}); } catch (e) { ok = e instanceof TypeError; }
            ok;
        ");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void DestructuringAssignment_SwapsAndEvaluatesToRhs()
    {
        // The assignment expression evaluates to the right-hand-side value.
        var value = Run("var a = 1, b = 2; var v = ([a, b] = [b, a]); a + ',' + b + ',' + v.length;");
        Assert.Equal("2,1,2", value.AsString());
    }

    [Fact]
    public void ObjectDestructuringDeclaration_BindsAliasesDefaultsAndRest()
    {
        var value = Run("const { x, y: z = 7, ...rest } = { x: 2, y: undefined, k: 9 }; x + z + rest.k;");
        Assert.Equal(18d, value.AsNumber());
    }

    [Fact]
    public void NestedDestructuringDeclaration_BindsNestedTargets()
    {
        var value = Run("const { a: { b }, c: [d] } = { a: { b: 3 }, c: [4] }; b + d;");
        Assert.Equal(7d, value.AsNumber());
    }

    [Fact]
    public void ForOfWithArrayBindingPattern_BindsPerIteration()
    {
        var value = Run("var sum = 0; for (var [a, b] of [[1,2],[3,4]]) { sum += a + b; } sum;");
        Assert.Equal(10d, value.AsNumber());
    }

    [Fact]
    public void FunctionParameterObjectPattern_BindsProperties()
    {
        var value = Run("function pick({x}) { return x; } pick({x: 42});");
        Assert.Equal(42d, value.AsNumber());
    }

    [Fact]
    public void FunctionParameterObjectPattern_NullThrowsTypeError()
    {
        var value = Run("var ok = false; function fn({}) {} try { fn(null); } catch (e) { ok = e instanceof TypeError; } ok;");
        Assert.True(value.AsBoolean());
    }
}
