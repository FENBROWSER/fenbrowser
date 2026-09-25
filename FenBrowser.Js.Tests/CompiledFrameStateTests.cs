using System.Reflection;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// What the runtime reads off a baseline-compiled frame when an error is made or
// thrown there. Each case compiles one named function and runs the script on
// the dispatch loop, which calls a compiled function's delegate.
[Collection(nameof(Interpreter2ParityTests))]
public sealed class CompiledFrameStateTests
{
    private static string RunWithCompiled(string name, string source)
    {
        var previous = Interp2Options.Enabled;
        Interp2Options.Enabled = false;
        try
        {
            var script = new BytecodeCompiler().CompileScript(new SourceText(source));
            var target = script.NestedFunctions.Single(f => f.Name == name);
            var compiled = Assert.IsType<JitCompiler.JitDelegate>(JitCompiler.TryCompile(target));
            typeof(BytecodeFunction)
                .GetField("JitDelegate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(target, compiled);
            return new BytecodeInterpreter().Execute(script).AsString();
        }
        finally
        {
            Interp2Options.Enabled = previous;
        }
    }

    [Fact]
    public void ErrorStackGivesTheLineAndColumnInACompiledFrame()
    {
        var stack = RunWithCompiled("inner",
            "function inner() {\n" +
            "  var pad = 1;\n" +
            "  return new Error('here').stack;\n" +
            "}\n" +
            "inner();");

        Assert.Equal("    at inner (<anonymous>:3:10)", stack.Split('\n')[1]);
    }

    [Fact]
    public void ANotAFunctionMessageNamesTheCalleeInACompiledFrame()
    {
        var message = RunWithCompiled("probe",
            "function probe(doc) { try { doc.createRange(); } catch (e) { return e.message; } return 'no'; } probe({});");

        Assert.StartsWith("doc.createRange is not a function", message);
    }

    [Fact]
    public void ANullishBaseThrowsBeforeTheKeyIsConverted()
    {
        // ECMA-262 13.3.2.1: RequireObjectCoercible on the base comes first.
        Assert.Equal("TypeError,false", RunWithCompiled("read", @"
            function read(b, k) { return b[k]; }
            var converted = false;
            var key = { toString() { converted = true; return 'x'; } };
            var r;
            try { read(null, key); } catch (e) { r = e.constructor.name; }
            r + ',' + converted;"));
    }
}
