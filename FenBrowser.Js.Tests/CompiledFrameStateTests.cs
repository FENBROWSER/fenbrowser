using System.Reflection;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// What the runtime reads off a baseline-compiled frame when an error is made or
// thrown there. Each case compiles one named function, whose loop is what makes
// a call run it compiled, and calls it.
public sealed class CompiledFrameStateTests
{
    private static string RunWithCompiled(string name, string source)
    {
        var script = new BytecodeCompiler().CompileScript(new SourceText(source));
        var target = script.NestedFunctions.Single(f => f.Name == name);
        var compiled = Assert.IsType<JitCompiler.JitDelegate>(JitCompiler.TryCompile(target));
        typeof(BytecodeFunction)
            .GetField("JitDelegate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, compiled);
        // A call runs a compiled body only when the body loops enough to be
        // worth it (JitCompiler.PrefersCompiled); say that it has.
        target.BackEdges = 1 << 20;
        Assert.True(JitCompiler.PrefersCompiled(target), "the call would not run the compiled body");
        return new BytecodeInterpreter().Execute(script).AsString();
    }

    [Fact]
    public void ErrorStackGivesTheLineAndColumnInACompiledFrame()
    {
        var stack = RunWithCompiled("inner",
            "function inner() {\n" +
            "  for (var pad = 0; pad < 1; pad++) { }\n" +
            "  return new Error('here').stack;\n" +
            "}\n" +
            "inner();");

        Assert.Equal("    at inner (<anonymous>:3:10)", stack.Split('\n')[1]);
    }

    [Fact]
    public void ANotAFunctionMessageNamesTheCalleeInACompiledFrame()
    {
        var message = RunWithCompiled("probe",
            "function probe(doc) { for (var i = 0; i < 1; i++) { } try { doc.createRange(); } catch (e) { return e.message; } return 'no'; } probe({});");

        Assert.StartsWith("doc.createRange is not a function", message);
    }

    [Fact]
    public void ANullishBaseThrowsBeforeTheKeyIsConverted()
    {
        // ECMA-262 13.3.2.1: RequireObjectCoercible on the base comes first.
        Assert.Equal("TypeError,false", RunWithCompiled("read", @"
            function read(b, k) { for (var i = 0; i < 1; i++) { } return b[k]; }
            var converted = false;
            var key = { toString() { converted = true; return 'x'; } };
            var r;
            try { read(null, key); } catch (e) { r = e.constructor.name; }
            r + ',' + converted;"));
    }
}
