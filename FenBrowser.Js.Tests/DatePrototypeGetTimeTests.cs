using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class DatePrototypeGetTimeTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void GetTimeReturnsConstructorArgument()
    {
        Assert.Equal(123456d, Run("new Date(123456).getTime();").AsNumber());
    }

    [Fact]
    public void ValueOfReturnsSameAsGetTime()
    {
        Assert.Equal(7777d, Run("new Date(7777).valueOf();").AsNumber());
    }

    [Fact]
    public void GetTimeOnNonDateThrowsTypeError()
    {
        Assert.Throws<JsThrownException>(() => Run("Date.prototype.getTime.call({});"));
    }
}
