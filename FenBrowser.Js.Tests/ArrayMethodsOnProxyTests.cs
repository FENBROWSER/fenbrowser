using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Array.prototype methods that delete or move elements run
/// DeletePropertyOrThrow and Set(O, P, V, true) on their receiver (ECMA-262
/// 23.1.3). On a Proxy those are its deleteProperty and set traps - calling the
/// object model directly threw a CLR "must be dispatched through the owning
/// interpreter" error instead.
/// </summary>
public sealed class ArrayMethodsOnProxyTests
{
    private static string RunString(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private const string TracedProxy =
        "var log = []; " +
        "var p = new Proxy([1, 2, 3], { " +
        "  set: function (t, k, v, r) { log.push('s' + String(k)); return Reflect.set(t, k, v, r); }, " +
        "  deleteProperty: function (t, k) { log.push('d' + k); return Reflect.deleteProperty(t, k); } " +
        "}); ";

    [Theory]
    [InlineData("p.pop();", "d2,slength", "1,2")]
    [InlineData("p.shift();", "s0,s1,d2,slength", "2,3")]
    [InlineData("p.reverse();", "s0,s2", "3,2,1")]
    [InlineData("p.unshift(0);", "s3,s2,s1,s0,slength", "0,1,2,3")]
    public void ArrayMethodRunsTheProxyTraps(string call, string expectedTraps, string expectedContents)
    {
        var result = RunString(
            TracedProxy + call + " log.join(',') + '|' + Array.prototype.join.call(p);");

        Assert.Equal(expectedTraps + "|" + expectedContents, result);
    }

    [Fact]
    public void RefusedDeleteThrowsTypeError()
    {
        var result = RunString(
            "var p = new Proxy([1, 2], { deleteProperty: function () { return false; } }); " +
            "var outcome; try { p.pop(); outcome = 'no throw'; } " +
            "catch (e) { outcome = e instanceof TypeError ? 'TypeError' : String(e); } outcome;");

        Assert.Equal("TypeError", result);
    }

    [Fact]
    public void ReversePreservesHolesOnOrdinaryArrays()
    {
        Assert.Equal("3,,1|false", RunString("var a = [1, , 3]; a.reverse(); String(a) + '|' + (1 in a);"));
    }
}
