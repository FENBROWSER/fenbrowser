using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Free-variable reads in bodies whose blocks are records: every function here
// captures a block-scoped binding in a closure, so the register-window loop lays
// it out with its blocks as records, and a name can resolve to a nearer
// binding at one instruction than at another. Compiled code keys its sites by
// instruction (BytecodeFunction.NameSites) for the same reason.
public sealed class DispatchLoopFreeVariableSiteTests
{
    private const string KeepOnDispatchLoop = "{ let pin = 0; var keep = function () { return pin; }; }";

    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void ANameThatABlockShadowsResolvesPerInstruction()
    {
        // Both reads of x use the same slot; only the instruction tells them apart.
        Assert.Equal("outer,block0,outer,block1,outer,block2|outer,block0,outer,block1,outer,block2", Run($@"
            var x = 'outer';
            function f() {{
                {KeepOnDispatchLoop}
                var r = [];
                for (var i = 0; i < 3; i++) {{
                    r.push(x);
                    {{ let x = 'block' + i; r.push(x); }}
                }}
                return r.join();
            }}
            f() + '|' + f();"));
    }

    [Fact]
    public void ClosureAndGlobalWritesLandWhereReadsSeeThem()
    {
        Assert.Equal("200,200,TypeError", Run($@"
            var g = 0;
            function make() {{
                var c = 0;
                const k = 1;
                return function (n) {{
                    {KeepOnDispatchLoop}
                    for (var i = 0; i < n; i++) {{ c = c + 1; g = g + 1; }}
                    var name = '';
                    try {{ k = 2; }} catch (e) {{ name = e.constructor.name; }}
                    return c + ',' + g + ',' + name;
                }};
            }}
            var f = make();
            f(100);
            f(100);"));
    }

    [Fact]
    public void AVarAddedByEvalShadowsTheCachedGlobal()
    {
        Assert.Equal("global|outer", Run($@"
            var x = 'global';
            function outer() {{
                var read = function () {{ {KeepOnDispatchLoop} return x; }};
                var before = '';
                for (var i = 0; i < 50; i++) before = read();
                eval('var x = ""outer""');
                return before + '|' + read();
            }}
            outer();"));
    }

    [Fact]
    public void APropertyAddedToAWithObjectShadowsTheCachedGlobal()
    {
        Assert.Equal("global|obj", Run($@"
            var y = 'global';
            var o = {{}};
            var read;
            with (o) {{ read = function () {{ {KeepOnDispatchLoop} return y; }}; }}
            var before = '';
            for (var i = 0; i < 50; i++) before = read();
            o.y = 'obj';
            before + '|' + read();"));
    }
    [Fact]
    public void IncrementsOfOuterVariablesLandOnTheDispatchLoop()
    {
        Assert.Equal("300,300", Run($@"
            var g = 0;
            function make() {{
                var c = 0;
                return function (n) {{ {KeepOnDispatchLoop} for (var i = 0; i < n; i++) {{ c++; g++; }} return c + ',' + g; }};
            }}
            var f = make(), r;
            for (var k = 0; k < 3; k++) r = f(100);
            r;"));
    }
}
