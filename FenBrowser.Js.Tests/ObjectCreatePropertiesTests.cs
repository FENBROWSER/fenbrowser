using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ObjectCreatePropertiesTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void InstallsDataPropertiesFromDescriptors()
    {
        Assert.Equal(1d, Run("Object.create(null, {a:{value:1, enumerable:true}}).a;").AsNumber());
        Assert.Equal(2d, Run("Object.create(null, {a:{value:1, enumerable:true}, b:{value:2, enumerable:true}}).b;").AsNumber());
    }

    [Fact]
    public void PrototypeStillRespected()
    {
        // a should be inherited from prototype, b should be own.
        Assert.True(Run("var p={a:1}; var o=Object.create(p, {b:{value:2, enumerable:true}}); o.a===1 && o.b===2;").AsBoolean());
    }

    [Fact]
    public void NonObjectPropertiesArgThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.create(null, 5);"));
    }

    [Fact]
    public void UndefinedPropertiesArgSkipped()
    {
        Assert.Equal(0d, Run("Object.keys(Object.create(null, undefined)).length;").AsNumber());
    }

    [Fact]
    public void RespectsEnumerableFlagOfDescriptor()
    {
        // Only enumerable descriptor entries become properties (the enumerable
        // flag on the descriptor object itself, not the value's enumerable field).
        Assert.True(Run("var o=Object.create(null, {a:{value:1, enumerable:false}}); 'a' in o;").AsBoolean());
    }
}
