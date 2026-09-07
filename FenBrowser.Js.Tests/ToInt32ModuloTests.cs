using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 7.1.6 ToInt32 / 7.1.7 ToUint32 truncate then reduce modulo 2^32.
/// The conversions used to route through a long, which saturates past 2^63 and
/// whose out-of-range result is platform-defined besides (audit JSRT-006).
/// </summary>
public sealed class ToInt32ModuloTests
{
    private static string RunString(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Theory]
    [InlineData("1e21", "-559939584")]
    [InlineData("-1e21", "559939584")]
    [InlineData("1e30", "0")]
    [InlineData("4294967297", "1")]
    [InlineData("-4294967297", "-1")]
    [InlineData("2147483648", "-2147483648")]
    public void Int32ArrayStoreWrapsModuloTwoToTheThirtySecond(string value, string expected)
    {
        Assert.Equal(expected, RunString($"var a = new Int32Array(1); a[0] = {value}; String(a[0]);"));
    }

    [Theory]
    [InlineData("1e21", "3735027712")]
    [InlineData("-1e21", "559939584")]
    [InlineData("4294967297", "1")]
    public void Uint32ArrayStoreWrapsModuloTwoToTheThirtySecond(string value, string expected)
    {
        Assert.Equal(expected, RunString($"var a = new Uint32Array(1); a[0] = {value}; String(a[0]);"));
    }

    [Theory]
    [InlineData("1e21", "0")]
    [InlineData("257", "1")]
    [InlineData("-1", "255")]
    public void Uint8ArrayStoreWrapsModuloTwoToTheEighth(string value, string expected)
    {
        Assert.Equal(expected, RunString($"var a = new Uint8Array(1); a[0] = {value}; String(a[0]);"));
    }

    [Fact]
    public void DataViewSetInt32WrapsModuloTwoToTheThirtySecond()
    {
        Assert.Equal("-559939584", RunString(
            "var d = new DataView(new ArrayBuffer(4)); d.setInt32(0, 1e21); String(d.getInt32(0));"));
    }

    [Theory]
    [InlineData("1e21 | 0", "-559939584")]
    [InlineData("1e21 >>> 0", "3735027712")]
    [InlineData("(-1e21) | 0", "559939584")]
    public void BitwiseOperatorsUseTheSameModulo(string expression, string expected)
    {
        Assert.Equal(expected, RunString($"String({expression});"));
    }
}
