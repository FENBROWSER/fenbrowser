using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ObjectGetOwnPropertyDescriptorsTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    private static double RunNum(string source) => Run(source).AsNumber();
    private static bool RunBool(string source) => Run(source).AsBoolean();

    [Fact]
    public void ReturnsOneEntryPerOwnKey()
    {
        Assert.Equal(2, RunNum("Object.keys(Object.getOwnPropertyDescriptors({a:1,b:2})).length;"));
    }

    [Fact]
    public void EachEntryIsADescriptor()
    {
        Assert.Equal(1, RunNum("Object.getOwnPropertyDescriptors({a:1}).a.value;"));
        Assert.True(RunBool("Object.getOwnPropertyDescriptors({a:1}).a.writable;"));
        Assert.True(RunBool("Object.getOwnPropertyDescriptors({a:1}).a.enumerable;"));
        Assert.True(RunBool("Object.getOwnPropertyDescriptors({a:1}).a.configurable;"));
    }

    [Fact]
    public void EmptyObjectReturnsEmptyResult()
    {
        Assert.Equal(0, RunNum("Object.keys(Object.getOwnPropertyDescriptors({})).length;"));
    }

    [Fact]
    public void RejectsNullAndUndefined()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.getOwnPropertyDescriptors(null);"));
        Assert.Throws<JsThrownException>(() => Run("Object.getOwnPropertyDescriptors(undefined);"));
        Assert.Throws<JsThrownException>(() => Run("Object.getOwnPropertyDescriptors();"));
    }
}
