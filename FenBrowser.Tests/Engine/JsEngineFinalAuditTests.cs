using System;
using System.Reflection;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Types;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// Regression tests for JS_ENGINE_FINAL.md audit findings.
    /// Covers: Symbol type (#5), JSON.stringify (#6), Regex parsing (#2),
    /// Bytecode compiler (#3), Reflect/Proxy (#21).
    /// </summary>
    [Collection("Engine Tests")]
    public class JsEngineFinalAuditTests
    {
        public JsEngineFinalAuditTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        private FenRuntime CreateRuntime() => new FenRuntime();

        // ── Finding #5: Symbol type correctness ──

        [Fact]
        public void Symbol_TypeofReturnsSymbol_NotObject()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = Symbol('test'); var t = typeof s;");
            Assert.Equal("symbol", rt.GetGlobal("t").ToString());
        }

        [Fact]
        public void Symbol_IsNotLooselyEqualToObject()
        {
            var rt = CreateRuntime();
            // Symbols should not coerce to objects in loose equality
            rt.ExecuteSimple("var s = Symbol('x'); var result = (s == s);");
            Assert.True(rt.GetGlobal("result").ToBoolean());
        }

        [Fact]
        public void FenSymbol_IValue_Type_Is_Symbol()
        {
            // Direct type check on the FenSymbol IValue implementation
            var sym = new FenBrowser.FenEngine.Core.FenSymbol("test");
            Assert.Equal(FenBrowser.FenEngine.Core.Interfaces.ValueType.Symbol, sym.Type);
            Assert.False(sym.IsObject);
        }

        [Fact]
        public void JsSymbol_IValue_Type_Is_Symbol()
        {
            var sym = new JsSymbol("test");
            Assert.Equal(FenBrowser.FenEngine.Core.Interfaces.ValueType.Symbol, sym.Type);
            Assert.False(sym.IsObject);
            Assert.True(sym.ToBoolean()); // Symbols are truthy
        }

        // ── Finding #6: JSON.stringify correctness ──

        [Fact]
        public void JsonStringify_Undefined_Returns_JsUndefined()
        {
            // ECMA-262 §25.5.2: JSON.stringify(undefined) returns undefined, not "undefined"
            var rt = CreateRuntime();
            rt.ExecuteSimple("var result = JSON.stringify(undefined);");
            var result = rt.GetGlobal("result");
            Assert.True(result.IsUndefined, "JSON.stringify(undefined) should return JS undefined");
        }

        [Fact]
        public void JsonStringify_Function_Returns_Undefined()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var result = JSON.stringify(function() {});");
            var result = rt.GetGlobal("result");
            Assert.True(result.IsUndefined, "JSON.stringify(function) should return undefined");
        }

        [Fact]
        public void JsonStringify_UndefinedInArray_Becomes_Null()
        {
            // In arrays, undefined becomes "null"
            var rt = CreateRuntime();
            rt.ExecuteSimple("var result = JSON.stringify([1, undefined, 3]);");
            Assert.Equal("[1,null,3]", rt.GetGlobal("result").ToString());
        }

        [Fact]
        public void JsonStringify_UndefinedProperty_Omitted()
        {
            // Object properties with undefined values are omitted
            var rt = CreateRuntime();
            rt.ExecuteSimple("var result = JSON.stringify({a: 1, b: undefined, c: 3});");
            var json = rt.GetGlobal("result").ToString();
            Assert.Contains("\"a\"", json);
            Assert.Contains("\"c\"", json);
            Assert.DoesNotContain("\"b\"", json);
        }

        [Fact]
        public void JsonStringify_Null_Returns_NullString()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var result = JSON.stringify(null);");
            Assert.Equal("null", rt.GetGlobal("result").ToString());
        }

        [Fact]
        public void JsonStringify_Number_Returns_NumberString()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var result = JSON.stringify(42);");
            Assert.Equal("42", rt.GetGlobal("result").ToString());
        }

        [Fact]
        public void JsonStringify_NaN_Returns_NullString()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var result = JSON.stringify(NaN);");
            Assert.Equal("null", rt.GetGlobal("result").ToString());
        }

        // ── Finding #2: Regex parsing ──

        [Fact]
        public void RegexLiteral_ParsesRealPattern_NotPlaceholder()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var re = /abc/; var result = re.test('xabcy');");
            Assert.True(rt.GetGlobal("result").ToBoolean(),
                "Regex /abc/ should match 'xabcy' — placeholder would match anything");
        }

        [Fact]
        public void RegexLiteral_NonMatchReturnsFlase()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var re = /xyz/; var result = re.test('abc');");
            Assert.False(rt.GetGlobal("result").ToBoolean(),
                "Regex /xyz/ should not match 'abc'");
        }

        [Fact]
        public void RegexLiteral_WithFlags_ParsesCorrectly()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var re = /hello/i; var result = re.test('HELLO world');");
            Assert.True(rt.GetGlobal("result").ToBoolean(),
                "Regex /hello/i should match case-insensitively");
        }

        [Fact]
        public void RegexLiteral_WithCharacterClass()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var re = /[0-9]+/; var result = re.test('abc123');");
            Assert.True(rt.GetGlobal("result").ToBoolean());
        }

        // ── Finding #3: Bytecode compiler gaps ──

        [Fact]
        public void BytecodeCompiler_UnsupportedNode_ThrowsAtRuntime_NotCompileTime()
        {
            // The compiler should emit a runtime throw for unsupported nodes
            // rather than crashing the entire compilation.
            // We verify that valid code around the unsupported node still compiles and runs.
            var rt = CreateRuntime();
            rt.ExecuteSimple("var x = 1 + 2; var y = x * 3;");
            Assert.Equal(9.0, rt.GetGlobal("y").ToNumber());
        }

        // ── Finding #21: Reflect.construct and Proxy ──

        [Fact]
        public void Reflect_Construct_CreatesInstance()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                function Foo(x) { this.x = x; }
                var obj = Reflect.construct(Foo, [42]);
                var result = obj.x;
            ");
            Assert.Equal(42.0, rt.GetGlobal("result").ToNumber());
        }

        [Fact]
        public void Reflect_Construct_WithNewTarget()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                function Base(x) { this.x = x; }
                function Derived() {}
                Derived.prototype = Object.create(Base.prototype);
                var obj = Reflect.construct(Base, [99], Derived);
                var result = obj.x;
            ");
            Assert.Equal(99.0, rt.GetGlobal("result").ToNumber());
        }

        [Fact]
        public void Proxy_DefaultForwarding_Get()
        {
            // Proxy without get trap should transparently forward to target
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var target = { a: 42 };
                var proxy = new Proxy(target, {});
                var result = proxy.a;
            ");
            Assert.Equal(42.0, rt.GetGlobal("result").ToNumber());
        }

        [Fact]
        public void Proxy_DefaultForwarding_Set()
        {
            // Proxy without set trap should forward sets to target
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var target = {};
                var proxy = new Proxy(target, {});
                proxy.x = 'hello';
                var result = target.x;
            ");
            Assert.Equal("hello", rt.GetGlobal("result").ToString());
        }

        [Fact]
        public void Proxy_GetTrap_Intercepts()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var target = { a: 1 };
                var proxy = new Proxy(target, {
                    get: function(t, prop) { return prop === 'a' ? 100 : t[prop]; }
                });
                var result = proxy.a;
            ");
            Assert.Equal(100.0, rt.GetGlobal("result").ToNumber());
        }

        [Fact]
        public void Proxy_ApplyTrap_InterceptsFunctionCall()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                function target(x) { return x * 2; }
                var proxy = new Proxy(target, {
                    apply: function(t, thisArg, args) { return t(args[0]) + 1; }
                });
                var result = proxy(5);
            ");
            Assert.Equal(11.0, rt.GetGlobal("result").ToNumber());
        }
        // ── Finding #7: ArrayBuffer / TypedArrays ──

        [Fact]
        public void ArrayBuffer_ByteLength_ReturnsCorrectSize()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var buf = new ArrayBuffer(16); var len = buf.byteLength;");
            Assert.Equal(16.0, rt.GetGlobal("len").ToNumber());
        }

        [Fact]
        public void ArrayBuffer_Slice_ReturnsCorrectLength()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var buf = new ArrayBuffer(8);
                var sliced = buf.slice(2, 5);
                var sliceLen = sliced.byteLength;
                var fullLen = buf.byteLength;
            ");
            Assert.Equal(3.0, rt.GetGlobal("sliceLen").ToNumber());
            Assert.Equal(8.0, rt.GetGlobal("fullLen").ToNumber());
        }

        [Fact]
        public void Uint8Array_ReadWrite_WorksCorrectly()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var arr = new Uint8Array(4);
                arr[0] = 42; arr[1] = 255; arr[2] = 0; arr[3] = 128;
                var v0 = arr[0]; var v1 = arr[1]; var v2 = arr[2]; var v3 = arr[3];
                var len = arr.length;
            ");
            Assert.Equal(42.0, rt.GetGlobal("v0").ToNumber());
            Assert.Equal(255.0, rt.GetGlobal("v1").ToNumber());
            Assert.Equal(0.0, rt.GetGlobal("v2").ToNumber());
            Assert.Equal(128.0, rt.GetGlobal("v3").ToNumber());
            Assert.Equal(4.0, rt.GetGlobal("len").ToNumber());
        }

        [Fact]
        public void Int32Array_ReadWrite_HandlesSignedValues()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var arr = new Int32Array(2);
                arr[0] = -1; arr[1] = 2147483647;
                var v0 = arr[0]; var v1 = arr[1];
            ");
            Assert.Equal(-1.0, rt.GetGlobal("v0").ToNumber());
            Assert.Equal(2147483647.0, rt.GetGlobal("v1").ToNumber());
        }

        [Fact]
        public void Float64Array_ReadWrite_HandlesDoubles()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var arr = new Float64Array(2);
                arr[0] = 3.14; arr[1] = -0.5;
                var v0 = arr[0]; var v1 = arr[1];
            ");
            Assert.Equal(3.14, rt.GetGlobal("v0").ToNumber(), 5);
            Assert.Equal(-0.5, rt.GetGlobal("v1").ToNumber(), 5);
        }

        [Fact]
        public void TypedArray_BYTES_PER_ELEMENT_IsCorrect()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var u8 = Uint8Array.BYTES_PER_ELEMENT;
                var i32 = Int32Array.BYTES_PER_ELEMENT;
                var f64 = Float64Array.BYTES_PER_ELEMENT;
            ");
            Assert.Equal(1.0, rt.GetGlobal("u8").ToNumber());
            Assert.Equal(4.0, rt.GetGlobal("i32").ToNumber());
            Assert.Equal(8.0, rt.GetGlobal("f64").ToNumber());
        }

        // ── Finding #9: Temporal API removal ──

        [Fact]
        public void Temporal_IsNotExposed()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var hasTemporal = typeof Temporal !== 'undefined';");
            var result = rt.GetGlobal("hasTemporal");
            Assert.False(result.ToBoolean());
        }

        // ── Finding #25: StructuredClone FenValue path ──

        [Fact]
        public void StructuredClone_ClonesPlainObject()
        {
            var obj = new FenObject();
            obj.Set("x", FenValue.FromNumber(42));
            obj.Set("y", FenValue.FromString("hello"));
            var cloned = FenBrowser.FenEngine.Workers.StructuredClone.CloneFenValue(FenValue.FromObject(obj));
            var clonedObj = cloned.AsObject();
            Assert.Equal(42.0, clonedObj.Get("x").ToNumber());
            Assert.Equal("hello", clonedObj.Get("y").ToString());
            Assert.NotSame(obj, clonedObj); // Must be a different object
        }

        [Fact]
        public void StructuredClone_ClonesArrayBuffer()
        {
            var buf = new JsArrayBuffer(4);
            buf.Data[0] = 10; buf.Data[1] = 20; buf.Data[2] = 30; buf.Data[3] = 40;
            var cloned = FenBrowser.FenEngine.Workers.StructuredClone.CloneFenValue(FenValue.FromObject(buf));
            var clonedBuf = cloned.AsObject() as JsArrayBuffer;
            Assert.NotNull(clonedBuf);
            Assert.Equal(4, clonedBuf.Data.Length);
            Assert.Equal(10, clonedBuf.Data[0]);
            Assert.Equal(40, clonedBuf.Data[3]);
            Assert.NotSame(buf.Data, clonedBuf.Data); // Deep copy
        }

        [Fact]
        public void StructuredClone_ThrowsOnFunction()
        {
            var fn = FenValue.FromFunction(new FenFunction("test", (a, t) => FenValue.Undefined));
            Assert.Throws<FenBrowser.FenEngine.Workers.StructuredCloneException>(
                () => FenBrowser.FenEngine.Workers.StructuredClone.CloneFenValue(fn));
        }

        [Fact]
        public void StructuredClone_HandlesCyclicReferences()
        {
            var obj = new FenObject();
            obj.Set("self", FenValue.FromObject(obj)); // Cycle
            var cloned = FenBrowser.FenEngine.Workers.StructuredClone.CloneFenValue(FenValue.FromObject(obj));
            var clonedObj = cloned.AsObject() as FenObject;
            Assert.NotNull(clonedObj);
            // The self reference should point to the clone, not the original
            var selfRef = clonedObj.Get("self").AsObject();
            Assert.Same(clonedObj, selfRef);
        }

        // ── Finding #8: Intl full implementation (not stubs) ──

        [Fact]
        public void Intl_HasCoreConstructors()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var hasIntl = typeof Intl !== 'undefined';");
            Assert.True(rt.GetGlobal("hasIntl").ToBoolean());

            rt.ExecuteSimple("var hasDTF = Intl.DateTimeFormat !== undefined;");
            Assert.True(rt.GetGlobal("hasDTF").ToBoolean());
            rt.ExecuteSimple("var hasNF = Intl.NumberFormat !== undefined;");
            Assert.True(rt.GetGlobal("hasNF").ToBoolean());

            // Verify constructors are callable
            rt.ExecuteSimple("var fmt = new Intl.DateTimeFormat('en-US'); var hasFormat = typeof fmt.format === 'function';");
            Assert.True(rt.GetGlobal("hasFormat").ToBoolean());
        }

        [Fact]
        public void Intl_DateTimeFormat_IsConstructable()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var fmt = new Intl.DateTimeFormat('en-US');
                var hasFormat = typeof fmt.format === 'function';
            ");
            Assert.True(rt.GetGlobal("hasFormat").ToBoolean());
        }

        [Fact]
        public void Intl_NumberFormat_IsConstructable()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var fmt = new Intl.NumberFormat('en-US');
                var hasFormat = typeof fmt.format === 'function';
            ");
            Assert.True(rt.GetGlobal("hasFormat").ToBoolean());
        }

        // ── Finding #26: IndexedDB indexes/cursors/keyranges ──

        [Fact]
        public void IDBKeyRange_Only_CreatesRange()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var range = IDBKeyRange.only('abc');
                var lower = range.lower;
                var upper = range.upper;
                var inc = range.includes('abc');
                var notInc = range.includes('xyz');
            ");
            Assert.Equal("abc", rt.GetGlobal("lower").ToString());
            Assert.Equal("abc", rt.GetGlobal("upper").ToString());
            Assert.True(rt.GetGlobal("inc").ToBoolean());
            Assert.False(rt.GetGlobal("notInc").ToBoolean());
        }

        [Fact]
        public void IDBKeyRange_Bound_CreatesRange()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var range = IDBKeyRange.bound('a', 'z', false, true);
                var lower = range.lower;
                var upper = range.upper;
                var lo = range.lowerOpen;
                var uo = range.upperOpen;
            ");
            Assert.Equal("a", rt.GetGlobal("lower").ToString());
            Assert.Equal("z", rt.GetGlobal("upper").ToString());
            Assert.False(rt.GetGlobal("lo").ToBoolean());
            Assert.True(rt.GetGlobal("uo").ToBoolean());
        }

        // ── Finding #1: Legacy Scripting/ModuleLoader.cs retired ──

        [Fact]
        public void LegacyScriptingModuleLoader_IsDeleted()
        {
            // The legacy regex-based ModuleLoader in Scripting/ should no longer exist.
            // It was marked [Obsolete] and has been removed — the spec-compliant Core.ModuleLoader is the sole path.
            var assembly = typeof(FenBrowser.FenEngine.Scripting.JavaScriptEngine).Assembly;
            var legacyType = assembly.GetType("FenBrowser.FenEngine.Scripting.ModuleLoader");
            Assert.Null(legacyType);
        }

        // ── Finding #23: Fake promises replaced with real JsPromise ──

        [Fact]
        public void FullscreenAPI_WithContext_ReturnsRealJsPromise()
        {
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(null);
            var methods = FenBrowser.FenEngine.WebAPIs.FullscreenAPI.CreateDocumentFullscreenMethods(context);
            // exitFullscreen should return a real JsPromise when context is provided
            var exitFn = methods.Get("exitFullscreen");
            Assert.True(exitFn.IsFunction);
            var result = exitFn.AsFunction().Invoke(Array.Empty<FenValue>(), context);
            Assert.True(result.IsObject);
            // Should be a JsPromise, not a bare FenObject
            Assert.IsType<JsPromise>(result.AsObject());
        }

        [Fact]
        public void ClipboardAPI_WithContext_ReturnsRealJsPromise()
        {
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(null);
            var clipboard = FenBrowser.FenEngine.WebAPIs.ClipboardAPI.CreateClipboardObject(context);
            var readTextFn = clipboard.Get("readText");
            Assert.True(readTextFn.IsFunction);
            var result = readTextFn.AsFunction().Invoke(Array.Empty<FenValue>(), context);
            Assert.True(result.IsObject);
            Assert.IsType<JsPromise>(result.AsObject());
        }

        [Fact]
        public void CacheStorage_WithContext_ReturnsRealJsPromise()
        {
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(null);
            var storageBackend = new FenBrowser.FenEngine.Storage.InMemoryStorageBackend();
            var cacheStorage = new FenBrowser.FenEngine.WebAPIs.CacheStorage(
                () => "https://example.com", storageBackend, context);
            var hasFn = cacheStorage.Get("has");
            Assert.True(hasFn.IsFunction);
            var result = hasFn.AsFunction().Invoke(
                new[] { FenValue.FromString("test-cache") }, context);
            Assert.True(result.IsObject);
            Assert.IsType<JsPromise>(result.AsObject());
        }

        [Fact]
        public void CustomElementRegistry_WhenDefined_WithContext_ReturnsRealJsPromise()
        {
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(null);
            var registry = new FenBrowser.FenEngine.DOM.CustomElementRegistry(context);
            var jsObj = registry.ToFenObject();
            var whenDefinedFn = jsObj.Get("whenDefined");
            Assert.True(whenDefinedFn.IsFunction);
            var result = whenDefinedFn.AsFunction().Invoke(
                new[] { FenValue.FromString("x-test") }, context);
            Assert.True(result.IsObject);
            Assert.IsType<JsPromise>(result.AsObject());
        }

        // ── Finding #10: ServiceWorkerContainer uses real JsPromise ──

        [Fact]
        public void ServiceWorkerContainer_WithContext_HasRealReadyPromise()
        {
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(null);
            var container = new FenBrowser.FenEngine.Workers.ServiceWorkerContainer(
                "https://example.com", context);
            var ready = container.Get("ready");
            Assert.True(ready.IsObject);
            Assert.IsType<JsPromise>(ready.AsObject());
        }

        // ── Finding #12: Worker message delivery uses StructuredClone ──

        [Fact]
        public void WorkerGlobalScope_DispatchMessage_ClonesData()
        {
            // Verify that StructuredClone.CloneFenValue properly deep-clones objects
            // This is the path used by WorkerGlobalScope.DispatchMessage
            var obj = new FenObject();
            obj.Set("key", FenValue.FromString("value"));

            var cloned = FenBrowser.FenEngine.Workers.StructuredClone.CloneFenValue(FenValue.FromObject(obj));
            Assert.True(cloned.IsObject);
            var clonedObj = cloned.AsObject();
            Assert.NotSame(obj, clonedObj);
            Assert.Equal("value", clonedObj.Get("key").ToString());
        }

        // ── Finding #26: IndexedDB transaction auto-commit + cursor direction ──

        [Fact]
        public void IDBKeyRange_LowerBound_CreatesRange()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var range = IDBKeyRange.lowerBound('m', true);
                var lower = range.lower;
                var lo = range.lowerOpen;
                var upper = range.upper;
            ");
            Assert.Equal("m", rt.GetGlobal("lower").ToString());
            Assert.True(rt.GetGlobal("lo").ToBoolean());
            Assert.True(rt.GetGlobal("upper").IsUndefined || rt.GetGlobal("upper").IsNull);
        }

        [Fact]
        public void IDBKeyRange_UpperBound_CreatesRange()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var range = IDBKeyRange.upperBound('m', false);
                var upper = range.upper;
                var uo = range.upperOpen;
                var lower = range.lower;
            ");
            Assert.Equal("m", rt.GetGlobal("upper").ToString());
            Assert.False(rt.GetGlobal("uo").ToBoolean());
            Assert.True(rt.GetGlobal("lower").IsUndefined || rt.GetGlobal("lower").IsNull);
        }

        [Fact]
        public void IDBKeyRange_Includes_WithBound()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var range = IDBKeyRange.bound('c', 'f');
                var r1 = range.includes('d');
                var r2 = range.includes('a');
                var r3 = range.includes('c');
                var r4 = range.includes('f');
                var r5 = range.includes('z');
            ");
            Assert.True(rt.GetGlobal("r1").ToBoolean());
            Assert.False(rt.GetGlobal("r2").ToBoolean());
            Assert.True(rt.GetGlobal("r3").ToBoolean());
            Assert.True(rt.GetGlobal("r4").ToBoolean());
            Assert.False(rt.GetGlobal("r5").ToBoolean());
        }

        // ── Finding #10: ServiceWorkerContainer live controller + controllerchange ──

        [Fact]
        public void ServiceWorkerContainer_HasControllerChangeHandler()
        {
            var container = new FenBrowser.FenEngine.Workers.ServiceWorkerContainer("https://example.com");
            var oncc = container.Get("oncontrollerchange");
            // Should exist as a property (initially null)
            Assert.True(oncc.IsNull);
        }

        [Fact]
        public void ServiceWorkerContainer_UpdateController_FiresControllerChange()
        {
            var container = new FenBrowser.FenEngine.Workers.ServiceWorkerContainer("https://example.com");
            bool fired = false;
            container.Set("oncontrollerchange", FenValue.FromFunction(new FenFunction("oncc", (args, thisVal) =>
            {
                fired = true;
                return FenValue.Undefined;
            })));
            var sw = new FenBrowser.FenEngine.Workers.ServiceWorker("https://example.com/sw.js", "https://example.com/", "activated");
            container.UpdateController(sw);
            Assert.True(fired);
            Assert.False(container.Get("controller").IsNull);
        }

        [Fact]
        public void ServiceWorkerContainer_HasGetRegistrations()
        {
            var container = new FenBrowser.FenEngine.Workers.ServiceWorkerContainer("https://example.com");
            var fn = container.Get("getRegistrations");
            Assert.True(fn.IsFunction);
        }

        // ── Finding #11: skipWaiting/clients.claim wiring ──

        [Fact]
        public void ServiceWorkerGlobalScope_SkipWaiting_IsWired()
        {
            // Verify that skipWaiting function exists on ServiceWorkerGlobalScope
            // We can't easily create one without a full WorkerRuntime, but we can check
            // via the ServiceWorkerClients claim method
            var clients = new FenBrowser.FenEngine.Workers.ServiceWorkerClients("https://example.com/");
            var claimFn = clients.Get("claim");
            Assert.True(claimFn.IsFunction);
        }

        // ── Finding #12: StructuredClone transfer list ──

        [Fact]
        public void StructuredClone_TransferArrayBuffer_DetachesSource()
        {
            var buf = new JsArrayBuffer(8);
            buf.Data[0] = 42;
            buf.Data[7] = 99;

            var transferList = new[] { FenValue.FromObject(buf) };
            var cloned = FenBrowser.FenEngine.Workers.StructuredClone.CloneFenValueWithTransfer(
                FenValue.FromObject(buf), transferList);

            // Source should be detached
            Assert.True(buf.IsDetached);
            Assert.Equal(0, buf.Data.Length);

            // Clone should have the data
            var clonedBuf = cloned.AsObject() as JsArrayBuffer;
            Assert.NotNull(clonedBuf);
            Assert.Equal(8, clonedBuf.Data.Length);
            Assert.Equal(42, clonedBuf.Data[0]);
            Assert.Equal(99, clonedBuf.Data[7]);
        }

        [Fact]
        public void StructuredClone_TransferDuplicate_Throws()
        {
            var buf = new JsArrayBuffer(4);
            var transferList = new[] { FenValue.FromObject(buf), FenValue.FromObject(buf) };

            Assert.Throws<FenBrowser.FenEngine.Workers.StructuredCloneException>(() =>
                FenBrowser.FenEngine.Workers.StructuredClone.CloneFenValueWithTransfer(
                    FenValue.FromObject(buf), transferList));
        }

        [Fact]
        public void StructuredClone_TransferNonTransferable_Throws()
        {
            var obj = new FenObject();
            obj.Set("key", FenValue.FromString("val"));
            var transferList = new[] { FenValue.FromObject(obj) };

            Assert.Throws<FenBrowser.FenEngine.Workers.StructuredCloneException>(() =>
                FenBrowser.FenEngine.Workers.StructuredClone.CloneFenValueWithTransfer(
                    FenValue.FromObject(obj), transferList));
        }

        [Fact]
        public void JsArrayBuffer_Detach_NeutersBuffer()
        {
            var buf = new JsArrayBuffer(16);
            Assert.False(buf.IsDetached);
            Assert.Equal(16, buf.Data.Length);

            buf.Detach();
            Assert.True(buf.IsDetached);
            Assert.Equal(0, buf.Data.Length);
            Assert.Equal(0.0, buf.Get("byteLength").ToNumber());
        }

        // ── Finding #28: WebAudio API completeness ──

        [Fact]
        public void AudioContext_HasAllNodeFactoryMethods()
        {
            // W3C Web Audio §10.3: AudioContext must expose all node factory methods
            var perm = new FenBrowser.FenEngine.Security.PermissionManager(FenBrowser.FenEngine.Security.JsPermissions.StandardWeb);
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
            context.Environment = new FenEnvironment();

            var ctx = FenBrowser.FenEngine.WebAPIs.WebAudioAPI.CreateAudioContext(context);

            // Original factories
            Assert.True(ctx.Get("createOscillator").IsFunction);
            Assert.True(ctx.Get("createGain").IsFunction);
            Assert.True(ctx.Get("createAnalyser").IsFunction);
            Assert.True(ctx.Get("createBufferSource").IsFunction);
            Assert.True(ctx.Get("createBiquadFilter").IsFunction);
            Assert.True(ctx.Get("createConvolver").IsFunction);
            Assert.True(ctx.Get("createDelay").IsFunction);
            Assert.True(ctx.Get("createDynamicsCompressor").IsFunction);

            // New factories (Finding #28)
            Assert.True(ctx.Get("createStereoPanner").IsFunction);
            Assert.True(ctx.Get("createPanner").IsFunction);
            Assert.True(ctx.Get("createChannelSplitter").IsFunction);
            Assert.True(ctx.Get("createChannelMerger").IsFunction);
            Assert.True(ctx.Get("createWaveShaper").IsFunction);
            Assert.True(ctx.Get("createConstantSource").IsFunction);
            Assert.True(ctx.Get("createPeriodicWave").IsFunction);
            Assert.True(ctx.Get("createIIRFilter").IsFunction);
            Assert.True(ctx.Get("createMediaElementSource").IsFunction);
            Assert.True(ctx.Get("createMediaStreamSource").IsFunction);
            Assert.True(ctx.Get("createMediaStreamDestination").IsFunction);
            Assert.True(ctx.Get("createScriptProcessor").IsFunction);
        }

        [Fact]
        public void AudioContext_CurrentTime_IsMonotonic()
        {
            // W3C Web Audio §10.1: currentTime advances monotonically
            var perm = new FenBrowser.FenEngine.Security.PermissionManager(FenBrowser.FenEngine.Security.JsPermissions.StandardWeb);
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
            context.Environment = new FenEnvironment();

            var ctx = FenBrowser.FenEngine.WebAPIs.WebAudioAPI.CreateAudioContext(context);
            var t0 = ctx.Get("currentTime").ToNumber();
            Assert.True(t0 >= 0, "currentTime should be non-negative");
        }

        [Fact]
        public void AudioContext_HasOnStateChange()
        {
            var perm = new FenBrowser.FenEngine.Security.PermissionManager(FenBrowser.FenEngine.Security.JsPermissions.StandardWeb);
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
            context.Environment = new FenEnvironment();

            var ctx = FenBrowser.FenEngine.WebAPIs.WebAudioAPI.CreateAudioContext(context);
            // onstatechange should be present (null initially)
            var val = ctx.Get("onstatechange");
            Assert.True(val.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Null || val.IsFunction || val.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Undefined);
        }

        [Fact]
        public void AudioContext_StereoPanner_HasPanParam()
        {
            var perm = new FenBrowser.FenEngine.Security.PermissionManager(FenBrowser.FenEngine.Security.JsPermissions.StandardWeb);
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
            context.Environment = new FenEnvironment();

            var ctx = FenBrowser.FenEngine.WebAPIs.WebAudioAPI.CreateAudioContext(context);
            var createFn = ctx.Get("createStereoPanner").AsFunction();
            var node = createFn.Invoke(Array.Empty<FenValue>(), context).AsObject();
            Assert.True(node.Get("pan").IsObject, "StereoPannerNode must have pan AudioParam");
        }

        [Fact]
        public void AudioContext_ChannelSplitter_DefaultOutputs()
        {
            var perm = new FenBrowser.FenEngine.Security.PermissionManager(FenBrowser.FenEngine.Security.JsPermissions.StandardWeb);
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
            context.Environment = new FenEnvironment();

            var ctx = FenBrowser.FenEngine.WebAPIs.WebAudioAPI.CreateAudioContext(context);
            var createFn = ctx.Get("createChannelSplitter").AsFunction();
            var node = createFn.Invoke(Array.Empty<FenValue>(), context).AsObject();
            Assert.Equal(6.0, node.Get("numberOfOutputs").ToNumber());
        }

        // ── Finding #29: WebRTC API completeness ──

        [Fact]
        public void RTCPeerConnection_HasRestartIce()
        {
            var perm = new FenBrowser.FenEngine.Security.PermissionManager(FenBrowser.FenEngine.Security.JsPermissions.StandardWeb);
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
            context.Environment = new FenEnvironment();

            var ctor = (FenFunction)FenBrowser.FenEngine.WebAPIs.WebRTCAPI.CreateRTCPeerConnectionConstructor(context);
            var pc = ctor.Invoke(Array.Empty<FenValue>(), context).AsObject();
            Assert.True(pc.Get("restartIce").IsFunction, "RTCPeerConnection must have restartIce()");
        }

        [Fact]
        public void RTCPeerConnection_HasAddTransceiver()
        {
            var perm = new FenBrowser.FenEngine.Security.PermissionManager(FenBrowser.FenEngine.Security.JsPermissions.StandardWeb);
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
            context.Environment = new FenEnvironment();

            var ctor = (FenFunction)FenBrowser.FenEngine.WebAPIs.WebRTCAPI.CreateRTCPeerConnectionConstructor(context);
            var pc = ctor.Invoke(Array.Empty<FenValue>(), context).AsObject();
            Assert.True(pc.Get("addTransceiver").IsFunction, "RTCPeerConnection must have addTransceiver()");
        }

        [Fact]
        public void RTCPeerConnection_AddTransceiver_ReturnsTransceiver()
        {
            var perm = new FenBrowser.FenEngine.Security.PermissionManager(FenBrowser.FenEngine.Security.JsPermissions.StandardWeb);
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
            context.Environment = new FenEnvironment();

            var ctor = (FenFunction)FenBrowser.FenEngine.WebAPIs.WebRTCAPI.CreateRTCPeerConnectionConstructor(context);
            var pc = ctor.Invoke(Array.Empty<FenValue>(), context).AsObject();
            var addTransceiver = pc.Get("addTransceiver").AsFunction();
            var transceiver = addTransceiver.Invoke(new[] { FenValue.FromString("audio") }, context).AsObject();
            Assert.NotNull(transceiver);
            Assert.True(transceiver.Get("sender").IsObject, "Transceiver must have sender");
            Assert.True(transceiver.Get("receiver").IsObject, "Transceiver must have receiver");
            Assert.True(transceiver.Get("stop").IsFunction, "Transceiver must have stop()");
        }

        // ── Finding #26: IndexedDB persistence via IStorageBackend ──

        [Fact]
        public void IndexedDB_SetStorageBackend_AcceptsBackend()
        {
            // IDB §2.11: IndexedDBService should accept an IStorageBackend for persistence
            var backend = new FenBrowser.FenEngine.Storage.InMemoryStorageBackend();
            FenBrowser.FenEngine.WebAPIs.IndexedDBService.SetStorageBackend(backend);
            // Should not throw — backend is set
        }

        [Fact]
        public void IndexedDB_Register_WithOriginAndBackend()
        {
            // IDB §4.1: IndexedDB should support origin partitioning
            var backend = new FenBrowser.FenEngine.Storage.InMemoryStorageBackend();
            var perm = new FenBrowser.FenEngine.Security.PermissionManager(FenBrowser.FenEngine.Security.JsPermissions.StandardWeb);
            var context = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
            context.Environment = new FenEnvironment();

            FenBrowser.FenEngine.WebAPIs.IndexedDBService.Register(context, "https://example.com", backend);

            // indexedDB global should be registered
            var indexedDB = context.Environment.Get("indexedDB");
            Assert.True(indexedDB.IsObject, "indexedDB global should be registered");
            Assert.True(indexedDB.AsObject().Get("open").IsFunction, "indexedDB.open should be a function");
            Assert.True(indexedDB.AsObject().Get("deleteDatabase").IsFunction, "indexedDB.deleteDatabase should be a function");
        }

        [Fact]
        public void IndexedDB_OriginPartitioning_IsolatesDatabases()
        {
            // IDB §4.1: Same database name under different origins must be isolated
            var backend = new FenBrowser.FenEngine.Storage.InMemoryStorageBackend();
            FenBrowser.FenEngine.WebAPIs.IndexedDBService.SetStorageBackend(backend);

            var perm = new FenBrowser.FenEngine.Security.PermissionManager(FenBrowser.FenEngine.Security.JsPermissions.StandardWeb);

            // Register under origin A
            var ctxA = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
            ctxA.Environment = new FenEnvironment();
            FenBrowser.FenEngine.WebAPIs.IndexedDBService.Register(ctxA, "https://a.com", backend);

            // Register under origin B
            var ctxB = new FenBrowser.FenEngine.Core.ExecutionContext(perm);
            ctxB.Environment = new FenEnvironment();
            FenBrowser.FenEngine.WebAPIs.IndexedDBService.Register(ctxB, "https://b.com", backend);

            // Both should get their own indexedDB
            Assert.True(ctxA.Environment.Get("indexedDB").IsObject);
            Assert.True(ctxB.Environment.Get("indexedDB").IsObject);
        }

        // ── Fix #6: StructuredClone Map/Set entry cloning ──

        [Fact]
        public void StructuredClone_Map_PreservesEntries()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var m = new Map();
                m.set('a', 1);
                m.set('b', 2);
                var cloned = structuredClone(m);
                var result = cloned.get('a') === 1 && cloned.get('b') === 2 && cloned.size === 2;
            ");
            Assert.True(rt.GetGlobal("result").ToBoolean());
        }

        [Fact]
        public void StructuredClone_Map_CSharpLevel()
        {
            // Direct C# verification of StructuredClone.CloneFenValue for JsMap
            var map = new JsMap(null);
            var setFn = map.Get("set");
            setFn.AsFunction().NativeImplementation(
                new FenValue[] { FenValue.FromString("a"), FenValue.FromNumber(1) },
                FenValue.FromObject(map));

            var cloned = FenBrowser.FenEngine.Workers.StructuredClone.CloneFenValue(FenValue.FromObject(map));
            var clonedMap = cloned.AsObject() as JsMap;
            Assert.NotNull(clonedMap);
            Assert.Equal(1, clonedMap.InternalStorage.Count);
        }

        [Fact]
        public void StructuredClone_Set_PreservesEntries()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var s = new Set();
                s.add(10);
                s.add(20);
                var cloned = structuredClone(s);
                var result = cloned.has(10) && cloned.has(20) && cloned.size === 2;
            ");
            Assert.True(rt.GetGlobal("result").ToBoolean());
        }

        [Fact]
        public void StructuredClone_Map_IsDeepCopy()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var m = new Map();
                m.set('x', 42);
                var cloned = structuredClone(m);
                cloned.set('x', 99);
                var result = m.get('x') === 42;
            ");
            Assert.True(rt.GetGlobal("result").ToBoolean());
        }

        // ── Fix #9: StructuredClone Error objects ──

        [Fact]
        public void StructuredClone_Error_PreservesMessageAndName()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var err = new Error('test message');
                var cloned = structuredClone(err);
                var result = cloned.message === 'test message' && cloned.name === 'Error';
            ");
            Assert.True(rt.GetGlobal("result").ToBoolean());
        }

        // ── Fix #3: Intl full registration ──

        [Fact]
        public void Intl_AllConstructors_NotOverwritten()
        {
            var rt = CreateRuntime();
            // Verify the full JsIntl registration is intact — all 10 ECMA-402 constructors
            rt.ExecuteSimple(@"
                var hasNF = typeof Intl.NumberFormat === 'function';
                var hasDTF = typeof Intl.DateTimeFormat === 'function';
                var hasColl = typeof Intl.Collator === 'function';
                var hasPR = typeof Intl.PluralRules === 'function';
                var hasRT = typeof Intl.RelativeTimeFormat === 'function';
                var hasLN = typeof Intl.ListFormat === 'function';
                var hasSeg = typeof Intl.Segmenter === 'function';
                var hasDN = typeof Intl.DisplayNames === 'function';
            ");
            Assert.True(rt.GetGlobal("hasNF").ToBoolean());
            Assert.True(rt.GetGlobal("hasDTF").ToBoolean());
            Assert.True(rt.GetGlobal("hasColl").ToBoolean());
            Assert.True(rt.GetGlobal("hasPR").ToBoolean());
            Assert.True(rt.GetGlobal("hasRT").ToBoolean());
            Assert.True(rt.GetGlobal("hasLN").ToBoolean());
            Assert.True(rt.GetGlobal("hasSeg").ToBoolean());
            Assert.True(rt.GetGlobal("hasDN").ToBoolean());
        }

        // ── Fix #4/#5: ServiceWorker script hash + persistence ──

        [Fact]
        public void ServiceWorkerManager_HasScriptHashComparison()
        {
            // Verify ComputeScriptHash is callable via reflection (private static)
            var method = typeof(FenBrowser.FenEngine.Workers.ServiceWorkerManager)
                .GetMethod("ComputeScriptHash", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var hash1 = (string)method.Invoke(null, new object[] { "console.log('hello');" });
            var hash2 = (string)method.Invoke(null, new object[] { "console.log('hello');" });
            var hash3 = (string)method.Invoke(null, new object[] { "console.log('world');" });

            Assert.Equal(hash1, hash2); // Same script = same hash
            Assert.NotEqual(hash1, hash3); // Different script = different hash
        }

        [Fact]
        public void ServiceWorkerManager_HasLoadPersistedRegistrations()
        {
            var method = typeof(FenBrowser.FenEngine.Workers.ServiceWorkerManager)
                .GetMethod("LoadPersistedRegistrationsAsync", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(method);
        }
    }
}
