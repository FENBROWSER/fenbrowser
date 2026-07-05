using System.Collections.Generic;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Host;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Promises;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class HostObjectIntegrationTests
{
    // Minimal recording IHostHooks - stores host properties in a dictionary keyed by
    // (handle.Index, property). Lets tests assert what the interpreter actually read or
    // wrote without needing a full DOM backend.
    private sealed class RecordingHostHooks : IHostHooks
    {
        public Dictionary<(int Index, string Prop), JsValue> Store { get; } = new();
        public List<(HostObjectHandle Handle, string Prop)> Reads { get; } = new();
        public List<(HostObjectHandle Handle, string Prop, JsValue Value)> Writes { get; } = new();
        public bool RefuseWrites { get; set; }

        public void EnqueuePromiseJob(PromiseJob job) { }
        public void ReportPromiseRejection(JsValue promise, PromiseRejectionOperation operation) { }

        public bool TryGetHostProperty(HostObjectHandle handle, string property, out JsValue value)
        {
            Reads.Add((handle, property));
            return Store.TryGetValue((handle.Index, property), out value);
        }

        public bool TrySetHostProperty(HostObjectHandle handle, string property, JsValue value)
        {
            Writes.Add((handle, property, value));
            if (RefuseWrites) return false;
            Store[(handle.Index, property)] = value;
            return true;
        }

        public JsValue CallHostFunction(int functionId, JsValue thisValue, System.ReadOnlySpan<JsValue> args)
            => JsValue.Undefined;
    }

    private static (BytecodeInterpreter Interpreter, RecordingHostHooks Hooks, HostObjectHandle Handle)
        Setup(int realmId = 0)
    {
        var interpreter = new BytecodeInterpreter();
        var hooks = new RecordingHostHooks();
        interpreter.HostHooks = hooks;
        interpreter.HostResolveContext = new HostObjectResolveContext(
            CurrentRealmId: realmId,
            CurrentDocumentEpoch: DocumentEpoch.Initial,
            CurrentNavigationEpoch: NavigationEpoch.Initial);

        var entry = new HostObjectEntry(
            Generation: 0,
            Kind: HostObjectKind.DomElement,
            RealmId: realmId,
            Origin: "https://example.test",
            DocumentEpoch: DocumentEpoch.Initial,
            NavigationEpoch: NavigationEpoch.Initial,
            FrameId: 0,
            PermissionFlags: 0);
        var handle = interpreter.HostObjectTable.Register(hostObject: new object(), entry);

        interpreter.RegisterGlobalHostObject("myHost", handle);
        return (interpreter, hooks, handle);
    }

    private static JsValue Run(BytecodeInterpreter interpreter, string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return interpreter.Execute(fn);
    }

    [Fact]
    public void HostGlobalIsAddressable()
    {
        var (interpreter, _, _) = Setup();
        Assert.Equal("object", Run(interpreter, "typeof myHost;").AsString());
    }

    [Fact]
    public void ReadMissingPropertyReturnsUndefined()
    {
        var (interpreter, hooks, handle) = Setup();
        var result = Run(interpreter, "myHost.tagName;");
        Assert.Equal(JsValueTag.Undefined, result.Tag);
        Assert.Single(hooks.Reads);
        Assert.Equal(handle, hooks.Reads[0].Handle);
        Assert.Equal("tagName", hooks.Reads[0].Prop);
    }

    [Fact]
    public void ReadReturnsStoredValue()
    {
        var (interpreter, hooks, handle) = Setup();
        hooks.Store[(handle.Index, "tagName")] = JsValue.FromString("DIV");
        Assert.Equal("DIV", Run(interpreter, "myHost.tagName;").AsString());
    }

    [Fact]
    public void WriteRoutesToHostHook()
    {
        var (interpreter, hooks, handle) = Setup();
        _ = Run(interpreter, "myHost.color = 'red';");
        Assert.Single(hooks.Writes);
        Assert.Equal(handle, hooks.Writes[0].Handle);
        Assert.Equal("color", hooks.Writes[0].Prop);
        Assert.Equal("red", hooks.Writes[0].Value.AsString());
    }

    [Fact]
    public void BracketWriteRoutesToHostHook()
    {
        var (interpreter, hooks, handle) = Setup();
        _ = Run(interpreter, "myHost['data-sitekey'] = 'abc';");
        Assert.Single(hooks.Writes);
        Assert.Equal(handle, hooks.Writes[0].Handle);
        Assert.Equal("data-sitekey", hooks.Writes[0].Prop);
        Assert.Equal("abc", hooks.Writes[0].Value.AsString());
    }

    [Fact]
    public void ComputedBracketWriteRoutesToHostHook()
    {
        var (interpreter, hooks, handle) = Setup();
        _ = Run(interpreter, "var key = 'dataset'; myHost[key] = 7;");
        Assert.Single(hooks.Writes);
        Assert.Equal(handle, hooks.Writes[0].Handle);
        Assert.Equal("dataset", hooks.Writes[0].Prop);
        Assert.Equal(7d, hooks.Writes[0].Value.AsNumber());
    }

    [Fact]
    public void ArrayForEachCallReadsHostArrayLikeProperties()
    {
        var (interpreter, hooks, handle) = Setup();
        hooks.Store[(handle.Index, "length")] = JsValue.FromInt32(1);
        hooks.Store[(handle.Index, "0")] = JsValue.FromString("DIV");
        hooks.Store[(handle.Index, "nodeType")] = JsValue.FromInt32(1);

        var result = Run(interpreter, @"
            var seen = '';
            Array.prototype.forEach.call(myHost, function(value, index, receiver) {
                seen = value + ':' + index + ':' + (receiver === myHost);
            });
            seen + ':' + (myHost.nodeType === 1) + ':' + (myHost.nodeType !== 2);
        ");

        Assert.Equal("DIV:0:true:true:true", result.AsString());
        Assert.Contains(hooks.Reads, read => read.Handle.Equals(handle) && read.Prop == "length");
        Assert.Contains(hooks.Reads, read => read.Handle.Equals(handle) && read.Prop == "0");
        Assert.Contains(hooks.Reads, read => read.Handle.Equals(handle) && read.Prop == "nodeType");
    }

    [Fact]
    public void RefusedWriteThrowsTypeError()
    {
        var (interpreter, hooks, _) = Setup();
        hooks.RefuseWrites = true;
        Assert.Throws<JsThrownException>(() => Run(interpreter, "myHost.color = 'red';"));
    }

    [Fact]
    public void StaleGenerationThrowsTypeError()
    {
        var (interpreter, _, handle) = Setup();
        // Free + register reuses the slot at generation+1; old handle is now stale.
        _ = interpreter.HostObjectTable.Free(handle);
        var entry = new HostObjectEntry(0, HostObjectKind.DomElement, 0, "x",
            DocumentEpoch.Initial, NavigationEpoch.Initial, 0, 0);
        _ = interpreter.HostObjectTable.Register(new object(), entry);

        Assert.Throws<JsThrownException>(() => Run(interpreter, "myHost.x;"));
    }

    [Fact]
    public void NavigationEpochAdvanceInvalidatesHandle()
    {
        var (interpreter, _, _) = Setup();
        // Bump the current navigation epoch past the entry's epoch.
        interpreter.HostResolveContext = interpreter.HostResolveContext with
        {
            CurrentNavigationEpoch = NavigationEpoch.Initial.Next(),
        };
        Assert.Throws<JsThrownException>(() => Run(interpreter, "myHost.x;"));
    }

    [Fact]
    public void DocumentEpochAdvanceInvalidatesHandle()
    {
        var (interpreter, _, _) = Setup();
        interpreter.HostResolveContext = interpreter.HostResolveContext with
        {
            CurrentDocumentEpoch = DocumentEpoch.Initial.Next(),
        };
        Assert.Throws<JsThrownException>(() => Run(interpreter, "myHost.x;"));
    }

    [Fact]
    public void CrossRealmAccessThrows()
    {
        var (interpreter, _, _) = Setup(realmId: 5);
        // Flip the interpreter's resolve context to a different realm.
        interpreter.HostResolveContext = interpreter.HostResolveContext with
        {
            CurrentRealmId = 6,
        };
        Assert.Throws<JsThrownException>(() => Run(interpreter, "myHost.x;"));
    }
}
