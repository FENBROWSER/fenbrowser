using System;
using System.Collections.Generic;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    /// <summary>
    /// JS-2: Promise A+ Spec Conformance regression tests.
    /// Validates that Promise.all/allSettled/race/any/finally behave per ECMAScript spec.
    /// </summary>
    [Collection("Engine Tests")]
    public class PromiseConformanceTests
    {
        public PromiseConformanceTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        private FenRuntime CreateRuntime() => new FenRuntime();

        private void Run(FenRuntime rt, string code)
        {
            rt.ExecuteSimple(code);
            EventLoopCoordinator.Instance.RunUntilEmpty();
        }

        [Fact]
        public void Promise_Resolve_ReturnsValue()
        {
            var rt = CreateRuntime();
            Run(rt, @"
                var resolved;
                Promise.resolve(42).then(function(v) { resolved = v; });
            ");
            var v = rt.GetGlobal("resolved");
            Assert.Equal(42.0, v.ToNumber());
        }

        [Fact]
        public void Promise_Reject_InvokesCatch()
        {
            var rt = CreateRuntime();
            Run(rt, @"
                var caught;
                Promise.reject('err').catch(function(r) { caught = r; });
            ");
            var v = rt.GetGlobal("caught");
            Assert.Equal("err", v.ToString());
        }

        [Fact]
        public void Promise_All_ResolvesWhenAllResolve()
        {
            var rt = CreateRuntime();
            Run(rt, @"
                var result;
                Promise.all([
                    Promise.resolve(1),
                    Promise.resolve(2),
                    Promise.resolve(3)
                ]).then(function(vals) { result = vals; });
            ");
            var r = rt.GetGlobal("result");
            Assert.True(r.IsObject, "result should be an array object");
            var arr = r.AsObject();
            Assert.NotNull(arr);
            Assert.Equal(1.0, arr.Get("0").ToNumber());
            Assert.Equal(2.0, arr.Get("1").ToNumber());
            Assert.Equal(3.0, arr.Get("2").ToNumber());
        }

        [Fact]
        public void Promise_All_RejectsOnFirstRejection()
        {
            var rt = CreateRuntime();
            Run(rt, @"
                var reason;
                Promise.all([
                    Promise.resolve(1),
                    Promise.reject('FAIL'),
                    Promise.resolve(3)
                ]).catch(function(r) { reason = r; });
            ");
            var v = rt.GetGlobal("reason");
            Assert.Equal("FAIL", v.ToString());
        }

        [Fact]
        public void Promise_AllSettled_AlwaysResolves()
        {
            var rt = CreateRuntime();
            Run(rt, @"
                var results;
                Promise.allSettled([
                    Promise.resolve(1),
                    Promise.reject('nope')
                ]).then(function(r) { results = r; });
            ");
            var arr = rt.GetGlobal("results").AsObject();
            Assert.NotNull(arr);
            var first = arr.Get("0").AsObject();
            Assert.NotNull(first);
            Assert.Equal("fulfilled", first.Get("status").ToString());
            Assert.Equal(1.0, first.Get("value").ToNumber());
            var second = arr.Get("1").AsObject();
            Assert.NotNull(second);
            Assert.Equal("rejected", second.Get("status").ToString());
            Assert.Equal("nope", second.Get("reason").ToString());
        }

        [Fact]
        public void Promise_Race_SettlesOnFirst()
        {
            var rt = CreateRuntime();
            Run(rt, @"
                var winner;
                Promise.race([
                    Promise.resolve('first'),
                    Promise.resolve('second')
                ]).then(function(v) { winner = v; });
            ");
            var v = rt.GetGlobal("winner");
            Assert.Equal("first", v.ToString());
        }

        [Fact]
        public void Promise_Any_ResolvesWithFirstFulfilled()
        {
            var rt = CreateRuntime();
            Run(rt, @"
                var result;
                Promise.any([
                    Promise.reject('e1'),
                    Promise.resolve('win'),
                    Promise.resolve('also')
                ]).then(function(v) { result = v; });
            ");
            var v = rt.GetGlobal("result");
            Assert.Equal("win", v.ToString());
        }

        [Fact]
        public void Promise_Finally_CalledOnFulfill()
        {
            var rt = CreateRuntime();
            Run(rt, @"
                var finallyCalled = false;
                var finalValue;
                Promise.resolve('test')
                    .finally(function() { finallyCalled = true; })
                    .then(function(v) { finalValue = v; });
            ");
            Assert.True(rt.GetGlobal("finallyCalled").ToBoolean());
            Assert.Equal("test", rt.GetGlobal("finalValue").ToString());
        }

        [Fact]
        public void Promise_Finally_CalledOnReject()
        {
            var rt = CreateRuntime();
            Run(rt, @"
                var finallyCalled = false;
                var caughtReason;
                Promise.reject('boom')
                    .finally(function() { finallyCalled = true; })
                    .catch(function(r) { caughtReason = r; });
            ");
            Assert.True(rt.GetGlobal("finallyCalled").ToBoolean());
            Assert.Equal("boom", rt.GetGlobal("caughtReason").ToString());
        }

        [Fact]
        public void Promise_Then_ChainPassesValue()
        {
            var rt = CreateRuntime();
            Run(rt, @"
                var last;
                Promise.resolve(1)
                    .then(function(v) { return v + 1; })
                    .then(function(v) { return v * 10; })
                    .then(function(v) { last = v; });
            ");
            Assert.Equal(20.0, rt.GetGlobal("last").ToNumber());
        }

        [Fact]
        public void Promise_Microtask_ScheduledAfterCurrentSync()
        {
            var rt = CreateRuntime();
            Run(rt, @"
                var order = [];
                order.push('sync1');
                Promise.resolve().then(function() { order.push('micro1'); });
                order.push('sync2');
            ");
            var arr = rt.GetGlobal("order").AsObject();
            Assert.NotNull(arr);
            Assert.Equal("sync1", arr.Get("0").ToString());
            Assert.Equal("sync2", arr.Get("1").ToString());
            Assert.Equal("micro1", arr.Get("2").ToString());
        }

        [Fact]
        public void Promise_RealmBranding_UsesNativePromisePrototypeAcrossFactoriesAndChains()
        {
            var rt = CreateRuntime();
            Run(rt, @"
                var resolved = Promise.resolve(1);
                var chained = resolved.then(function(v) { return v + 1; });
                var rejected = Promise.reject('boom').catch(function(reason) { return reason; });

                var resolveUsesPromiseProto = Object.getPrototypeOf(resolved) === Promise.prototype;
                var resolveIsPromise = resolved instanceof Promise;
                var chainUsesPromiseProto = Object.getPrototypeOf(chained) === Promise.prototype;
                var chainIsPromise = chained instanceof Promise;
                var catchUsesPromiseProto = Object.getPrototypeOf(rejected) === Promise.prototype;
                var catchIsPromise = rejected instanceof Promise;
            ");

            Assert.True(rt.GetGlobal("resolveUsesPromiseProto").ToBoolean());
            Assert.True(rt.GetGlobal("resolveIsPromise").ToBoolean());
            Assert.True(rt.GetGlobal("chainUsesPromiseProto").ToBoolean());
            Assert.True(rt.GetGlobal("chainIsPromise").ToBoolean());
            Assert.True(rt.GetGlobal("catchUsesPromiseProto").ToBoolean());
            Assert.True(rt.GetGlobal("catchIsPromise").ToBoolean());
        }
    }
}
