using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ErrorPrototypeToStringTests
{
    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void DefaultErrorIsJustTheName()
    {
        Assert.Equal("Error", RunStr("new Error().toString();"));
    }

    [Fact]
    public void ErrorWithMessageJoinsByColon()
    {
        Assert.Equal("Error: boom", RunStr("new Error('boom').toString();"));
    }

    [Fact]
    public void NamedSubclassUsesItsName()
    {
        Assert.Equal("TypeError: bad", RunStr("new TypeError('bad').toString();"));
    }

    [Fact]
    public void EmptyMessageDropsColon()
    {
        Assert.Equal("Error", RunStr("var e = new Error(); e.message = ''; e.toString();"));
    }

    [Fact]
    public void EmptyNameDropsName()
    {
        Assert.Equal("oh no", RunStr("var e = new Error('oh no'); e.name = ''; e.toString();"));
    }
}
