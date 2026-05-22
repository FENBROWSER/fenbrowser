using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ObjectIntegrityTests
{
    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact]
    public void NewObjectIsExtensible() => Assert.True(RunBool("Object.isExtensible({});"));

    [Fact]
    public void PreventExtensionsFlipsExtensibility()
    {
        Assert.False(RunBool("var o = {}; Object.preventExtensions(o); Object.isExtensible(o);"));
    }

    [Fact]
    public void PrimitivesAreNotExtensible()
    {
        Assert.False(RunBool("Object.isExtensible(42);"));
        Assert.False(RunBool("Object.isExtensible('s');"));
    }

    [Fact]
    public void FreezeMakesIsFrozenTrue()
    {
        Assert.True(RunBool("var o = {a:1}; Object.freeze(o); Object.isFrozen(o);"));
    }

    [Fact]
    public void FreezeImpliesIsSealed()
    {
        Assert.True(RunBool("var o = {a:1}; Object.freeze(o); Object.isSealed(o);"));
    }

    [Fact]
    public void FreezeImpliesNotExtensible()
    {
        Assert.False(RunBool("var o = {}; Object.freeze(o); Object.isExtensible(o);"));
    }

    [Fact]
    public void FrozenPlainObjectIsNotEditable()
    {
        // After freeze, attempting to mutate the value should leave the original
        // intact (sloppy-mode SetProperty returns false, no throw in our current path).
        Assert.True(RunBool("var o = {a:1}; Object.freeze(o); o.a = 2; o.a === 1;"));
    }

    [Fact]
    public void SealMakesIsSealedTrueButNotFrozen()
    {
        Assert.True(RunBool("var o = {a:1}; Object.seal(o); Object.isSealed(o);"));
        Assert.False(RunBool("var o = {a:1}; Object.seal(o); Object.isFrozen(o);"));
    }

    [Fact]
    public void SealedObjectStillAllowsValueChange()
    {
        // seal keeps writable=true (only configurable goes to false), so existing
        // own data properties remain mutable.
        Assert.True(RunBool("var o = {a:1}; Object.seal(o); o.a = 5; o.a === 5;"));
    }

    [Fact]
    public void PrimitivesAreFrozenAndSealedVacuously()
    {
        Assert.True(RunBool("Object.isFrozen(42);"));
        Assert.True(RunBool("Object.isSealed(42);"));
        Assert.True(RunBool("Object.isFrozen(null);"));
    }

    [Fact]
    public void EmptyExtensibleObjectIsNotFrozenOrSealed()
    {
        Assert.False(RunBool("Object.isFrozen({});"));
        Assert.False(RunBool("Object.isSealed({});"));
    }

    [Fact]
    public void EmptyObjectAfterPreventExtensionsIsBothSealedAndFrozen()
    {
        // No own properties to violate the "all non-configurable / non-writable"
        // postconditions, so a non-extensible empty object is both sealed and frozen.
        Assert.True(RunBool("var o = {}; Object.preventExtensions(o); Object.isSealed(o);"));
        Assert.True(RunBool("var o = {}; Object.preventExtensions(o); Object.isFrozen(o);"));
    }
}
