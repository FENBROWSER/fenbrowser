using System;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// JS-6: Map/Set spec-complete iteration regression tests.
    /// Validates Map and Set creation, iteration, forEach callback signatures.
    /// </summary>
    [Collection("Engine Tests")]
    public class MapSetConformanceTests
    {
        public MapSetConformanceTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        private FenRuntime CreateRuntime() => new FenRuntime();

        // ==================== MAP TESTS ====================

        [Fact]
        public void Map_BasicSetAndGet()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var m = new Map(); m.set('key', 'value'); var v = m.get('key');");
            Assert.Equal("value", rt.GetGlobal("v").ToString());
        }

        [Fact]
        public void Map_Size_IncrementsOnSet()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var m = new Map(); m.set('a', 1); m.set('b', 2); var s = m.size;");
            Assert.Equal(2.0, rt.GetGlobal("s").ToNumber());
        }

        [Fact]
        public void Map_Has_ReturnsTrueForExistingKey()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var m = new Map(); m.set('x', 1); var h = m.has('x');");
            Assert.True(rt.GetGlobal("h").ToBoolean());
        }

        [Fact]
        public void Map_Delete_RemovesKey()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var m = new Map(); m.set('x', 1); m.delete('x'); var h = m.has('x'); var s = m.size;");
            Assert.False(rt.GetGlobal("h").ToBoolean());
            Assert.Equal(0.0, rt.GetGlobal("s").ToNumber());
        }

        [Fact]
        public void Map_ForEach_CallbackReceivesValueKeyMap()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var m = new Map();
                m.set('alpha', 100);
                var callbackValue;
                var callbackKey;
                var callbackMap;
                m.forEach(function(val, key, map) {
                    callbackValue = val;
                    callbackKey = key;
                    callbackMap = map;
                });
            ");
            Assert.Equal(100.0, rt.GetGlobal("callbackValue").ToNumber());
            Assert.Equal("alpha", rt.GetGlobal("callbackKey").ToString());
            Assert.True(rt.GetGlobal("callbackMap").IsObject);
        }

        [Fact]
        public void Map_ObjectKeys_WorkCorrectly()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var m = new Map();
                var keyObj = { id: 1 };
                m.set(keyObj, 'objectValue');
                var v = m.get(keyObj);
            ");
            Assert.Equal("objectValue", rt.GetGlobal("v").ToString());
        }

        [Fact]
        public void Map_Chaining_SetReturnsSelf()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var m = new Map(); m.set('a', 1).set('b', 2).set('c', 3); var s = m.size;");
            Assert.Equal(3.0, rt.GetGlobal("s").ToNumber());
        }

        // ==================== SET TESTS ====================

        [Fact]
        public void Set_BasicAddAndHas()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var s = new Set();
                s.add(1);
                s.add('two');
                var h1 = s.has(1);
                var h2 = s.has('two');
                var h3 = s.has(99);
            ");
            Assert.True(rt.GetGlobal("h1").ToBoolean());
            Assert.True(rt.GetGlobal("h2").ToBoolean());
            Assert.False(rt.GetGlobal("h3").ToBoolean());
        }

        [Fact]
        public void Set_NoDuplicates()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = new Set(); s.add(1); s.add(1); s.add(1); var sz = s.size;");
            Assert.Equal(1.0, rt.GetGlobal("sz").ToNumber());
        }

        [Fact]
        public void Set_Delete_RemovesValue()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(
                "var s = new Set(); s.add('hello'); s.delete('hello'); var h = s.has('hello'); var sz = s.size;");
            Assert.False(rt.GetGlobal("h").ToBoolean());
            Assert.Equal(0.0, rt.GetGlobal("sz").ToNumber());
        }

        [Fact]
        public void Set_ForEach_CallbackReceivesValueValueSet()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var s = new Set();
                s.add(42);
                var arg0; var arg1;
                s.forEach(function(val, val2, set) {
                    arg0 = val;
                    arg1 = val2;
                });
            ");
            // For Set, both arg0 and arg1 should be the value (spec §23.2.3.6)
            Assert.Equal(42.0, rt.GetGlobal("arg0").ToNumber());
            Assert.Equal(42.0, rt.GetGlobal("arg1").ToNumber());
        }

        [Fact]
        public void Set_Chaining_AddReturnsSelf()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = new Set(); s.add(1).add(2).add(3); var sz = s.size;");
            Assert.Equal(3.0, rt.GetGlobal("sz").ToNumber());
        }

        [Fact]
        public void Set_Clear_RemovesAll()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple("var s = new Set(); s.add(1); s.add(2); s.clear(); var sz = s.size;");
            Assert.Equal(0.0, rt.GetGlobal("sz").ToNumber());
        }

        [Fact]
        public void Set_InitializedFromArray_ContainsAllElements()
        {
            var rt = CreateRuntime();
            rt.ExecuteSimple(@"
                var s = new Set([1, 2, 3, 2, 1]);
                var sz = s.size;
                var h1 = s.has(1);
                var h3 = s.has(3);
            ");
            Assert.Equal(3.0, rt.GetGlobal("sz").ToNumber());
            Assert.True(rt.GetGlobal("h1").ToBoolean());
            Assert.True(rt.GetGlobal("h3").ToBoolean());
        }
    }
}
