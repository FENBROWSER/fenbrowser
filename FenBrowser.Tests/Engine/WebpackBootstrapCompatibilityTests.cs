using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public class WebpackBootstrapCompatibilityTests
    {
        public WebpackBootstrapCompatibilityTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        [Fact]
        public void DeferredChunkGate_RunsEntryWhenDependencyChunkMarkedLoaded()
        {
            var runtime = new FenRuntime();

            runtime.ExecuteSimple(@"
                var modules = { 54736: 0 };
                var deferred = [];
                var g = {
                  o: function(obj, key) { return Object.prototype.hasOwnProperty.call(obj, key); }
                };

                g.O = function(result, chunkIds, callback, priority) {
                  if (!chunkIds) {
                    var lowestPriority = 1 / 0;
                    for (var l = 0; l < deferred.length; l++) {
                      for (var entry = deferred[l], ids = entry[0], run = entry[1], entryPriority = entry[2], ready = true, i = 0; i < ids.length; i++) {
                        if ((!1 && entryPriority) || lowestPriority >= entryPriority) {
                          if (Object.keys(g.O).every(function(name) { return g.O[name](ids[i]); })) {
                            ids.splice(i--, 1);
                          } else {
                            ready = false;
                            if (entryPriority < lowestPriority) {
                              lowestPriority = entryPriority;
                            }
                          }
                        }
                      }

                      if (ready) {
                        deferred.splice(l--, 1);
                        var callbackResult = run();
                        if (callbackResult !== undefined) {
                          result = callbackResult;
                        }
                      }
                    }

                    return result;
                  }

                  priority = priority || 0;
                  for (var j = deferred.length; j > 0 && deferred[j - 1][2] > priority; j--) {
                    deferred[j] = deferred[j - 1];
                  }

                  deferred[j] = [chunkIds, callback, priority];
                };

                g.O.j = function(chunkId) { return modules[chunkId] === 0; };

                globalThis.__webpackCompatHits = 0;
                globalThis.__webpackCompatReturn = 'unset';
                globalThis.__webpackCompatDeferredLengthAfterQueue = -1;
                globalThis.__webpackCompatDeferredLengthAfterDrain = -1;
                globalThis.__webpackCompatGateKeysLength = -1;
                globalThis.__webpackCompatGateCheck = false;

                g.O(0, [54736], function() {
                  globalThis.__webpackCompatHits++;
                  return 'entry-ran';
                });

                globalThis.__webpackCompatDeferredLengthAfterQueue = deferred.length;
                globalThis.__webpackCompatGateKeysLength = Object.keys(g.O).length;
                globalThis.__webpackCompatGateCheck = Object.keys(g.O).every(function(name) { return g.O[name](54736); });

                globalThis.__webpackCompatReturn = g.O();
                globalThis.__webpackCompatDeferredLengthAfterDrain = deferred.length;
            ");

            Assert.Equal(1.0, runtime.GetGlobal("__webpackCompatHits").ToNumber());
            Assert.Equal("entry-ran", runtime.GetGlobal("__webpackCompatReturn").ToString());
        }

        [Fact]
        public void ArrayIndexAssignment_UpdatesLengthForDeferredBootstrapQueues()
        {
            var runtime = new FenRuntime();

            runtime.ExecuteSimple(@"
                var items = [];
                items[0] = 'first';
                items[3] = 'last';
                globalThis.__arrayLength = items.length;
                globalThis.__arrayZero = items[0];
                globalThis.__arrayThree = items[3];
            ");

            Assert.Equal(4.0, runtime.GetGlobal("__arrayLength").ToNumber());
            Assert.Equal("first", runtime.GetGlobal("__arrayZero").ToString());
            Assert.Equal("last", runtime.GetGlobal("__arrayThree").ToString());
        }

        [Fact]
        public void FunctionPropertyAssignment_IsVisibleToObjectKeys()
        {
            var runtime = new FenRuntime();

            runtime.ExecuteSimple(@"
                function gate() { return true; }
                gate.j = function() { return true; };
                globalThis.__gateKeysLength = Object.keys(gate).length;
                globalThis.__gateEvery = Object.keys(gate).every(function(name) { return gate[name](); });
            ");

            Assert.Equal(1.0, runtime.GetGlobal("__gateKeysLength").ToNumber());
            Assert.Equal(true, runtime.GetGlobal("__gateEvery").ToBoolean());
        }

        [Fact]
        public void DescriptorProbe_AfterObjectKeys_DoesNotReturnUndefinedForOwnKey()
        {
            var runtime = new FenRuntime();

            runtime.ExecuteSimple(@"
                var gate = {};
                gate.j = function() { return true; };
                var key = Object.keys(gate)[0];
                var descriptor = Object.getOwnPropertyDescriptor(gate, key);
                globalThis.__descriptorExists = !!descriptor;
                globalThis.__descriptorWritableType = typeof descriptor.writable;
            ");

            Assert.Equal(true, runtime.GetGlobal("__descriptorExists").ToBoolean());
            Assert.Equal("boolean", runtime.GetGlobal("__descriptorWritableType").ToString());
        }

        [Fact]
        public void DescriptorOrEmptyObject_WritableRead_DoesNotThrow()
        {
            var runtime = new FenRuntime();

            runtime.ExecuteSimple(@"
                globalThis.__descriptorOrOk = false;
                globalThis.__descriptorOrType = 'unset';
                try {
                    var maybe = Object.getOwnPropertyDescriptor(function demo(){}, 'missing');
                    var writable = (maybe || {}).writable;
                    globalThis.__descriptorOrType = typeof writable;
                    globalThis.__descriptorOrOk = true;
                } catch (e) {
                    globalThis.__descriptorOrOk = false;
                    globalThis.__descriptorOrType = String(e && e.message || e);
                }
            ");

            Assert.Equal(true, runtime.GetGlobal("__descriptorOrOk").ToBoolean());
            Assert.Equal("undefined", runtime.GetGlobal("__descriptorOrType").ToString());
        }

        [Fact]
        public void SyntheticTempNameCollision_DoesNotClobberUserBinding()
        {
            var runtime = new FenRuntime();

            runtime.ExecuteSimple(@"
                var __fenbc_object_literal_0 = 41;
                var out = (__fenbc_object_literal_0 || {}).writable;
                globalThis.__collisionBinding = __fenbc_object_literal_0;
                globalThis.__collisionType = typeof out;
            ");

            Assert.Equal(41.0, runtime.GetGlobal("__collisionBinding").ToNumber());
            Assert.Equal("undefined", runtime.GetGlobal("__collisionType").ToString());
        }

        [Fact]
        public void SyntheticMethodTempNameCollision_DoesNotBreakObjectLiteralConstruction()
        {
            var runtime = new FenRuntime();

            runtime.ExecuteSimple(@"
                var __fenbc_object_method_0 = 7;
                var box = {
                  run: function() { return 11; }
                };
                globalThis.__methodCollisionSentinel = __fenbc_object_method_0;
                globalThis.__methodCollisionResult = box.run();
            ");

            Assert.Equal(7.0, runtime.GetGlobal("__methodCollisionSentinel").ToNumber());
            Assert.Equal(11.0, runtime.GetGlobal("__methodCollisionResult").ToNumber());
        }
    }
}
