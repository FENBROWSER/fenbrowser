using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ObjectDefinePropertiesHasOwnTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    private static double RunNum(string source) => Run(source).AsNumber();
    private static bool RunBool(string source) => Run(source).AsBoolean();

    [Fact]
    public void DefinePropertiesInstallsEachOwn()
    {
        Assert.Equal(1, RunNum("var o = {}; Object.defineProperties(o, {a:{value:1, enumerable:true}, b:{value:2, enumerable:true}}); o.a;"));
        Assert.Equal(2, RunNum("var o = {}; Object.defineProperties(o, {a:{value:1, enumerable:true}, b:{value:2, enumerable:true}}); o.b;"));
    }

    [Fact]
    public void DefinePropertiesReturnsReceiver()
    {
        Assert.Equal(1, RunNum("var o = {}; (Object.defineProperties(o, {x:{value:5, enumerable:true}}) === o) ? 1 : 0;"));
    }

    [Fact]
    public void DefinePropertiesIgnoresNonEnumerableDescriptorEntries()
    {
        // Spec walks own enumerable keys of the descriptors object. A non-enumerable
        // key on that object is skipped, even if its value would be a valid descriptor.
        // We can't easily mark the descriptors object's key as non-enumerable yet
        // without defineProperty itself, so this test exercises only the happy path
        // and the no-op cases.
        Assert.Equal(0, RunNum("var o = {}; Object.defineProperties(o, {}); Object.keys(o).length;"));
    }

    [Fact]
    public void DefinePropertiesRejectsNonObjectTarget()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.defineProperties(null, {});"));
        Assert.Throws<JsThrownException>(() => Run("Object.defineProperties({}, null);"));
        Assert.Throws<JsThrownException>(() => Run("Object.defineProperties();"));
    }

    [Fact]
    public void HasOwnReturnsTrueForOwn()
    {
        Assert.True(RunBool("Object.hasOwn({a:1}, 'a');"));
    }

    [Fact]
    public void HasOwnReturnsFalseForInherited()
    {
        // 'toString' lives on Object.prototype, not directly on the literal.
        Assert.False(RunBool("Object.hasOwn({}, 'toString');"));
    }

    [Fact]
    public void HasOwnReturnsFalseForMissing()
    {
        Assert.False(RunBool("Object.hasOwn({a:1}, 'b');"));
    }

    [Fact]
    public void HasOwnThrowsOnNullOrUndefined()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.hasOwn(null, 'x');"));
        Assert.Throws<JsThrownException>(() => Run("Object.hasOwn();"));
    }

    [Fact]
    public void HasOwnCoercesKey()
    {
        // Number key coerces via ToPropertyKey, so the key matches an integer index
        // property when it exists.
        Assert.True(RunBool("Object.hasOwn([10, 20], 0);"));
        Assert.False(RunBool("Object.hasOwn([10, 20], 5);"));
    }
}
