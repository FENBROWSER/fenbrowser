using System;
using FenBrowser.Core.Engine;
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
            Assert.Equal(5.0, rt.GetGlobal("len").ToNumber()); // [1,2,3,4,[5]] — 5 elements
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
            catch (InvalidOperationException) { /* already draining — ignore */ }
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
