using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ArrayIsArrayTests
{
    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Theory]
    [InlineData("Array.isArray([]);", true)]
    [InlineData("Array.isArray([1, 2, 3]);", true)]
    [InlineData("Array.isArray(new Array(3));", true)]
    [InlineData("Array.isArray({});", false)]
    [InlineData("Array.isArray({length: 0});", false)]
    [InlineData("Array.isArray('abc');", false)]
    [InlineData("Array.isArray(42);", false)]
    [InlineData("Array.isArray(null);", false)]
    [InlineData("Array.isArray(undefined);", false)]
    [InlineData("Array.isArray();", false)]
    [InlineData("Array.isArray(true);", false)]
    [InlineData("Array.isArray(function(){});", false)]
    public void ClassifiesValueCorrectly(string source, bool expected)
    {
        Assert.Equal(expected, RunBool(source));
    }

    [Fact]
    public void ReturnsTrueForProxyWrappingArray()
    {
        Assert.True(RunBool("Array.isArray(new Proxy([], {}));"));
    }

    [Fact]
    public void ThrowsTypeErrorForRevokedProxyWrappingArray()
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(@"
            var handle = Proxy.revocable([], {});
            handle.revoke();
            Array.isArray(handle.proxy);
        "));

        new BytecodeVerifier().Verify(fn);

        Assert.Throws<JsThrownException>(() => new BytecodeInterpreter().Execute(fn));
    }
}
