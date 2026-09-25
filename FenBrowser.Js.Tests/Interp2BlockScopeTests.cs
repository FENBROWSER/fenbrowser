using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 14.2 blocks as environment records on the register-window loop.
// A body whose blocks can be window slots keeps them there; one whose blocks
// cannot - a closure captures a block binding, a loop gives every turn its own
// copy, an early exit leaves several blocks at once - keeps them as records,
// pushed and popped as the bytecode says. Both used to be declined.
[Collection(nameof(Interpreter2ParityTests))]
public sealed class Interp2BlockScopeTests : IDisposable
{
    private readonly bool _previousEngine = Interp2Options.Enabled;

    public Interp2BlockScopeTests() => Interp2Options.Enabled = true;

    public void Dispose() => Interp2Options.Enabled = _previousEngine;

    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    private static string Run(string source) => new BytecodeInterpreter().Execute(Compile(source)).AsString();

    [Theory]
    [InlineData("function f() { var fs = []; for (let i = 0; i < 3; i++) fs.push(() => i); return fs; }")]
    [InlineData("function f() { var g; { let x = 1; g = () => x; } return g; }")]
    [InlineData("function f() { for (let i = 0; i < 5; i++) { let j = i; if (j > 2) break; } }")]
    public void ABodyWhoseBlocksCannotBeSlotsRunsWithBlockRecords(string declaration)
    {
        var layout = FrameLayout.For(Compile(declaration).NestedFunctions[0]);
        Assert.Equal(Interp2Bailout.None, layout.Bailout);
    }

    [Theory]
    // Each turn of a for-let loop has its own binding.
    [InlineData("function f() { var fs = []; for (let i = 0; i < 3; i++) fs.push(() => i);" +
                " return fs.map(g => g()).join(); } f();", "0,1,2")]
    [InlineData("function f() { var fs = []; for (const v of [4, 5]) fs.push(() => v);" +
                " return fs.map(g => g()).join(); } f();", "4,5")]
    [InlineData("function f() { var fs = []; for (const k in { a: 1, b: 2 }) fs.push(() => k);" +
                " return fs.map(g => g()).join(); } f();", "a,b")]
    // A closure sees later writes to the block binding it captured.
    [InlineData("function f() { var g; { let x = 1; g = () => x; x = 2; } return String(g()); } f();", "2")]
    // Nested blocks shadowing one name are distinct bindings.
    [InlineData("function f() { var g, h; { let x = 1; { let x = 2; g = () => x; } h = () => x; }" +
                " return g() + ',' + h(); } f();", "2,1")]
    [InlineData("function f() { let x = 'outer'; { let x = 'inner'; var g = () => x; } return g() + ',' + x; } f();",
                "inner,outer")]
    [InlineData("function f() { var x = 'v'; var g; { let x = 'b'; g = () => x; } return g() + x; } f();", "bv")]
    // Leaving several blocks at once: break, continue, labels, return.
    [InlineData("function f() { var fs = []; for (let i = 0; i < 5; i++) { let j = i * 2; if (j > 4) break;" +
                " fs.push(() => j); } return fs.map(g => g()).join(); } f();", "0,2,4")]
    [InlineData("function f() { var fs = []; outer: for (let i = 0; i < 3; i++) { for (let j = 0; j < 3; j++) {" +
                " if (j > i) continue outer; fs.push(() => i + '' + j); } } return fs.map(g => g()).join(); } f();",
                "00,10,11,20,21,22")]
    [InlineData("function f() { for (let i = 0; i < 3; i++) { let g = () => i; if (i === 1) return g(); } } String(f());",
                "1")]
    // switch cases share one block.
    [InlineData("function f(n) { switch (n) { case 1: let a = 'one'; var g = () => a; break; default: a = 'x'; }" +
                " return g(); } f(1);", "one")]
    // x++ and x op= e on a captured block binding.
    [InlineData("function f() { var g; { let n = 1; g = () => n; n++; n += 2; } return String(g()); } f();", "4")]
    // A catch parameter is a block binding.
    [InlineData("function f() { var g; try { throw 5; } catch (e) { g = () => e; } return String(g()); } f();", "5")]
    public void BlockBindingsBehaveAsTheSpecificationSays(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    [InlineData("function f() { var r; { var g = () => x; try { g(); } catch (e) { r = e.constructor.name; }" +
                " let x = 1; r += ',' + g(); } return r; } f();", "ReferenceError,1")]
    [InlineData("function f() { var r; { var g = () => x; try { typeof x; } catch (e) { r = e.constructor.name; }" +
                " let x = 1; } return r; } f();", "ReferenceError")]
    [InlineData("function f() { var g; { const c = 1; g = () => c; try { c = 2; } catch (e) { return e.constructor.name; } } }" +
                " f();", "TypeError")]
    public void TheDeadZoneAndConstnessAreTheRecords(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Fact]
    public void AThrowOutOfNestedBlocksLandsInTheHandlersScope()
    {
        // The catch must see the frame's variables again, not the blocks the
        // throw left, and blocks entered afterwards chain to the right place.
        Assert.Equal("3,6,top", Run(
            "function f() { var top = 'top', g, h; try { { let a = 1; { let b = 2; g = () => a + b; throw 0; } } }" +
            " catch (e) { let c = 3; h = () => c * 2; } return g() + ',' + h() + ',' + top; } f();"));
    }

    [Fact]
    public void AGeneratorSuspendsInsideABlockRecord()
    {
        Assert.Equal("0,1,2", Run(
            "function* gen() { for (let i = 0; i < 3; i++) { yield () => i; } }" +
            " var fs = []; for (var g of gen()) fs.push(g); fs.map(g => g()).join();"));
    }

    [Fact]
    public void AnAsyncFunctionAwaitsInsideABlockRecord()
    {
        var interpreter = new BytecodeInterpreter();
        interpreter.Execute(Compile(
            "var out = 'pending'; async function a() { var fs = []; for (let i = 0; i < 3; i++) { await null; fs.push(() => i); }" +
            " out = fs.map(g => g()).join(); } a();"));
        Assert.Equal("0,1,2", interpreter.Execute(Compile("out;")).AsString());
    }
}
