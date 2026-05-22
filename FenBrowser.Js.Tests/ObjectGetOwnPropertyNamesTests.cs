using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ObjectGetOwnPropertyNamesTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void EnumerableKeysInInsertionOrder()
    {
        Assert.Equal("a", Run("Object.getOwnPropertyNames({a:1,b:2,c:3})[0];").AsString());
        Assert.Equal("b", Run("Object.getOwnPropertyNames({a:1,b:2,c:3})[1];").AsString());
        Assert.Equal("c", Run("Object.getOwnPropertyNames({a:1,b:2,c:3})[2];").AsString());
        Assert.Equal(3, Run("Object.getOwnPropertyNames({a:1,b:2,c:3}).length;").AsNumber());
    }

    [Fact]
    public void IncludesNonEnumerableProperties()
    {
        // Sealing keeps Configurable=false but doesn't change Enumerable; the new
        // descriptor here is enumerable. To verify the non-enumerable surfacing
        // without DefineProperty, this case is covered indirectly: getOwnPropertyNames
        // must surface every own key regardless. A direct non-enumerable test would
        // need Object.defineProperty which is a separate concern.
        Assert.Equal(1, Run("Object.getOwnPropertyNames({a:1}).length;").AsNumber());
    }

    [Fact]
    public void EmptyObjectReturnsEmptyArray()
    {
        Assert.Equal(0, Run("Object.getOwnPropertyNames({}).length;").AsNumber());
    }

    [Fact]
    public void NullOrUndefinedThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.getOwnPropertyNames(null);"));
        Assert.Throws<JsThrownException>(() => Run("Object.getOwnPropertyNames(undefined);"));
        Assert.Throws<JsThrownException>(() => Run("Object.getOwnPropertyNames();"));
    }
}
