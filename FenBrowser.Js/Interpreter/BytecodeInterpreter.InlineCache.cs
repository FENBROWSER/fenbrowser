using System.Runtime.CompilerServices;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Jit.CacheIR;
using FenBrowser.Js.Jit.CacheIR.Attachers;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// Plan §31: inline cache helpers. Data-property-only fast path for now.
public sealed partial class BytecodeInterpreter
{
    // A site reading a fixed name has its key in the bytecode, so its programs
    // guard the shape alone; obj[k] sites guard the key as well.
    private bool TryRunLoadSite(BytecodeFunction fn, int offset, JsValue receiver, string key, out JsValue result)
    {
        var sites = fn.LoadCacheSites;
        if (receiver.Tag != JsValueTag.Object || sites is null ||
            (uint)offset >= (uint)sites.Length || sites[offset] is not { } site)
        { result = JsValue.Undefined; return false; }

        return site.TryRun(_heap.GetObject(receiver.AsObjectHandle()), key, out result);
    }

    private void AttachLoadSite(BytecodeFunction fn, int offset, JsValue receiver, string key, bool keyVariesAtSite)
    {
        if (receiver.Tag != JsValueTag.Object) return;

        var sites = fn.EnsureLoadCacheSites();
        if ((uint)offset >= (uint)sites.Length) return;

        var site = sites[offset] ??= new CacheIRSite();
        if (site.IsMegamorphic) return;

        var program = LoadPropertyAttacher.TryAttach(
            _heap.GetObject(receiver.AsObjectHandle()), key, keyVariesAtSite);
        if (program is not null) site.Attach(program);
    }

    private bool TryGetLoadIC(BytecodeFunction fn, int offset, JsValue receiver, string key, out JsValue result)
        => TryRunLoadSite(fn, offset, receiver, key, out result);

    private void PopulateLoadIC(BytecodeFunction fn, int offset, JsValue receiver, string key)
        => AttachLoadSite(fn, offset, receiver, key, keyVariesAtSite: false);

    // Store IC fast path: receiver is a plain object whose current shape carries
    // `key` as a writable, non-accessor own data property. Updates the slot in
    // place without walking the prototype chain. Returns false if the IC misses
    // (caller must take the slow [[Set]] path) or if the cached slot has been
    // invalidated (made non-writable, deleted, or turned into an accessor).
    private bool TryStoreIC(BytecodeFunction fn, int offset, ObjectHandle ownerHandle, JsValue receiver, string key, JsValue value)
    {
        var sites = fn.StoreCacheSites;
        if (receiver.Tag != JsValueTag.Object || sites is null ||
            (uint)offset >= (uint)sites.Length || sites[offset] is not { } site)
            return false;

        var obj = _heap.GetObject(receiver.AsObjectHandle());
        if (!site.TryResolveStore(obj, key, out var slot)) return false;

        CommitCachedStore(obj, ownerHandle, slot, key, value);
        return true;
    }

    /// <summary>
    /// The object a cached load may read from: one that does not route property
    /// access through a handler. Null for everything else, so a compiled guard
    /// turns those away on a single test.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JsObject? CacheableLoadReceiver(JsValue receiver)
    {
        if (receiver.Tag != JsValueTag.Object) return null;
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        return obj is ProxyObject or ModuleNamespaceObject ? null : obj;
    }

    /// <summary>
    /// The store counterpart. A module namespace refuses every write, which the
    /// general path already reports, so only a proxy has to be turned away here.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal JsObject? CacheableStoreReceiver(JsValue receiver)
    {
        if (receiver.Tag != JsValueTag.Object) return null;
        var obj = _heap.GetObject(receiver.AsObjectHandle());
        return obj is ProxyObject ? null : obj;
    }

    /// <summary>The barrier a compiled store takes after writing a slot itself.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CachedStoreBarrier(JsValue receiver, JsValue value)
    {
        if (value.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(receiver.AsObjectHandle(), value.AsObjectHandle());
        }
    }

    /// <summary>
    /// Performs a store the cache has already proved safe. The write, its
    /// barrier and the prototype-assignment bookkeeping live here rather than in
    /// the cache program, which knows nothing of the heap.
    /// </summary>
    private void CommitCachedStore(JsObject obj, ObjectHandle ownerHandle, int slot, string key, JsValue value)
    {
        obj.WriteDataSlot(slot, value);
        if (value.Tag == JsValueTag.Object) _heap.WriteBarrier(ownerHandle, value.AsObjectHandle());
        MarkFunctionInstancePrototypeAssignment(obj, key, value);
    }

    private void PopulateStoreIC(BytecodeFunction fn, int offset, JsValue receiver, string key)
    {
        if (receiver.Tag != JsValueTag.Object) return;

        var sites = fn.EnsureStoreCacheSites();
        if ((uint)offset >= (uint)sites.Length) return;

        var site = sites[offset] ??= new CacheIRSite();
        if (site.IsMegamorphic) return;

        var obj = _heap.GetObject(receiver.AsObjectHandle());
        if (site.Covers(obj.CurrentShape, key)) return;

        var program = StorePropertyAttacher.TryAttach(obj, key, keyVariesAtSite: false);
        if (program is not null) site.Attach(program);
    }

    // Tier 4 #20 GetElem IC: same shape/key/slot lookup as the LoadIC, but
    // keyed off the GetElem instruction offset and only consulted when the
    // key value is a String at runtime (the common `obj["foo"]` case).
    // Integer-indexed array access remains on the slow path.
    private bool TryGetElemStringIC(BytecodeFunction fn, int offset, JsValue receiver, string key, out JsValue result)
        => TryRunLoadSite(fn, offset, receiver, key, out result);

    private void PopulateGetElemStringIC(BytecodeFunction fn, int offset, JsValue receiver, string key)
        => AttachLoadSite(fn, offset, receiver, key, keyVariesAtSite: true);

    // Tier 4 #20 Call IC: monomorphic cache of the resolved callee handle at
    // each call site. On a hit the dispatch path is fixed (NativeFunction,
    // JsFunction, BoundFunction, Proxy) so the type-discrimination cascade in
    // CallFunction is skipped. Bound functions still need to merge args, so
    // they take the slow path; only NativeFunction and ordinary
    // JsFunction monomorphic call sites benefit.
    private bool TryDispatchCallIC(
        BytecodeFunction fn, int offset, JsValue callee, in CallArgs args, JsValue thisValue, out JsValue result)
    {
        result = JsValue.Undefined;
        if (callee.Tag != JsValueTag.Object) return false;
        var caches = fn.CallICs;
        if (caches is null || (uint)offset >= (uint)caches.Length ||
            caches[offset] is not { } entry) return false;
        if (entry.Megamorphic) return false;

        var handle = callee.AsObjectHandle().ToInt64();
        if (entry.CalleeHandle != handle) return false;

        var obj = _heap.GetObject(callee.AsObjectHandle());
        switch (entry.Kind)
        {
            case CallICKind.Native:
                if (obj is not NativeFunctionObject) { entry.Megamorphic = true; return false; }
                entry.Hits++;
                result = CallFunction(callee, args, thisValue);
                return true;
            case CallICKind.OrdinaryFunction:
                if (obj is not JsFunctionObject jfn ||
                    jfn.Kind != Objects.FunctionKind.Ordinary)
                { entry.Megamorphic = true; return false; }
                entry.Hits++;
                result = CallOrdinaryFunctionFast(jfn, args, thisValue);
                return true;
            default:
                return false;
        }
    }

    private JsValue CallOrdinaryFunctionFast(JsFunctionObject fn, in CallArgs args, JsValue thisValue)
    {
        var bcFn = fn.Function;

        // The monomorphic call cache is a second door into an ordinary function
        // body, so the register-window loop has to be reachable through it too -
        // otherwise a call site would run one loop before it warmed up and the
        // other afterwards.
        if (Interpreter2.Interp2Options.Enabled)
        {
            var layout = Interpreter2.FrameLayout.For(bcFn);
            if (layout.Eligible)
            {
                return Interp2Execute(fn, layout, args, thisValue);
            }
        }

        bcFn.Invocations++;
#if !PUBLISH_AOT
        const int BackEdgeScale = 100;
        if (!bcFn.JitCompileAttempted &&
            (long)bcFn.Invocations * BackEdgeScale + bcFn.BackEdges
                >= (long)JitCompiler.TierUpThreshold * BackEdgeScale)
        {
            bcFn.JitCompileAttempted = true;
            JitCompiler.RequestCompile(bcFn);
        }
#endif
        return ExecuteInternal(bcFn, args, thisValue, ResolveFunctionOuterEnvironment(fn), callee: fn);
    }

    private void PopulateCallIC(BytecodeFunction fn, int offset, JsValue callee)
    {
        if (callee.Tag != JsValueTag.Object) return;
        var obj = _heap.GetObject(callee.AsObjectHandle());
        CallICKind kind;
        if (obj is NativeFunctionObject) kind = CallICKind.Native;
        else if (obj is JsFunctionObject f && f.Kind == Objects.FunctionKind.Ordinary) kind = CallICKind.OrdinaryFunction;
        else return; // Proxy, bound, async, generator — not cached.

        var caches = fn.EnsureCallICs();
        if ((uint)offset >= (uint)caches.Length) return;
        var handle = callee.AsObjectHandle().ToInt64();
        if (caches[offset] is { } entry)
        {
            if (entry.CalleeHandle != handle) entry.Megamorphic = true;
            return;
        }

        caches[offset] = new CallICEntry { CalleeHandle = handle, Kind = kind };
    }
}
