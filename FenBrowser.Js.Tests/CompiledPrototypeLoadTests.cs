using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Jit.Baseline;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Compiled code misses into LoadPropertyMiss, which attaches programs to the
// site. A read that lands on a prototype must attach, hit, and still notice an
// own property that later shadows it.
public sealed class CompiledPrototypeLoadTests
{
    private static JsValue RunCompiled(string source, out BytecodeFunction reader)
    {
        var script = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(script);
        reader = script.NestedFunctions[0];
        var compiled = BaselineCompiler.TryCompile(reader);
        Assert.NotNull(compiled);
        reader.JitCompileAttempted = true;
        reader.JitDelegate = compiled;
        reader.BackEdges = int.MaxValue;
        Assert.True(JitCompiler.PrefersCompiled(reader));
        return new BytecodeInterpreter().Execute(script);
    }

    [Fact]
    public void APrototypeReadInCompiledCodeIsCachedAndStaysCorrect()
    {
        var result = RunCompiled(@"
            function sum(o, n) { var t = 0; for (var i = 0; i < n; i++) t = (t + o.step) | 0; return t; }
            function Base() {}
            Base.prototype.step = 2;
            var b = new Base();
            var first = sum(b, 100);
            b.step = 5;
            first + ',' + sum(b, 100);", out var reader);

        Assert.Equal("200,500", result.AsString());
        Assert.NotNull(reader.LoadCacheSites);
        Assert.Contains(reader.LoadCacheSites!, site => site is { Count: > 0 });
    }

    [Fact]
    public void ACompiledSiteLearnsAPrototypeRead()
    {
        // Only the prototype read ever reaches this site, so a program on it can
        // only have come from that read.
        var result = RunCompiled(@"
            function sum(o, n) { var t = 0; for (var i = 0; i < n; i++) t = (t + o.step) | 0; return t; }
            function Base() {}
            Base.prototype.step = 2;
            sum(new Base(), 100);", out var reader);

        Assert.Equal(200.0, result.AsNumber());
        Assert.Contains(reader.LoadCacheSites!, site => site is { Count: > 0 });
    }
}
