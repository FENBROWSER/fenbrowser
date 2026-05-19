using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ObjectAndBytecodeTests
{
    [Fact]
    public void ObjectPropertyDescriptorAndPrototypeLookupWorks()
    {
        var heap = new JsHeap();
        var parent = new JsObject();
        parent.DefineOwnProperty("p", new JsPropertyDescriptor(JsValue.FromNumber(10), true, true, true));
        var parentHandle = heap.AllocateObject(parent, AllocationSite.Current());

        var child = new JsObject();
        child.SetPrototype(parentHandle);

        var found = child.TryGetProperty("p", h => heap.GetObject(h), out var descriptor);
        Assert.True(found);
        Assert.Equal(10, descriptor.Value.AsNumber());
    }

    [Fact]
    public void NonWritablePropertyCannotBeSet()
    {
        var obj = new JsObject();
        obj.DefineOwnProperty("x", new JsPropertyDescriptor(JsValue.FromNumber(1), Writable: false, Enumerable: true, Configurable: true));

        Assert.False(obj.SetProperty("x", JsValue.FromNumber(2)));
        Assert.True(obj.TryGetOwnProperty("x", out var descriptor));
        Assert.Equal(1, descriptor.Value.AsNumber());
    }

    [Fact]
    public void BytecodeVerifierRejectsMissingReturn()
    {
        var fn = new BytecodeFunction
        {
            RegisterCount = 2,
            Constants = Array.Empty<JsValue>(),
            VariableSlots = new Dictionary<string, int>(),
            PropertyNames = Array.Empty<string>(),
            Instructions = new[] { new Instruction(OpCode.LoadConst, 1, 0, 0) }
        };

        var verifier = new BytecodeVerifier();
        Assert.Throws<InvalidOperationException>(() => verifier.Verify(fn));
    }

    [Fact]
    public void BytecodeVerifierRejectsInvalidJumpTarget()
    {
        var fn = new BytecodeFunction
        {
            RegisterCount = 2,
            Constants = new[] { JsValue.FromNumber(1) },
            VariableSlots = new Dictionary<string, int>(),
            PropertyNames = Array.Empty<string>(),
            Instructions = new[]
            {
                new Instruction(OpCode.LoadConst, 1, 0, 0),
                new Instruction(OpCode.Jump, 99, 0, 0),
                new Instruction(OpCode.Return, 0, 0, 0)
            }
        };

        var verifier = new BytecodeVerifier();
        Assert.Throws<InvalidOperationException>(() => verifier.Verify(fn));
    }

    [Fact]
    public void CompilerAndInterpreterRunArithmeticAndVariables()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 1 + 2 * 3; x = x + 1; x;"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(8, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterRunIfElse()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 0; if (1) { x = 7; } else { x = 9; } x;"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(7, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterRunWhileLoop()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 3; while (x) { x = x - 1; } x;"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(0, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHonorReturn()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 1; return x + 2; x = 99;"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(3, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleTryCatchThrow()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let x = 1; try { throw 9; } catch (e) { x = e; } x;"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(9, result.AsNumber());
    }

    [Fact]
    public void InterpreterThrowsForUncaughtThrow()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("throw 5;"));
        new BytecodeVerifier().Verify(fn);

        var ex = Assert.Throws<JsThrownException>(() => new BytecodeInterpreter().Execute(fn));
        Assert.Equal(5, ex.Value.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleObjectAndMemberAccess()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let o = { a: 1 }; o.a = o.a + 2; o.a;"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(3, result.AsNumber());
    }

    [Fact]
    public void CompilerAndInterpreterHandleArrayLiteralAndIndexAccess()
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText("let arr = [1,2,3]; arr[1] = arr[1] + 5; arr[1];"));
        new BytecodeVerifier().Verify(fn);

        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal(7, result.AsNumber());
    }
}
