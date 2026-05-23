using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class FinalizationRegistryTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void RegisterReturnsUndefined()
    {
        Assert.Equal(JsValueTag.Undefined,
            Run("new FinalizationRegistry(function(){}).register({}, 'held');").Tag);
    }

    [Fact]
    public void UnregisterReturnsFalseWhenTokenMissing()
    {
        Assert.False(Run("new FinalizationRegistry(function(){}).unregister({});").AsBoolean());
    }

    [Fact]
    public void UnregisterReturnsTrueWhenTokenMatches()
    {
        Assert.True(Run("var t={}; var r=new FinalizationRegistry(function(){}); r.register({}, 'h', t); r.unregister(t);").AsBoolean());
    }

    [Fact]
    public void ConstructorRequiresCallableCallback()
    {
        Assert.Throws<JsThrownException>(() => Run("new FinalizationRegistry({});"));
        Assert.Throws<JsThrownException>(() => Run("new FinalizationRegistry();"));
    }

    [Fact]
    public void RegisterRequiresObjectTarget()
    {
        Assert.Throws<JsThrownException>(() => Run("new FinalizationRegistry(function(){}).register(5);"));
    }
}
