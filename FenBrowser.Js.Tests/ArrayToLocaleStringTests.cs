using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayToLocaleStringTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void JoinsByCommaLikeToString()
    {
        Assert.Equal("1,2,3", RunStr("[1,2,3].toLocaleString();"));
    }

    [Fact]
    public void EmptyArrayIsEmptyString()
    {
        Assert.Equal("", RunStr("[].toLocaleString();"));
    }

    [Fact]
    public void NullAndUndefinedElementsRenderEmpty()
    {
        Assert.Equal("a,,c", RunStr("['a',null,'c'].toLocaleString();"));
    }
}
