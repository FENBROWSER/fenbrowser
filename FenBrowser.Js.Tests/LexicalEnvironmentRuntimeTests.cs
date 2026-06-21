using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class LexicalEnvironmentRuntimeTests
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

    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void LetWithoutInitializerInitializesAtDeclaration()
    {
        Assert.True(RunBool("let x; x === undefined;"));
    }

    [Fact]
    public void LetInitializerStoresIntoEnvironmentBinding()
    {
        Assert.Equal(7, RunNum("let x = 7; x;"));
    }

    [Fact]
    public void ConstInitializerStoresIntoImmutableBinding()
    {
        Assert.Equal(11, RunNum("const c = 11; c;"));
    }

    [Fact]
    public void LexicalDeclarationIsNotGlobalObjectProperty()
    {
        Assert.True(RunBool("let hidden = 3; hidden === 3 && globalThis.hidden === undefined;"));
    }

    [Fact]
    public void ReadBeforeLetInitializationThrowsReferenceError()
    {
        Assert.Equal(
            "ReferenceError",
            RunStr("let observed; try { x; let x = 1; } catch (e) { observed = e.name; } observed;"));
    }

    [Fact]
    public void TypeofBeforeLetInitializationThrowsReferenceError()
    {
        Assert.Equal(
            "ReferenceError",
            RunStr("let observed; try { typeof x; let x = 1; } catch (e) { observed = e.name; } observed;"));
    }

    [Fact]
    public void UnresolvableReadThrowsReferenceError()
    {
        Assert.Equal(
            "ReferenceError",
            RunStr("let observed; try { definitelyMissing; } catch (e) { observed = e.name; } observed;"));
    }

    [Fact]
    public void AssignmentToConstThrowsTypeError()
    {
        Assert.Equal(
            "TypeError",
            RunStr("let observed; try { const c = 1; c = 2; } catch (e) { observed = e.name; } observed;"));
    }

    [Fact]
    public void ForLetInitializerIsRecreatedOnEachOuterIteration()
    {
        Assert.Equal(
            2,
            RunNum("let count = 0; for (let outer = 0; outer < 2; outer++) { for (let length = 0; length < 1; length++) { count++; } } count;"));
    }

    [Fact]
    public void SwitchClassAndFunctionDeclarationsStayInCaseBlockScope()
    {
        Assert.True(RunBool(@"
            var inside = false;
            var classOutside;
            var generatorOutside;
            var asyncOutside;
            var asyncGeneratorOutside;
            switch (0) {
                default:
                    class C {}
                    inside = typeof C === 'function' && typeof gf === 'function' &&
                             typeof af === 'function' && typeof ag === 'function';
                    function* gf() {}
                    async function af() {}
                    async function* ag() {}
            }
            try { C; } catch (error) { classOutside = error.name; }
            try { gf; } catch (error) { generatorOutside = error.name; }
            try { af; } catch (error) { asyncOutside = error.name; }
            try { ag; } catch (error) { asyncGeneratorOutside = error.name; }
            inside && classOutside === 'ReferenceError' && generatorOutside === 'ReferenceError' &&
            asyncOutside === 'ReferenceError' && asyncGeneratorOutside === 'ReferenceError';
        "));
    }
}
