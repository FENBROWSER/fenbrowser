using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Modules;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Module code on the register-window loop: an async program body over the
// module's environment, suspending at a top-level await like an async
// function (ECMA-262 16.2.1.6.5 ExecuteModule), and the import opcodes.
[Collection(nameof(Interpreter2ParityTests))]
public sealed class Interp2ModuleCodeTests : IDisposable
{
    private readonly bool _previousEngine = Interp2Options.Enabled;

    public Interp2ModuleCodeTests() => Interp2Options.Enabled = true;

    public void Dispose() => Interp2Options.Enabled = _previousEngine;

    private static (BytecodeInterpreter, ModuleEvaluator) Setup()
    {
        var interpreter = new BytecodeInterpreter();
        return (interpreter, new ModuleEvaluator(interpreter));
    }

    [Fact]
    public void ModuleBodiesRunOnTheRegisterWindow()
    {
        var program = JsParser.ParseModule(
            new SourceText("import.meta; await import('x'); export const v = 1;"));
        var module = new BytecodeCompiler().CompileProgram(program);
        var layout = FrameLayout.For(module);
        Assert.Equal(Interp2Bailout.None, layout.Bailout);
        Assert.True(layout.AsyncEligible);
    }

    [Theory]
    [InlineData("globalThis.out = String(await Promise.resolve(5));", "5")]
    // Suspending twice, with a try around the second await.
    [InlineData("var a = await 1; var b; try { b = await Promise.reject(new Error('r')); } catch (e) { b = e.message; }" +
                " globalThis.out = a + ',' + b;", "1,r")]
    // A module's top-level declarations stay in the module.
    [InlineData("let local = 1; var alsoLocal = 2; globalThis.out = typeof globalThis.local + ',' + typeof globalThis.alsoLocal;",
                "undefined,undefined")]
    [InlineData("globalThis.out = String(this);", "undefined")]
    [InlineData("globalThis.out = typeof import.meta;", "object")]
    public void ModuleCodeRunsAndSuspends(string source, string expected)
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("mod", source);
        _ = evaluator.Evaluate("mod");
        Assert.True(interpreter.TryReadGlobalValue("out", out var value));
        Assert.Equal(expected, value.AsString());
    }

    [Fact]
    public void ImportsResolveThroughTheModuleEnvironment()
    {
        var (interpreter, evaluator) = Setup();
        evaluator.RegisterSource("dep", "export let count = 1; export function bump() { count++; }");
        evaluator.RegisterSource("main", "import { count, bump } from 'dep'; bump(); globalThis.out = String(count);");
        _ = evaluator.Evaluate("main");
        Assert.True(interpreter.TryReadGlobalValue("out", out var value));
        Assert.Equal("2", value.AsString());
    }
}
