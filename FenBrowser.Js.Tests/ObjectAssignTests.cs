using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ObjectAssignTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void CopiesEnumerablePropertiesFromSingleSource()
    {
        Assert.Equal(3, Run("var t = {}; Object.assign(t, {a:1, b:2, c:3}); t.c;").AsNumber());
    }

    [Fact]
    public void MultipleSourcesAreAppliedLeftToRight()
    {
        Assert.Equal(20, Run("var t = {}; Object.assign(t, {x:10}, {x:20}); t.x;").AsNumber());
    }

    [Fact]
    public void IgnoresNullAndUndefinedSources()
    {
        Assert.Equal(1, Run("var t = {a:1}; Object.assign(t, null, undefined, {b:2}); t.a;").AsNumber());
        Assert.Equal(2, Run("var t = {a:1}; Object.assign(t, null, undefined, {b:2}); t.b;").AsNumber());
    }

    [Fact]
    public void ReturnsTarget()
    {
        Assert.Equal(5, Run("var t = {}; Object.assign(t, {z:5}).z;").AsNumber());
    }

    [Fact]
    public void ThrowsWhenTargetIsUndefinedOrNull()
    {
        Assert.Throws<JsThrownException>(() => Run("Object.assign(undefined, {a:1});"));
        Assert.Throws<JsThrownException>(() => Run("Object.assign(null);"));
        Assert.Throws<JsThrownException>(() => Run("Object.assign();"));
    }

    [Fact]
    public void DoesNotCopyAcrossPrototype()
    {
        // Prototype-inherited property must not be copied.
        Assert.Equal(JsValueTag.Undefined,
            Run("var p = {inherited:9}; var s = Object.create(p); s.own = 1; var t = {}; Object.assign(t, s); t.inherited;").Tag);
    }

    [Fact]
    public void PrimitiveTargetCoerces()
    {
        // 20.1.2.1 step 1: ToObject(target). Primitives box - but the box is
        // discarded, so callers cannot observe the assigned properties on the original
        // primitive. The function still returns the (coerced) target rather than
        // throwing.
        Assert.Equal(JsValueTag.Number, Run("Object.assign(42, {a:1});").Tag);
    }
}
