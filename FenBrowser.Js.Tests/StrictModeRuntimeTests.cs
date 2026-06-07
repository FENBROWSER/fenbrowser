using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class StrictModeRuntimeTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
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
    public void StrictMode_AssignmentToUndeclaredThrowsReferenceError()
    {
        Assert.Equal(
            "ReferenceError",
            RunStr("\"use strict\"; let observed; try { missingInStrict = 1; } catch (e) { observed = e.name; } observed;"));
    }

    [Fact]
    public void StrictMode_IsInheritedByNestedFunctionCode()
    {
        Assert.Equal(
            "ReferenceError",
            RunStr("\"use strict\"; function f(){ nestedMissing = 1; } let observed; try { f(); } catch (e) { observed = e.name; } observed;"));
    }

    [Fact]
    public void StrictMode_IsInheritedByNestedArrowDelete()
    {
        Assert.Equal(
            "TypeError",
            RunStr("\"use strict\"; let observed; try { (() => { delete Boolean.prototype; })(); } catch (e) { observed = e.name; } observed;"));
    }

    [Fact]
    public void SloppyMode_AssignmentToUndeclaredCreatesGlobalProperty()
    {
        Assert.True(RunBool("missingInSloppy = 7; globalThis.missingInSloppy === 7;"));
    }

    [Fact]
    public void FunctionDirective_CompilesAsStrictFunction()
    {
        var script = new BytecodeCompiler().CompileScript(new SourceText("function f(){ 'use strict'; return 1; }"));
        var f = Assert.Single(script.NestedFunctions, n => n.Name == "f");
        Assert.True(f.IsStrictMode);
    }
}
