using System;
using FenBrowser.Core.Engine;
using System.Reflection;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// JS-8: Built-in prototype method completeness regression tests.
    /// Validates modern ECMAScript Array, String, Object, Number, and global built-in methods.
    /// </summary>
    [Collection("Engine Tests")]
    public class BuiltinCompletenessTests
    {
        public BuiltinCompletenessTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        private FenRuntime CreateRuntime() => new FenRuntime();

        // ==================== ARRAY TESTS ====================

        [Fact]
        public void Array_Flat_FlattensByDefaultDepth1()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var a = [1, [2, 3], [4, [5]]].flat(); var len = a.length;");
            Assert.Equal(5.0, rt.GetGlobal("len").ToNumber()); // [1,2,3,4,[5]] â€” 5 elements
        }

        [Fact]
        public void Array_FlatMap_MapsAndFlattens()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var a = [1, 2, 3].flatMap(function(x) { return [x, x * 2]; }); var len = a.length;");
            Assert.Equal(6.0, rt.GetGlobal("len").ToNumber()); // [1,2,2,4,3,6]
        }

        [Fact]
        public void Array_At_PositiveIndex()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var a = [10, 20, 30]; var v = a.at(1);");
            Assert.Equal(20.0, rt.GetGlobal("v").ToNumber());
        }

        [Fact]
        public void Array_At_NegativeIndex()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var a = [10, 20, 30]; var v = a.at(-1);");
            Assert.Equal(30.0, rt.GetGlobal("v").ToNumber());
        }

        [Fact]
        public void Array_FindLast_ReturnsLastMatch()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var a = [1, 2, 3, 4, 5]; var v = a.findLast(function(x) { return x % 2 === 0; });");
            Assert.Equal(4.0, rt.GetGlobal("v").ToNumber());
        }

        [Fact]
        public void Array_FindLastIndex_ReturnsLastMatchIndex()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var a = [1, 2, 3, 4, 5]; var i = a.findLastIndex(function(x) { return x % 2 === 0; });");
            Assert.Equal(3.0, rt.GetGlobal("i").ToNumber());
        }

        [Fact]
        public void Array_ToReversed_NonMutating()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var a = [1, 2, 3];
                var b = a.toReversed();
                var aFirst = a[0];
                var bFirst = b[0];
            ");
            Assert.Equal(1.0, rt.GetGlobal("aFirst").ToNumber()); // original unchanged
            Assert.Equal(3.0, rt.GetGlobal("bFirst").ToNumber()); // reversed copy
        }

        [Fact]
        public void Array_From_ArrayLike()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var a = Array.from('hello'); var len = a.length;");
            Assert.Equal(5.0, rt.GetGlobal("len").ToNumber());
        }

        // ==================== STRING TESTS ====================

        [Fact]
        public void String_At_PositiveIndex()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = 'hello'; var c = s.at(1);");
            Assert.Equal("e", rt.GetGlobal("c").ToString());
        }

        [Fact]
        public void String_At_NegativeIndex()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = 'hello'; var c = s.at(-1);");
            Assert.Equal("o", rt.GetGlobal("c").ToString());
        }

        [Fact]
        public void String_ReplaceAll_ReplacesAll()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = 'foo bar foo baz foo'; var r = s.replaceAll('foo', 'qux');");
            Assert.Equal("qux bar qux baz qux", rt.GetGlobal("r").ToString());
        }

        [Fact]
        public void String_PadStart_PadsToWidth()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = '5'.padStart(3, '0');");
            Assert.Equal("005", rt.GetGlobal("s").ToString());
        }

        [Fact]
        public void String_PadEnd_PadsToWidth()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = '5'.padEnd(3, '0');");
            Assert.Equal("500", rt.GetGlobal("s").ToString());
        }

        [Fact]
        public void String_TrimStart_RemovesLeadingWhitespace()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = '   hello'.trimStart();");
            Assert.Equal("hello", rt.GetGlobal("s").ToString());
        }

        [Fact]
        public void String_TrimEnd_RemovesTrailingWhitespace()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = 'hello   '.trimEnd();");
            Assert.Equal("hello", rt.GetGlobal("s").ToString());
        }

        [Fact]
        public void String_Repeat_RepeatsString()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = 'ab'.repeat(3);");
            Assert.Equal("ababab", rt.GetGlobal("s").ToString());
        }

        // ==================== OBJECT TESTS ====================

        [Fact]
        public void Object_HasOwn_TrueForOwnProperty()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var obj = { x: 1 }; var h = Object.hasOwn(obj, 'x');");
            Assert.True(rt.GetGlobal("h").ToBoolean());
        }

        [Fact]
        public void Object_HasOwn_FalseForInherited()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var obj = {}; var h = Object.hasOwn(obj, 'toString');");
            Assert.False(rt.GetGlobal("h").ToBoolean());
        }

        [Fact]
        public void Object_FromEntries_RoundTripsEntries()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var obj1 = { a: 1, b: 2 };
                var entries = Object.entries(obj1);
                var obj2 = Object.fromEntries(entries);
                var aVal = obj2.a;
                var bVal = obj2.b;
            ");
            Assert.Equal(1.0, rt.GetGlobal("aVal").ToNumber());
            Assert.Equal(2.0, rt.GetGlobal("bVal").ToNumber());
        }

        [Fact]
        public void RegExp_LegacyAccessor_GetWithReceiver_UsesAccessorDescriptor()
        {
            var rt = CreateRuntime();
            var regexpValue = (FenValue)rt.GetGlobal("RegExp");
            var regexpObject = Assert.IsType<FenFunction>(regexpValue.AsObject());

            var descriptor = regexpObject.GetOwnPropertyDescriptor("leftContext");
            Assert.True(descriptor.HasValue);
            Assert.NotNull(descriptor.Value.Getter);

            var value = regexpObject.GetWithReceiver("leftContext", regexpValue);
            var fenKeyValue = regexpObject.GetWithReceiver(FenValue.FromString("leftContext"), regexpValue, rt.Context);

            Assert.True(value.IsString);
            Assert.Equal(string.Empty, value.AsString());
            Assert.True(fenKeyValue.IsString, $"fenKeyValue type={fenKeyValue.Type} value={fenKeyValue}");
            Assert.Equal(string.Empty, fenKeyValue.AsString());
        }

        [Fact]
        public void RegExp_LegacyAccessor_ReflectGet_OnSelf_ReturnsEmptyString()
        {
            var rt = CreateRuntime();
            var regexpValue = (FenValue)rt.GetGlobal("RegExp");
            var reflectValue = (FenValue)rt.GetGlobal("Reflect");
            var reflectObject = Assert.IsType<FenObject>(reflectValue.AsObject());
            var getFunction = Assert.IsType<FenFunction>(reflectObject.Get("get").AsObject());
            var getImplementationOwner = getFunction.NativeImplementation.Method.DeclaringType?.FullName ?? string.Empty;
            Assert.Contains("FenRuntime", getImplementationOwner);

            var directInvokeValue = getFunction.Invoke(
                new[] { regexpValue, FenValue.FromString("leftContext"), regexpValue },
                rt.Context,
                reflectValue);
            Assert.True(directInvokeValue.IsString, $"directInvokeValue type={directInvokeValue.Type} value={directInvokeValue}");
            Assert.Equal(string.Empty, directInvokeValue.AsString());

            rt.ExecuteSimple(@"
                var dg = Reflect.get;
                var dgType = typeof dg;
                var sameFn = dg === Reflect.get;
                var viaDirect = dg(RegExp, 'leftContext', RegExp);
                var viaMember = Reflect.get(RegExp, 'leftContext', RegExp);
            ");

            var dgType = (FenValue)rt.GetGlobal("dgType");
            Assert.True(dgType.IsString);
            Assert.Equal("function", dgType.AsString());

            var sameFn = (FenValue)rt.GetGlobal("sameFn");
            Assert.True(sameFn.ToBoolean());

            var viaDirect = (FenValue)rt.GetGlobal("viaDirect");
            Assert.True(viaDirect.IsString, $"viaDirect type={viaDirect.Type} value={viaDirect}");
            Assert.Equal(string.Empty, viaDirect.AsString());

            var viaMember = (FenValue)rt.GetGlobal("viaMember");
            Assert.True(viaMember.IsString, $"viaMember type={viaMember.Type} value={viaMember}");
            Assert.Equal(string.Empty, viaMember.AsString());
        }

        [Fact]
        public void Debug_AnnexB_EvalCompiler_MarksBlockFunctionForHoist()
        {
            var lexer = new Lexer("{ function f() {} }");
            var parser = new Parser(lexer);
            var program = parser.ParseProgram();
            Assert.Empty(parser.Errors);

            var compiler = new FenBrowser.FenEngine.Core.Bytecode.Compiler.BytecodeCompiler(isEval: true);
            var block = compiler.Compile(program);

            Assert.NotNull(block.AnnexBBlockFunctionNames);
            Assert.Contains("f", block.AnnexBBlockFunctionNames);
        }

        // ==================== NUMBER STATIC TESTS ====================

        [Fact]
        public void Number_IsFinite_Static()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var a = Number.isFinite(42);
                var b = Number.isFinite(Infinity);
                var c = Number.isFinite(NaN);
                var d = Number.isFinite(0);
            ");
            Assert.True(rt.GetGlobal("a").ToBoolean());
            Assert.False(rt.GetGlobal("b").ToBoolean());
            Assert.False(rt.GetGlobal("c").ToBoolean());
            Assert.True(rt.GetGlobal("d").ToBoolean());
        }

        [Fact]
        public void Number_IsNaN_Static_DoesNotCoerce()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var a = Number.isNaN(NaN);
                var b = Number.isNaN(42);
                var c = Number.isNaN('NaN');
            ");
            Assert.True(rt.GetGlobal("a").ToBoolean());
            Assert.False(rt.GetGlobal("b").ToBoolean());
            Assert.False(rt.GetGlobal("c").ToBoolean()); // Number.isNaN never coerces
        }

        [Fact]
        public void Number_IsInteger_Static()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var a = Number.isInteger(42);
                var b = Number.isInteger(42.5);
                var c = Number.isInteger(42.0);
            ");
            Assert.True(rt.GetGlobal("a").ToBoolean());
            Assert.False(rt.GetGlobal("b").ToBoolean());
            Assert.True(rt.GetGlobal("c").ToBoolean());
        }

        // ==================== STRUCTUREDCLONE TESTS ====================

        [Fact]
        public void StructuredClone_DeepCopiesNestedObject()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var original = { a: 1, b: { c: 2 } };
                var clone = structuredClone(original);
                clone.b.c = 99;
                var originalC = original.b.c;
                var cloneC = clone.b.c;
            ");
            // original should be unchanged
            Assert.Equal(2.0, rt.GetGlobal("originalC").ToNumber());
            Assert.Equal(99.0, rt.GetGlobal("cloneC").ToNumber());
        }

        // ==================== JS-2a: Array.fromAsync TESTS ====================

        // Helper: run JS code then drain the microtask queue (required for Promise.then callbacks).
        private void RunWithMicrotasks(FenRuntime rt, string code)
        {
            rt.ExecuteSimple(code);
            // Drain pending microtasks (Promise.then callbacks are scheduled as microtasks).
            try { EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint(); }
            catch (InvalidOperationException) { /* already draining â€” ignore */ }
        }

        [Fact]
        public void Array_FromAsync_ReturnsPromise()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var p = Array.fromAsync([1, 2, 3]);
                var isPromise = p != null && typeof p === 'object' && typeof p.then === 'function';
            ");
            Assert.True(rt.GetGlobal("isPromise").ToBoolean());
        }

        [Fact]
        public void Array_StaticBuiltinMetadata_MatchesSpecSurface()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                function isCtor(fn) {
                    try {
                        Reflect.construct(function() {}, [], fn);
                        return true;
                    } catch (e) {
                        return false;
                    }
                }

                var arrayDesc = Object.getOwnPropertyDescriptor(this, 'Array');
                var fromDesc = Object.getOwnPropertyDescriptor(Array, 'from');
                var ofDesc = Object.getOwnPropertyDescriptor(Array, 'of');
                var fromAsyncDesc = Object.getOwnPropertyDescriptor(Array, 'fromAsync');

                var arrayEnumerableOut = arrayDesc.enumerable;
                var arrayLengthOut = Array.length;
                var fromLengthOut = Array.from.length;
                var ofLengthOut = Array.of.length;
                var fromAsyncLengthOut = Array.fromAsync.length;
                var fromEnumerableOut = fromDesc.enumerable;
                var ofEnumerableOut = ofDesc.enumerable;
                var fromAsyncEnumerableOut = fromAsyncDesc.enumerable;
                var fromCtorOut = isCtor(Array.from);
                var ofCtorOut = isCtor(Array.of);
                var fromAsyncCtorOut = isCtor(Array.fromAsync);
            ");

            Assert.False(rt.GetGlobal("arrayEnumerableOut").ToBoolean());
            Assert.Equal(1.0, rt.GetGlobal("arrayLengthOut").ToNumber());
            Assert.Equal(1.0, rt.GetGlobal("fromLengthOut").ToNumber());
            Assert.Equal(0.0, rt.GetGlobal("ofLengthOut").ToNumber());
            Assert.Equal(1.0, rt.GetGlobal("fromAsyncLengthOut").ToNumber());
            Assert.False(rt.GetGlobal("fromEnumerableOut").ToBoolean());
            Assert.False(rt.GetGlobal("ofEnumerableOut").ToBoolean());
            Assert.False(rt.GetGlobal("fromAsyncEnumerableOut").ToBoolean());
            Assert.False(rt.GetGlobal("fromCtorOut").ToBoolean());
            Assert.False(rt.GetGlobal("ofCtorOut").ToBoolean());
            Assert.False(rt.GetGlobal("fromAsyncCtorOut").ToBoolean());
        }

        [Fact]
        public void Array_FromAsync_SyncIterable_Resolves()
        {
            var rt = CreateRuntime();
            RunWithMicrotasks(rt, @"
                var resolved = null;
                var p = Array.fromAsync([10, 20, 30]);
                p.then(function(arr) { resolved = arr; });
            ");
            var len = rt.GetGlobal("resolved").AsObject()?.Get("length").ToNumber() ?? -1;
            var first = rt.GetGlobal("resolved").AsObject()?.Get("0").ToNumber() ?? -1;
            Assert.Equal(3.0, len);
            Assert.Equal(10.0, first);
        }

        [Fact]
        public void Array_FromAsync_WithMapFn()
        {
            var rt = CreateRuntime();
            RunWithMicrotasks(rt, @"
                var resolved = null;
                Array.fromAsync([1, 2, 3], function(x) { return x * 2; }).then(function(arr) { resolved = arr; });
            ");
            var len = rt.GetGlobal("resolved").AsObject()?.Get("length").ToNumber() ?? -1;
            var second = rt.GetGlobal("resolved").AsObject()?.Get("1").ToNumber() ?? -1;
            Assert.Equal(3.0, len);
            Assert.Equal(4.0, second);
        }

        [Fact]
        public void Array_FromAsync_NullInput_Rejects()
        {
            var rt = CreateRuntime();
            RunWithMicrotasks(rt, @"
                var rejected = false;
                Array.fromAsync(null).then(function() { }, function() { rejected = true; });
            ");
            Assert.True(rt.GetGlobal("rejected").ToBoolean());
        }

        [Fact]
        public void Array_FromAsync_NonCallableMapper_Rejects()
        {
            var rt = CreateRuntime();
            RunWithMicrotasks(rt, @"
                var rejected = false;
                Array.fromAsync([1, 2, 3], 123).then(function() { }, function() { rejected = true; });
            ");
            Assert.True(rt.GetGlobal("rejected").ToBoolean());
        }

        [Fact]
        public void Array_FromAsync_MapFn_UsesThisArg()
        {
            var rt = CreateRuntime();
            RunWithMicrotasks(rt, @"
                var resolved = null;
                var ctx = { mul: 3 };
                Array.fromAsync([2, 4], function(x) { return x * this.mul; }, ctx)
                  .then(function(arr) { resolved = arr; });
            ");

            var first = rt.GetGlobal("resolved").AsObject()?.Get("0").ToNumber() ?? -1;
            var second = rt.GetGlobal("resolved").AsObject()?.Get("1").ToNumber() ?? -1;
            Assert.Equal(6.0, first);
            Assert.Equal(12.0, second);
        }

        [Fact]
        public void Array_FromAsync_InvalidIteratorMethod_Rejects()
        {
            var rt = CreateRuntime();
            RunWithMicrotasks(rt, @"
                var rejected = false;
                var bad = { [Symbol.iterator]: 1 };
                Array.fromAsync(bad).then(function() { }, function() { rejected = true; });
            ");
            Assert.True(rt.GetGlobal("rejected").ToBoolean());
        }

        [Fact]
        public void Array_FromAsync_ArrayLikePromiseValues_AwaitsElements()
        {
            var rt = CreateRuntime();
            RunWithMicrotasks(rt, @"
                var resolved = null;
                Array.fromAsync({
                    length: 2,
                    0: Promise.resolve(2),
                    1: Promise.resolve(1)
                }).then(function(arr) { resolved = arr; });
            ");

            var resolved = rt.GetGlobal("resolved").AsObject();
            Assert.NotNull(resolved);
            Assert.Equal(2.0, resolved.Get("0").ToNumber());
            Assert.Equal(1.0, resolved.Get("1").ToNumber());
        }

        [Fact]
        public void Array_FromAsync_ArrayLikeLengthObserver_CoercesValueOf_AndKeepsArrayPrototype()
        {
            var rt = CreateRuntime();
            RunWithMicrotasks(rt, @"
                var log = [];
                var lengthObserver = {
                    get valueOf() {
                        log.push('get length.valueOf');
                        return function() {
                            log.push('call length.valueOf');
                            return 2;
                        };
                    }
                };
                var items = {};
                Object.defineProperty(items, Symbol.asyncIterator, {
                    get: function() {
                        log.push('get Symbol.asyncIterator');
                        return undefined;
                    }
                });
                Object.defineProperty(items, Symbol.iterator, {
                    get: function() {
                        log.push('get Symbol.iterator');
                        return undefined;
                    }
                });
                Object.defineProperty(items, 'length', {
                    get: function() {
                        log.push('get length');
                        return lengthObserver;
                    }
                });
                Object.defineProperty(items, '0', {
                    get: function() {
                        log.push('get 0');
                        return Promise.resolve(2);
                    }
                });
                Object.defineProperty(items, '1', {
                    get: function() {
                        log.push('get 1');
                        return Promise.resolve(1);
                    }
                });

                var resolved = null;
                var joinType = 'missing';
                var usesArrayProto = false;
                var logText = '';
                Array.fromAsync(items).then(function(arr) {
                    resolved = arr;
                    joinType = typeof arr.join;
                    usesArrayProto = Object.getPrototypeOf(arr) === Array.prototype;
                    logText = log.join('|');
                });
            ");

            var resolved = rt.GetGlobal("resolved").AsObject();
            Assert.NotNull(resolved);
            Assert.Equal(2.0, resolved.Get("length").ToNumber());
            Assert.Equal(2.0, resolved.Get("0").ToNumber());
            Assert.Equal(1.0, resolved.Get("1").ToNumber());
            Assert.Equal("function", rt.GetGlobal("joinType").ToString());
            Assert.True(rt.GetGlobal("usesArrayProto").ToBoolean());
            Assert.Equal(
                "get Symbol.asyncIterator|get Symbol.iterator|get length|get length.valueOf|call length.valueOf|get 0|get 1",
                rt.GetGlobal("logText").ToString());
        }

        [Fact]
        public void Promise_All_ResultUsesArrayPrototype()
        {
            var rt = CreateRuntime();
            RunWithMicrotasks(rt, @"
                var resolved = null;
                var joinType = 'missing';
                var joined = 'missing';
                var usesArrayProto = false;
                Promise.all([Promise.resolve(2), Promise.resolve(1)]).then(function(arr) {
                    resolved = arr;
                    joinType = typeof arr.join;
                    joined = arr.join(', ');
                    usesArrayProto = Object.getPrototypeOf(arr) === Array.prototype;
                });
            ");

            var resolved = rt.GetGlobal("resolved").AsObject();
            Assert.NotNull(resolved);
            Assert.Equal(2.0, resolved.Get("length").ToNumber());
            Assert.Equal("function", rt.GetGlobal("joinType").ToString());
            Assert.Equal("2, 1", rt.GetGlobal("joined").ToString());
            Assert.True(rt.GetGlobal("usesArrayProto").ToBoolean());
        }

        [Fact]
        public void Array_FromAsync_ObservedAsyncIteratorReturningSyncGenerator_Resolves()
        {
            var rt = CreateRuntime();
            RunWithMicrotasks(rt, @"
                function formatPropertyName(propertyKey, objectName) {
                    if (typeof propertyKey === 'symbol') {
                        if (propertyKey.description.startsWith('Symbol.')) {
                            return objectName + '[' + propertyKey.description + ']';
                        }
                        return objectName + '[Symbol(' + propertyKey.description + ')]';
                    }
                    return objectName ? objectName + '.' + propertyKey : propertyKey;
                }

                function observeProperty(calls, object, propertyName, value, objectName) {
                    Object.defineProperty(object, propertyName, {
                        get() {
                            calls.push('get ' + formatPropertyName(propertyName, objectName));
                            return value;
                        },
                        set() {
                            calls.push('set ' + formatPropertyName(propertyName, objectName));
                        }
                    });
                }

                function* syncGen() {
                    for (let i = 0; i < 4; i++) {
                        yield i * 2;
                    }
                }

                var actual = [];
                var items = {};
                observeProperty(actual, items, Symbol.asyncIterator, syncGen, 'items');
                observeProperty(actual, items, Symbol.iterator, undefined, 'items');
                observeProperty(actual, items, 'length', 2, 'items');
                observeProperty(actual, items, 0, 2, 'items');
                observeProperty(actual, items, 1, 1, 'items');

                var resolved = null;
                var rejected = '';
                var joinType = 'missing';
                var logText = '';
                Array.fromAsync(items).then(
                    function(arr) {
                        resolved = arr;
                        joinType = typeof arr.join;
                        logText = actual.join('|');
                    },
                    function(err) {
                        rejected = String(err);
                    }
                );
            ");

            Assert.Equal(string.Empty, rt.GetGlobal("rejected").ToString());
            var resolved = rt.GetGlobal("resolved").AsObject();
            Assert.NotNull(resolved);
            Assert.Equal(4.0, resolved.Get("length").ToNumber());
            Assert.Equal(6.0, resolved.Get("3").ToNumber());
            Assert.Equal("function", rt.GetGlobal("joinType").ToString());
            Assert.Equal("get items[Symbol.asyncIterator]", rt.GetGlobal("logText").ToString());
        }

        [Fact]
        public void GeneratorFunction_Invoke_ReturnsGeneratorObject()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                function* syncGen() {
                    yield 1;
                    yield 2;
                }
            ");

            var syncGen = rt.GetGlobal("syncGen").AsFunction();
            Assert.NotNull(syncGen);

            var invoked = syncGen.Invoke(Array.Empty<FenValue>(), rt.Context, FenValue.Undefined);
            Assert.True(invoked.IsObject);

            var iterator = invoked.AsObject();
            Assert.NotNull(iterator);
            Assert.True(iterator.Get("next").IsFunction);

            var first = iterator.Get("next").AsFunction().Invoke(Array.Empty<FenValue>(), rt.Context, FenValue.FromObject(iterator));
            Assert.True(first.IsObject);
            Assert.Equal(1.0, first.AsObject().Get("value").ToNumber());
            Assert.False(first.AsObject().Get("done").ToBoolean());
        }

        [Fact]
        public void Array_FromAsync_PropertyBagObserver_ArrayLikePromiseValues_Resolves()
        {
            var rt = CreateRuntime();
            RunWithMicrotasks(rt, @"
                function formatPropertyName(propertyKey, objectName) {
                    if (typeof propertyKey === 'symbol') {
                        if (propertyKey.description.startsWith('Symbol.')) {
                            return objectName + '[' + propertyKey.description + ']';
                        }
                        return objectName + '[Symbol(' + propertyKey.description + ')]';
                    }
                    if (typeof propertyKey === 'string' && propertyKey !== String(Number(propertyKey))) {
                        return objectName ? objectName + '.' + propertyKey : propertyKey;
                    }
                    return objectName + '[' + propertyKey + ']';
                }

                function toPrimitiveObserver(calls, primitiveValue, propertyName) {
                    return {
                        get valueOf() {
                            calls.push('get ' + propertyName + '.valueOf');
                            return function () {
                                calls.push('call ' + propertyName + '.valueOf');
                                return primitiveValue;
                            };
                        }
                    };
                }

                function propertyBagObserver(calls, propertyBag, objectName) {
                    return new Proxy(propertyBag, {
                        get(target, key, receiver) {
                            calls.push('get ' + formatPropertyName(key, objectName));
                            const result = Reflect.get(target, key, receiver);
                            if (result === undefined) {
                                return undefined;
                            }
                            if ((result !== null && typeof result === 'object') || typeof result === 'function') {
                                return result;
                            }
                            return toPrimitiveObserver(calls, result, formatPropertyName(key, objectName));
                        }
                    });
                }

                var actual = [];
                var items = propertyBagObserver(actual, {
                    length: 2,
                    0: Promise.resolve(2),
                    1: Promise.resolve(1)
                }, 'items');

                var resolved = null;
                var rejected = '';
                var logText = '';
                Array.fromAsync(items).then(
                    function(arr) {
                        resolved = arr;
                        logText = actual.join('|');
                    },
                    function(err) {
                        rejected = String(err);
                    }
                );
            ");

            Assert.Equal(string.Empty, rt.GetGlobal("rejected").ToString());
            var resolved = rt.GetGlobal("resolved").AsObject();
            Assert.NotNull(resolved);
            Assert.Equal(2.0, resolved.Get("0").ToNumber());
            Assert.Equal(1.0, resolved.Get("1").ToNumber());
            Assert.Equal(
                "get items[Symbol.asyncIterator]|get items[Symbol.iterator]|get items.length|get items.length.valueOf|call items.length.valueOf|get items[0]|get items[1]",
                rt.GetGlobal("logText").ToString());
        }

        [Fact]
        public void Reflect_Get_OnProxy_PreservesSymbolPropertyKeys()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var sawSymbol = false;
                var target = {};
                Object.defineProperty(target, Symbol.asyncIterator, {
                    value: 42,
                    configurable: true
                });
                var proxy = new Proxy(target, {
                    get(target, key, receiver) {
                        sawSymbol = key === Symbol.asyncIterator;
                        return Reflect.get(target, key, receiver);
                    }
                });
                var proxyValue = Reflect.get(proxy, Symbol.asyncIterator);
                var proxySymbolGetOut = sawSymbol && proxyValue === 42;
            ");

            Assert.True(rt.GetGlobal("proxySymbolGetOut").ToBoolean());
        }

        [Fact]
        public void RegExp_Literal_Inherits_RegExpPrototype_Methods()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var regexTestType = typeof /^[$_a-zA-Z][$_a-zA-Z0-9]*$/u.test;
                var regexTestOut = /^[$_a-zA-Z][$_a-zA-Z0-9]*$/u.test('length');
            ");

            Assert.Equal("function", rt.GetGlobal("regexTestType").ToString());
            Assert.True(rt.GetGlobal("regexTestOut").ToBoolean());
        }

        [Fact]
        public void String_Global_RemainsCallableFunction_AfterStaticMethodMerge()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var stringType = typeof String;
                var stringCallType = typeof String.call;
                var stringInvokeOut = String(42);
            ");

            Assert.Equal("function", rt.GetGlobal("stringType").ToString());
            Assert.Equal("function", rt.GetGlobal("stringCallType").ToString());
            Assert.Equal("42", rt.GetGlobal("stringInvokeOut").ToString());
        }

        [Fact]
        public void Array_PrototypeMap_ExposesFunctionPrototypeCall()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var mapType = typeof Array.prototype.map;
                var mapCallType = typeof Array.prototype.map.call;
            ");

            Assert.Equal("function", rt.GetGlobal("mapType").ToString());
            Assert.Equal("function", rt.GetGlobal("mapCallType").ToString());
        }

        [Fact]
        public void Array_PrototypeMap_Call_WithStringConstructor_ReturnsMappedArray()
        {
            var rt = CreateRuntime();
            var arrayCtor = rt.GetGlobal("Array").AsFunction();
            var arrayPrototype = arrayCtor?.Get("prototype").AsObject();
            var realmArrayPrototype = typeof(FenRuntime)
                .GetField("_realmArrayPrototype", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(rt);

            Assert.NotNull(arrayCtor);
            Assert.NotNull(arrayPrototype);
            Assert.True(arrayPrototype.Get("map").IsFunction);
            Assert.True(arrayPrototype.Get("join").IsFunction);
            Assert.Same(arrayPrototype, realmArrayPrototype);

            var result = (FenValue)rt.ExecuteSimple(@"
                var literalJoinType = typeof [1, 2].join;
                var mapResult = Array.prototype.map.call({ length: 2, 0: 2, 1: 1 }, String);
                var mapResultType = typeof mapResult;
                var mapResult0 = mapResult[0];
                var mapResult1 = mapResult[1];
                var mapResultProtoJoinType = typeof Object.getPrototypeOf(mapResult).join;
                var mapResultUsesArrayProto = Object.getPrototypeOf(mapResult) === Array.prototype;
                var joinType = typeof mapResult.join;
            ");

            Assert.NotEqual(FenBrowser.FenEngine.Core.Interfaces.ValueType.Throw, result.Type);
            Assert.Equal("function", rt.GetGlobal("literalJoinType").ToString());
            Assert.Equal("object", rt.GetGlobal("mapResultType").ToString());
            Assert.Equal("2", rt.GetGlobal("mapResult0").ToString());
            Assert.Equal("1", rt.GetGlobal("mapResult1").ToString());
            Assert.Equal("function", rt.GetGlobal("mapResultProtoJoinType").ToString());
            Assert.True(rt.GetGlobal("mapResultUsesArrayProto").ToBoolean());
            Assert.Equal("function", rt.GetGlobal("joinType").ToString());
        }

        [Fact]
        public void Array_PrototypeMap_Call_WithStringConstructor_FormatsArrayLike()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var formatted = Array.prototype.map.call({ length: 2, 0: 2, 1: 1 }, String).join(', ');
            ");

            Assert.Equal("2, 1", rt.GetGlobal("formatted").ToString());
        }
        // ==================== JS-2b: Iterator.prototype TESTS ====================

        [Fact]
        public void Iterator_Prototype_HasMapMethod()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var iter = Iterator.from([1, 2, 3]);
                var hasMap = typeof iter.map === 'function';
            ");
            Assert.True(rt.GetGlobal("hasMap").ToBoolean());
        }

        [Fact]
        public void Iterator_Prototype_IsSharedAcrossInstances()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var iter1 = Iterator.from([1]);
                var iter2 = Iterator.from([2]);
                var sameProto = Object.getPrototypeOf(iter1) === Object.getPrototypeOf(iter2);
            ");
            Assert.True(rt.GetGlobal("sameProto").ToBoolean());
        }

        [Fact]
        public void Iterator_Prototype_Map_UsesSharedMethod()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var iter1 = Iterator.from([1]);
                var iter2 = Iterator.from([2]);
                var sameMap = iter1.map === iter2.map;
            ");
            Assert.True(rt.GetGlobal("sameMap").ToBoolean());
        }

        [Fact]
        public void Array_Iterator_HasIteratorPrototype()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var arrIter = [1,2,3][Symbol.iterator]();
                var iterFromIter = Iterator.from([10]);
                var sameProto = Object.getPrototypeOf(arrIter) === Object.getPrototypeOf(iterFromIter);
            ");
            Assert.True(rt.GetGlobal("sameProto").ToBoolean());
        }

        [Fact]
        public void Array_FromAsync_Rejects_WithRealTypeError_ForNonCallableAsyncIterator()
        {
            var rt = CreateRuntime();
            RunWithMicrotasks(rt, @"
                var rejectedName = '';
                Array.fromAsync({ [Symbol.asyncIterator]: 1 }).then(
                    function() {},
                    function(err) { rejectedName = err && err.name; }
                );
            ");

            Assert.Equal("TypeError", rt.GetGlobal("rejectedName").ToString());
        }

        [Fact]
        public void Array_FromAsync_Preserves_ThrownRejectionReason()
        {
            var rt = CreateRuntime();
            RunWithMicrotasks(rt, @"
                var reasonName = '';
                var reasonMessage = '';
                var expected = new Error('boom');
                var items = {
                    [Symbol.asyncIterator]: function() {
                        return {
                            next: function() {
                                throw expected;
                            }
                        };
                    }
                };
                Array.fromAsync(items).then(
                    function() {},
                    function(err) {
                        reasonName = err && err.name;
                        reasonMessage = err && err.message;
                    }
                );
            ");

            Assert.Equal("Error", rt.GetGlobal("reasonName").ToString());
            Assert.Equal("boom", rt.GetGlobal("reasonMessage").ToString());
        }

        [Fact]
        public void RegExp_SymbolMatch_IsInstalled_And_Returns_GroupsObject()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var re = /(?<group>a)(b)/;
                var result = re[Symbol.match]('ab');
                var hasMethod = typeof RegExp.prototype[Symbol.match] === 'function';
                var capture0 = result[0];
                var groupValue = result.groups.group;
            ");

            Assert.True(rt.GetGlobal("hasMethod").ToBoolean());
            Assert.Equal("ab", rt.GetGlobal("capture0").ToString());
            Assert.Equal("a", rt.GetGlobal("groupValue").ToString());
        }

        [Fact]
        public void Iterator_Map_WorksViaPrototype()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var arr = Iterator.from([1, 2, 3]).map(function(x) { return x * 10; }).toArray();
                var sum = arr[0] + arr[1] + arr[2];
            ");
            Assert.Equal(60.0, rt.GetGlobal("sum").ToNumber());
        }

        // ==================== JS-3: Symbol.dispose + DisposableStack TESTS ====================

        [Fact]
        public void Symbol_Dispose_IsWellKnownSymbol()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var hasDispose = typeof Symbol.dispose !== 'undefined';
                var disposeType = typeof Symbol.dispose;
            ");
            Assert.True(rt.GetGlobal("hasDispose").ToBoolean());
            Assert.Equal("symbol", rt.GetGlobal("disposeType").ToString());
        }

        [Fact]
        public void DisposableStack_Defer_CallsFn()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var called = false;
                var ds = new DisposableStack();
                ds.defer(function() { called = true; });
                ds[Symbol.dispose]();
            ");
            Assert.True(rt.GetGlobal("called").ToBoolean());
        }

        [Fact]
        public void DisposableStack_Adopt_CallsOnDispose()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var receivedVal = null;
                var ds = new DisposableStack();
                ds.adopt(42, function(v) { receivedVal = v; });
                ds[Symbol.dispose]();
            ");
            Assert.Equal(42.0, rt.GetGlobal("receivedVal").ToNumber());
        }

        [Fact]
        public void DisposableStack_Disposes_InLIFO_Order()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var order = [];
                var ds = new DisposableStack();
                ds.defer(function() { order.push(1); });
                ds.defer(function() { order.push(2); });
                ds.defer(function() { order.push(3); });
                ds[Symbol.dispose]();
                var first = order[0];
                var last = order[2];
            ");
            Assert.Equal(3.0, rt.GetGlobal("first").ToNumber());
            Assert.Equal(1.0, rt.GetGlobal("last").ToNumber());
        }

        [Fact]
        public void DisposableStack_Use_CallsSymbolDispose()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var disposed = false;
                var resource = {};
                resource[Symbol.dispose] = function() { disposed = true; };
                var ds = new DisposableStack();
                ds.use(resource);
                ds[Symbol.dispose]();
            ");
            Assert.True(rt.GetGlobal("disposed").ToBoolean());
        }

        // ==================== JS-4: Array.from iterable protocol TESTS ====================

        [Fact]
        public void Array_From_MapIterable_Works()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var m = new Map();
                m.set('a', 1);
                m.set('b', 2);
                var arr = Array.from(m);
                var len = arr.length;
            ");
            Assert.Equal(2.0, rt.GetGlobal("len").ToNumber());
        }

        [Fact]
        public void Array_From_SetIterable_Works()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var s = new Set([10, 20, 30]);
                var arr = Array.from(s);
                var len = arr.length;
                var first = arr[0];
            ");
            Assert.Equal(3.0, rt.GetGlobal("len").ToNumber());
            Assert.Equal(10.0, rt.GetGlobal("first").ToNumber());
        }

        [Fact]
        public void Array_From_GeneratorIterable_Works()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                function* gen() { yield 5; yield 10; yield 15; }
                var arr = Array.from(gen());
                var len = arr.length;
                var sum = arr[0] + arr[1] + arr[2];
            ");
            Assert.Equal(3.0, rt.GetGlobal("len").ToNumber());
            Assert.Equal(30.0, rt.GetGlobal("sum").ToNumber());
        }
    }
}

