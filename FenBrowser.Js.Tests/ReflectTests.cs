using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ReflectTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact] public void ReflectHas() => Assert.True(RunBool("Reflect.has({a:1}, 'a');"));
    [Fact] public void ReflectHasMissing() => Assert.False(RunBool("Reflect.has({}, 'a');"));
    [Fact] public void ReflectHasInherited() => Assert.True(RunBool("Reflect.has({}, 'toString');"));

    [Fact] public void ReflectGet() => Assert.Equal(1, RunNum("Reflect.get({a:1}, 'a');"));
    [Fact] public void ReflectGetMissingIsUndefined() => Assert.True(RunBool("Reflect.get({}, 'x') === undefined;"));

    [Fact] public void ReflectSetReturnsTrue() => Assert.True(RunBool("Reflect.set({}, 'a', 1);"));
    [Fact] public void ReflectSetUpdatesProperty()
    {
        Assert.Equal(7, RunNum("var o = {}; Reflect.set(o, 'a', 7); o.a;"));
    }

    [Fact] public void ReflectDeletePropertyReturnsTrue() => Assert.True(RunBool("var o = {a:1}; Reflect.deleteProperty(o, 'a');"));

    [Fact] public void ReflectOwnKeysReturnsArray()
    {
        Assert.Equal(2, RunNum("Reflect.ownKeys({a:1, b:2}).length;"));
    }

    [Fact] public void ReflectGetPrototypeOf()
    {
        Assert.Equal(JsValueTag.Object, RunBool("Reflect.getPrototypeOf({}) !== null;") ? JsValueTag.Object : JsValueTag.Null);
        Assert.True(RunBool("Reflect.getPrototypeOf({}) === Object.prototype;"));
    }

    [Fact] public void ReflectSetPrototypeOf()
    {
        Assert.True(RunBool("var o = {}, p = {x:5}; Reflect.setPrototypeOf(o, p); o.x === 5;"));
    }

    [Fact] public void ReflectSetPrototypeOfThrowsForBadProto()
    {
        // ECMA-262 28.1.13 step 2: a proto that is neither Object nor null is a TypeError
        // (not a false return), matching test262 Reflect/setPrototypeOf/proto-is-*-throws.
        Assert.Throws<JsThrownException>(() => RunBool("Reflect.setPrototypeOf({}, 42);"));
    }

    [Fact] public void ReflectIsExtensibleDefaultTrue() => Assert.True(RunBool("Reflect.isExtensible({});"));

    [Fact] public void ReflectPreventExtensionsBlocksFurtherExtension()
    {
        Assert.False(RunBool("var o = {}; Reflect.preventExtensions(o); Reflect.isExtensible(o);"));
    }

    [Fact] public void ReflectApply()
    {
        Assert.Equal(3, RunNum("Reflect.apply(function(a,b){return a+b;}, null, [1,2]);"));
    }

    [Fact] public void ReflectApplyOnNonFunctionThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("Reflect.apply({}, null, []);"));
    }

    [Fact] public void ReflectGetOwnPropertyDescriptor()
    {
        Assert.Equal(7, RunNum("Reflect.getOwnPropertyDescriptor({a:7}, 'a').value;"));
    }

    [Fact] public void ReflectDefineProperty()
    {
        Assert.True(RunBool("var o = {}; Reflect.defineProperty(o, 'x', {value:9, writable:true, enumerable:true, configurable:true});"));
        Assert.Equal(9, RunNum("var o = {}; Reflect.defineProperty(o, 'x', {value:9, writable:true, enumerable:true, configurable:true}); o.x;"));
    }

    [Fact] public void ReflectMethodsThrowOnNonObjectTarget()
    {
        Assert.Throws<JsThrownException>(() => RunNum("Reflect.has(42, 'a');"));
        Assert.Throws<JsThrownException>(() => RunNum("Reflect.get(null, 'a');"));
        Assert.Throws<JsThrownException>(() => RunNum("Reflect.set('s', 'a', 1);"));
    }

    // ECMA-262 28.1.2 Reflect.construct(target, argumentsList [, newTarget])
    [Fact] public void ReflectConstructSimple()
    {
        Assert.Equal(15, RunNum("function F(x) { this.v = x; } Reflect.construct(F, [15]).v;"));
    }

    [Fact] public void ReflectConstructWithNewTarget()
    {
        Assert.True(RunBool(@"
            function Target(x) { this.value = x; }
            function NewTarget() {}
            NewTarget.prototype = { marker: 42 };
            var constructed = Reflect.construct(Target, [9], NewTarget);
            constructed.value === 9 &&
            Object.getPrototypeOf(constructed) === NewTarget.prototype &&
            constructed.marker === 42;
        "));
    }

    [Fact] public void ReflectConstructNewTargetDefaultsToTarget()
    {
        Assert.True(RunBool(@"
            function F() { this.x = 1; }
            F.prototype = { fromProto: 99 };
            var obj = Reflect.construct(F, []);
            obj.x === 1 && obj.fromProto === 99;
        "));
    }

    [Fact] public void ReflectConstructReturnsInstanceWhenCtorReturnsNonObject()
    {
        Assert.True(RunBool(@"
            function F() { this.tag = 1; return 42; }
            Reflect.construct(F, []).tag === 1;
        "));
    }

    [Fact] public void ReflectConstructReturnsObjectWhenCtorReturnsObject()
    {
        Assert.True(RunBool(@"
            function F() { return { ok: true }; }
            Reflect.construct(F, []).ok === true;
        "));
    }

    [Fact] public void ReflectConstructTargetMustBeObject()
    {
        Assert.Throws<JsThrownException>(() => RunNum("Reflect.construct(42, []);"));
        Assert.Throws<JsThrownException>(() => RunNum("Reflect.construct(null, []);"));
    }
}
