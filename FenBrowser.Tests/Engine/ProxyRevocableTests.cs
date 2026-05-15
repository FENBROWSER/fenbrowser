using FenBrowser.FenEngine.Core;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    // Spec: ECMA-262 §10.5 / §28.2.2.1 (Proxy.revocable) — after revoke() runs,
    // every internal method on the proxy throws TypeError.
    [Collection("Engine Tests")]
    public class ProxyRevocableTests
    {
        [Fact]
        public void Revocable_ReturnsObjectWithProxyAndRevoke()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                var r = Proxy.revocable({x: 1}, {});
                var hasProxy = typeof r.proxy === 'object';
                var hasRevoke = typeof r.revoke === 'function';
            ");

            Assert.True(runtime.GetGlobal("hasProxy").ToBoolean());
            Assert.True(runtime.GetGlobal("hasRevoke").ToBoolean());
        }

        [Fact]
        public void Revocable_GetTrap_FiresBeforeRevoke()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                var trapped = false;
                var r = Proxy.revocable({}, {
                    get: function(t, k) { trapped = true; return 42; }
                });
                var v = r.proxy.foo;
            ");

            Assert.True(runtime.GetGlobal("trapped").ToBoolean());
            Assert.Equal(42d, runtime.GetGlobal("v").ToNumber());
        }

        [Fact]
        public void Revocable_GetTrap_ThrowsAfterRevoke()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                var r = Proxy.revocable({}, { get: function() { return 1; } });
                r.revoke();
                var threw = false;
                try { var v = r.proxy.foo; } catch (e) { threw = e instanceof TypeError; }
            ");

            Assert.True(runtime.GetGlobal("threw").ToBoolean(),
                "Accessing a revoked proxy must throw TypeError");
        }

        [Fact]
        public void Revocable_SetTrap_ThrowsAfterRevoke()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                var r = Proxy.revocable({}, { set: function() { return true; } });
                r.revoke();
                var threw = false;
                try { r.proxy.foo = 1; } catch (e) { threw = e instanceof TypeError; }
            ");

            Assert.True(runtime.GetGlobal("threw").ToBoolean());
        }

        [Fact]
        public void Revocable_HasTrap_ThrowsAfterRevoke()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                var r = Proxy.revocable({}, { has: function() { return true; } });
                r.revoke();
                var threw = false;
                try { var b = 'foo' in r.proxy; } catch (e) { threw = e instanceof TypeError; }
            ");

            Assert.True(runtime.GetGlobal("threw").ToBoolean());
        }

        [Fact]
        public void Revocable_DoubleRevoke_IsIdempotent()
        {
            var runtime = new FenRuntime();
            runtime.ExecuteSimple(@"
                var r = Proxy.revocable({}, {});
                r.revoke();
                var threw = false;
                try { r.revoke(); } catch (e) { threw = true; }
            ");

            Assert.False(runtime.GetGlobal("threw").ToBoolean(),
                "Calling revoke() on an already-revoked proxy must be a no-op, not an error");
        }
    }
}
