using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ProxyTests
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

    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void ProxyConstructorExists()
    {
        Assert.True(RunBool("typeof Proxy === 'function';"));
    }

    [Fact]
    public void ProxyConstructorRequiresTwoArgs()
    {
        Assert.Throws<JsThrownException>(() => RunBool("new Proxy();"));
    }

    [Fact]
    public void ProxyTargetMustBeObject()
    {
        Assert.Throws<JsThrownException>(() => RunBool("new Proxy(42, {});"));
    }

    [Fact]
    public void ProxyHandlerMustBeObject()
    {
        Assert.Throws<JsThrownException>(() => RunBool("new Proxy({}, 42);"));
    }

    [Fact]
    public void ProxyWithoutTrapsForwardsGet()
    {
        Assert.True(RunBool(@"
            var target = { x: 42 };
            var p = new Proxy(target, {});
            p.x === 42;
        "));
    }

    [Fact]
    public void ProxyWithoutTrapsForwardsSet()
    {
        Assert.True(RunBool(@"
            var target = {};
            var p = new Proxy(target, {});
            p.a = 10;
            target.a === 10;
        "));
    }

    [Fact]
    public void ProxyWithoutTrapsForwardsHas()
    {
        Assert.True(RunBool(@"
            var target = { a: 1 };
            var p = new Proxy(target, {});
            'a' in p && !('b' in p);
        "));
    }

    [Fact]
    public void ProxyWithoutTrapsForwardsDelete()
    {
        Assert.True(RunBool(@"
            var target = { a: 1, b: 2 };
            var p = new Proxy(target, {});
            delete p.a;
            !target.hasOwnProperty('a') && target.b === 2;
        "));
    }

    [Fact]
    public void ProxyWithoutTrapsForwardsFunctionCall()
    {
        Assert.Equal(15, RunNum(@"
            var target = function(a, b) { return a + b; };
            var p = new Proxy(target, {});
            p(5, 10);
        "));
    }

    [Fact]
    public void ProxyWithoutTrapsForwardsConstruct()
    {
        Assert.True(RunBool(@"
            function Target(x) { this.value = x; }
            var p = new Proxy(Target, {});
            var inst = new p(7);
            inst.value === 7;
        "));
    }

    [Fact]
    public void ProxyGetTrapIntercepts()
    {
        Assert.True(RunBool(@"
            var target = { a: 1 };
            var handler = {
                get: function(t, prop, receiver) { return 42; }
            };
            var p = new Proxy(target, handler);
            p.a === 42;
        "));
    }

    [Fact]
    public void ProxySetTrapIntercepts()
    {
        Assert.True(RunBool(@"
            var target = {};
            var handler = {
                set: function(t, prop, value, receiver) { t[prop] = value + 1; return true; }
            };
            var p = new Proxy(target, handler);
            p.x = 5;
            target.x === 6;
        "));
    }

    [Fact]
    public void ProxyHasTrapIntercepts()
    {
        Assert.True(RunBool(@"
            var target = {};
            var handler = {
                has: function(t, prop) { return prop === 'magic'; }
            };
            var p = new Proxy(target, handler);
            'magic' in p && !('other' in p);
        "));
    }

    [Fact]
    public void ProxyDeletePropertyTrapIntercepts()
    {
        Assert.True(RunBool(@"
            var target = { a: 1 };
            var handler = {
                deleteProperty: function(t, prop) { return prop === 'a'; }
            };
            var p = new Proxy(target, handler);
            delete p.a;
        "));
    }

    [Fact]
    public void ProxyApplyTrapIntercepts()
    {
        Assert.True(RunBool(@"
            var target = function(x) { return x; };
            var handler = {
                apply: function(t, thisArg, args) { return args[0] + 100; }
            };
            var p = new Proxy(target, handler);
            p(5) === 105;
        "));
    }

    [Fact]
    public void ProxyConstructTrapIntercepts()
    {
        Assert.True(RunBool(@"
            var target = function(x) { this.value = x; };
            var handler = {
                construct: function(t, args) { return { ok: true, val: args[0] }; }
            };
            var p = new Proxy(target, handler);
            var inst = new p(42);
            inst.ok === true && inst.val === 42;
        "));
    }

    [Fact]
    public void ProxyConstructorPrototypeIsFunctionPrototype()
    {
        Assert.True(RunBool(@"
            Object.getPrototypeOf(Proxy) === Function.prototype;
        "));
    }

    [Fact]
    public void ProxyApplyForwardsWhenTrapMissingOnNestedProxy()
    {
        Assert.True(RunBool(@"
            var hasOwn = Object.prototype.hasOwnProperty;
            var hasOwnTarget = new Proxy(hasOwn, {});
            var hasOwnProxy = new Proxy(hasOwnTarget, {});
            var obj = { foo: 1 };
            hasOwnProxy.call(obj, 'foo') && !Reflect.apply(hasOwnProxy, obj, ['bar']);
        "));
    }

    [Fact]
    public void ProxyConstructForwardsWhenTrapMissingOnNestedProxy()
    {
        Assert.Equal(0, RunNum(@"
            var ArrayTarget = new Proxy(Array, {});
            var ArrayProxy = new Proxy(ArrayTarget, {});

            var array = new ArrayProxy(1, 2, 3);
            var ok1 = Array.isArray(array);
            var ok2 = array.length === 3 && array[0] === 1 && array[1] === 2 && array[2] === 3;

            class MyArray extends Array {
              get isMyArray() { return true; }
            }
            var myArray = Reflect.construct(ArrayProxy, [], MyArray);
            var ok3 = Array.isArray(myArray);
            var ok4 = myArray instanceof MyArray;
            var ok5 = myArray.isMyArray === true;

            if (!ok1) 1;
            else if (!ok2) 2;
            else if (!ok3) 3;
            else if (!ok4) 4;
            else if (!ok5) 5;
            else 0;
        "));
    }

    [Fact]
    public void ProxyRevocableReturnsObjectWithProxyAndRevoke()
    {
        Assert.True(RunBool(@"
            var r = Proxy.revocable({x: 1}, {});
            typeof r.proxy === 'object' && typeof r.revoke === 'function';
        "));
    }

    [Fact]
    public void ProxyRevocableGetTrapFiresBeforeRevoke()
    {
        Assert.True(RunBool(@"
            var trapped = false;
            var r = Proxy.revocable({}, {
                get: function(t, k) { trapped = true; return 42; }
            });
            var v = r.proxy.foo;
            trapped && v === 42;
        "));
    }

    [Fact]
    public void ProxyRevocableThrowsAfterRevoke()
    {
        Assert.True(RunBool(@"
            var r = Proxy.revocable({}, { get: function() { return 1; } });
            r.revoke();
            var threw = false;
            try { var v = r.proxy.foo; } catch (e) { threw = e instanceof TypeError; }
            threw;
        "));
    }

    [Fact]
    public void ProxyDoubleRevokeIsNoop()
    {
        Assert.True(RunBool(@"
            var r = Proxy.revocable({}, {});
            r.revoke();
            var ok = true;
            try { r.revoke(); } catch (e) { ok = false; }
            ok;
        "));
    }

    [Fact]
    public void ProxyGetTrapReceiversAreCorrect()
    {
        Assert.True(RunBool(@"
            var observed = {};
            var target = { attr: 1 };
            var handler = {
                get: function(t, prop, receiver) {
                    observed.t = t;
                    observed.prop = prop;
                    observed.receiver = receiver;
                    return 99;
                }
            };
            var p = new Proxy(target, handler);
            var val = p.attr;
            val === 99 && observed.t === target && observed.prop === 'attr' &&
            observed.receiver === p;
        "));
    }

    [Fact]
    public void ProxySetTrapThisBindingIsHandler()
    {
        Assert.True(RunBool(@"
            var handlerThis;
            var handler = {
                set: function(t, p, v, r) { handlerThis = this; return true; }
            };
            var p = new Proxy({}, handler);
            p.x = 1;
            handlerThis === handler;
        "));
    }

    [Fact]
    public void ProxyGetPrototypeOfTrapIntercepts()
    {
        Assert.True(RunBool(@"
            var proto = { marker: 1 };
            var p = new Proxy({}, {
                getPrototypeOf: function() { return proto; }
            });
            Object.getPrototypeOf(p) === proto && Reflect.getPrototypeOf(p) === proto;
        "));
    }

    [Fact]
    public void ProxySetPrototypeOfTrapIntercepts()
    {
        Assert.True(RunBool(@"
            var hit = 0;
            var p = new Proxy({}, {
                setPrototypeOf: function(target, proto) { hit++; return true; }
            });
            var ok = Reflect.setPrototypeOf(p, {});
            ok && hit === 1;
        "));
    }

    [Fact]
    public void ProxyIsExtensibleAndPreventExtensionsTrapsIntercept()
    {
        Assert.True(RunBool(@"
            var isHit = 0;
            var preventHit = 0;
            var target = {};
            Object.preventExtensions(target);
            var p = new Proxy(target, {
                isExtensible: function() { isHit++; return false; },
                preventExtensions: function() { preventHit++; return true; }
            });
            Reflect.isExtensible(p) === false &&
            Reflect.preventExtensions(p) === true &&
            Object.isExtensible(p) === false &&
            (Object.preventExtensions(p), true) &&
            isHit === 2 && preventHit === 2;
        "));
    }

    [Fact]
    public void ProxyOwnKeysTrapIntercepts()
    {
        Assert.True(RunBool(@"
            var p = new Proxy({}, {
                ownKeys: function() { return ['b', 'a']; }
            });
            var keys = Reflect.ownKeys(p);
            keys.length === 2 && keys[0] === 'b' && keys[1] === 'a';
        "));
    }

    [Fact]
    public void ProxyGetOwnPropertyDescriptorTrapIntercepts()
    {
        Assert.True(RunBool(@"
            var p = new Proxy({}, {
                getOwnPropertyDescriptor: function(target, key) {
                    if (key === 'x') return { value: 7, writable: true, enumerable: true, configurable: true };
                    return undefined;
                }
            });
            var desc = Object.getOwnPropertyDescriptor(p, 'x');
            var desc2 = Reflect.getOwnPropertyDescriptor(p, 'x');
            desc.value === 7 && desc2.value === 7;
        "));
    }

    [Fact]
    public void ProxyDefinePropertyTrapIntercepts()
    {
        Assert.True(RunBool(@"
            var hit = 0;
            var p = new Proxy({}, {
                defineProperty: function(target, key, desc) {
                    hit++;
                    return key === 'x' && desc.value === 9;
                }
            });
            Reflect.defineProperty(p, 'x', { value: 9, configurable: true, enumerable: true, writable: true }) &&
            (Object.defineProperty(p, 'x', { value: 9, configurable: true, enumerable: true, writable: true }), true) &&
            hit === 2;
        "));
    }
}
