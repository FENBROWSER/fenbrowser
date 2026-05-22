using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class JsonParseReviverTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void ReviverTransformsScalarValue()
    {
        Assert.Equal(8, RunNum("JSON.parse('4', function(k,v){return typeof v === 'number' ? v*2 : v;});"));
    }

    [Fact]
    public void ReviverTransformsObjectMembers()
    {
        Assert.Equal(20, RunNum("JSON.parse('{\"a\":10}', function(k,v){return typeof v === 'number' ? v*2 : v;}).a;"));
    }

    [Fact]
    public void ReviverReturningUndefinedDeletesMember()
    {
        Assert.Equal("undefined", RunStr("typeof JSON.parse('{\"a\":1,\"b\":2}', function(k,v){return k === 'a' ? undefined : v;}).a;"));
    }

    [Fact]
    public void ReviverWithoutFunctionIsIgnored()
    {
        // Non-callable reviver is silently skipped (spec step 2).
        Assert.Equal(4, RunNum("JSON.parse('4', 42);"));
    }

    [Fact]
    public void ReviverVisitsArrayElements()
    {
        Assert.Equal(6, RunNum("JSON.parse('[1,2,3]', function(k,v){return typeof v === 'number' ? v*2 : v;})[2];"));
    }
}
