using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Modules;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// A module namespace is read-only from outside (ECMA-262 10.4.6.9 [[Set]]
// returns false), and a module that does not parse rejects its import() with a
// SyntaxError.
public sealed class ModuleNamespaceSetTests
{
    private static BytecodeInterpreter WithModules(params (string Key, string Source)[] modules)
    {
        var interpreter = new BytecodeInterpreter();
        var sources = new System.Collections.Generic.Dictionary<string, string>();
        foreach (var (key, source) in modules)
        {
            sources[key] = source;
        }

        _ = new ModuleEvaluator(interpreter, key => sources.TryGetValue(key, out var s) ? s : null);
        return interpreter;
    }

    private static string Run(BytecodeInterpreter interpreter, string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        interpreter.Execute(fn);
        interpreter.PumpMicrotasks();
        var read = new BytecodeCompiler().CompileScript(new SourceText("outcome;"));
        return interpreter.Execute(read).AsString();
    }

    [Fact]
    public void ReflectSetOnANamespaceIsFalseEvenForTheSameValue()
    {
        var interpreter = WithModules(("m", "export var a = 1;"));
        Assert.Equal("false,false,false", Run(interpreter, @"
            var outcome = 'pending';
            import('m').then(function (ns) {
                outcome = [Reflect.set(ns, 'a', 1), Reflect.set(ns, 'b', 2), Reflect.set(ns, Symbol.toStringTag, 'Module')].join();
            });"));
    }

    [Fact]
    public void AModuleThatDoesNotParseRejectsWithSyntaxError()
    {
        var interpreter = WithModules(("bad", "with ({}) {}"));
        Assert.Equal("SyntaxError", Run(interpreter, @"
            var outcome = 'pending';
            import('bad').then(function () { outcome = 'fulfilled'; }, function (e) { outcome = e.name; });"));
    }
}
