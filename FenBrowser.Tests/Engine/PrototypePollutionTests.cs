using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    // Spec: ECMA-262 §10.1.2.1 OrdinarySetPrototypeOf — closing a prototype-chain
    // cycle (or assigning to a non-extensible object's prototype) is a TypeError.
    [Collection("Engine Tests")]
    public class PrototypePollutionTests
    {
        [Fact]
        public void SetPrototypeOf_DirectCycle_Throws()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                var a = {};
                var threw = false;
                try { Object.setPrototypeOf(a, a); }
                catch (e) { threw = e instanceof TypeError; }
            ");

            Assert.True(runtime.GetGlobal("threw").ToBoolean(),
                "Setting an object's prototype to itself must throw TypeError");
        }

        [Fact]
        public void SetPrototypeOf_IndirectCycle_Throws()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                var a = {};
                var b = {};
                Object.setPrototypeOf(a, b);
                var threw = false;
                try { Object.setPrototypeOf(b, a); }
                catch (e) { threw = e instanceof TypeError; }
            ");

            Assert.True(runtime.GetGlobal("threw").ToBoolean(),
                "Closing a two-step __proto__ cycle must throw TypeError");
        }

        [Fact]
        public void SetPrototypeOf_NonExtensible_Throws()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                var a = {};
                var b = {};
                Object.preventExtensions(a);
                var threw = false;
                try { Object.setPrototypeOf(a, b); }
                catch (e) { threw = e instanceof TypeError; }
            ");

            Assert.True(runtime.GetGlobal("threw").ToBoolean(),
                "setPrototypeOf on a non-extensible object must throw TypeError");
        }

        [Fact]
        public void SetPrototypeOf_SameProto_NonExtensible_DoesNotThrow()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                var a = {};
                Object.preventExtensions(a);
                var proto = Object.getPrototypeOf(a);
                var threw = false;
                try { Object.setPrototypeOf(a, proto); }
                catch (e) { threw = true; }
            ");

            Assert.False(runtime.GetGlobal("threw").ToBoolean(),
                "setPrototypeOf to the same prototype is a no-op even when non-extensible");
        }

        [Fact]
        public void PreventExtensions_StrictMode_AddingPropertyThrows()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                'use strict';
                var a = {};
                Object.preventExtensions(a);
                var threw = false;
                try { a.x = 1; }
                catch (e) { threw = e instanceof TypeError; }
            ");

            Assert.True(runtime.GetGlobal("threw").ToBoolean(),
                "Adding to a non-extensible object in strict mode must throw TypeError");
        }
    }
}
