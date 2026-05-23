using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class NativeErrorConstructorTests
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

    [Fact] public void ReferenceErrorName()  => Assert.Equal("ReferenceError", RunStr("new ReferenceError('x').name;"));
    [Fact] public void ReferenceErrorMessage() => Assert.Equal("oops", RunStr("new ReferenceError('oops').message;"));
    [Fact] public void EvalErrorName()       => Assert.Equal("EvalError", RunStr("new EvalError('x').name;"));
    [Fact] public void EvalErrorInheritsErrorPrototype()
    {
        // The prototype chain is NativeError.prototype -> Error.prototype -> Object.prototype
        // so `e instanceof Error` holds for any native error subclass per 20.5.6.1.
        Assert.True(RunBool("new ReferenceError() instanceof Error;"));
        Assert.True(RunBool("new EvalError() instanceof Error;"));
    }

    [Fact]
    public void ReferenceErrorToStringFormat()
    {
        Assert.Equal("ReferenceError: bad", RunStr("new ReferenceError('bad').toString();"));
    }
}
