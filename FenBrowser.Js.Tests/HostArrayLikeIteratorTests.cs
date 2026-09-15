using System.Collections.Generic;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Host;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 23.1.5.1 CreateArrayIterator accepts any object, and a host object (a DOM
// collection such as NodeList) is an object. Array.prototype.values/keys/entries threw
// "called on non-object receiver" for one, which is what NodeList.prototype.values is,
// and it broke Cloudflare Turnstile's message handler on bing.com.
public sealed class HostArrayLikeIteratorTests
{
    private sealed class ArrayLikeHostHooks : IHostHooks
    {
        public Dictionary<(int Index, string Prop), JsValue> Store { get; } = new();

        public void EnqueuePromiseJob(PromiseJob job) { }
        public void ReportPromiseRejection(JsValue promise, PromiseRejectionOperation operation) { }

        public bool TryGetHostProperty(HostObjectHandle handle, string property, out JsValue value)
            => Store.TryGetValue((handle.Index, property), out value);

        public bool TrySetHostProperty(HostObjectHandle handle, string property, JsValue value)
        {
            Store[(handle.Index, property)] = value;
            return true;
        }

        public bool TryConvertHostObjectToPrimitive(HostObjectHandle handle, string hint, out JsValue value)
        {
            value = JsValue.Undefined;
            return false;
        }

        public JsValue CallHostFunction(int functionId, JsValue thisValue, System.ReadOnlySpan<JsValue> args)
            => JsValue.Undefined;
    }

    private static (BytecodeInterpreter Interpreter, ArrayLikeHostHooks Hooks) Setup()
    {
        var interpreter = new BytecodeInterpreter();
        var hooks = new ArrayLikeHostHooks();
        interpreter.HostHooks = hooks;
        interpreter.HostResolveContext = new HostObjectResolveContext(
            CurrentRealmId: 0,
            CurrentDocumentEpoch: DocumentEpoch.Initial,
            CurrentNavigationEpoch: NavigationEpoch.Initial);

        var entry = new HostObjectEntry(
            Generation: 0,
            Kind: HostObjectKind.Other,
            RealmId: 0,
            Origin: "https://example.test",
            DocumentEpoch: DocumentEpoch.Initial,
            NavigationEpoch: NavigationEpoch.Initial,
            FrameId: 0,
            PermissionFlags: 0);
        var handle = interpreter.HostObjectTable.Register(hostObject: new object(), entry);
        hooks.Store[(handle.Index, "length")] = JsValue.FromInt32(2);
        hooks.Store[(handle.Index, "0")] = JsValue.FromString("a");
        hooks.Store[(handle.Index, "1")] = JsValue.FromString("b");
        interpreter.RegisterGlobalHostObject("hostList", handle);
        return (interpreter, hooks);
    }

    private static string Run(BytecodeInterpreter interpreter, string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return interpreter.Execute(fn).AsString();
    }

    [Fact]
    public void ValuesIteratesAHostArrayLike()
    {
        var (interpreter, _) = Setup();

        Assert.Equal("a,b", Run(interpreter,
            "var out = []; for (var v of Array.prototype.values.call(hostList)) out.push(v); out.join(',');"));
    }

    [Fact]
    public void KeysAndEntriesIterateAHostArrayLike()
    {
        var (interpreter, _) = Setup();

        Assert.Equal("0,1", Run(interpreter,
            "var out = []; for (var k of Array.prototype.keys.call(hostList)) out.push(k); out.join(',');"));
        Assert.Equal("0:a|1:b", Run(interpreter,
            "var out = []; for (var e of Array.prototype.entries.call(hostList)) out.push(e[0] + ':' + e[1]); out.join('|');"));
    }

    [Fact]
    public void IteratorReadsTheLengthOnEveryStep()
    {
        var (interpreter, hooks) = Setup();

        Assert.Equal("a|done", Run(interpreter,
            "var it = Array.prototype.values.call(hostList); var first = it.next().value;" +
            "hostList.length = 1;" +
            "first + '|' + (it.next().done ? 'done' : 'more');"));
        Assert.NotNull(hooks);
    }
}
