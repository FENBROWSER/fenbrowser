using System;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Testing;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    [Collection("Engine Tests")]
    public sealed class Test262RunnerTests
    {
        [Fact]
        public void ParseMetadata_RawFlag_SetsIsRaw()
        {
            var metadata = Test262Runner.ParseMetadata("""
/*---
description: raw metadata parse
flags: [raw, module]
---*/
""");

            Assert.True(metadata.IsRaw);
            Assert.True(metadata.IsModule);
        }

        [Fact]
        public async Task RunSingleTestAsync_RawFlag_DoesNotInjectHarnessPrelude()
        {
            using var fixture = CreateFixture(
                assertJs: "globalThis.__assertInjected = true;",
                staJs: "globalThis.__staInjected = true;");

            string testFile = fixture.WriteTest("raw-harness.js", """
/*---
description: raw tests must execute without injected harness prelude
flags: [raw]
---*/
if (typeof __assertInjected !== 'undefined' || typeof __staInjected !== 'undefined') {
  throw new Error('harness leaked');
}
""");

            var runner = new Test262Runner(fixture.RootPath, timeoutMs: 5_000);
            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_NegativeModule_WithWrongErrorType_Fails()
        {
            using var fixture = CreateFixture();

            string testFile = fixture.WriteTest("module-negative-mismatch.js", """
/*---
description: runtime negative should only pass on the expected exception type
negative:
  phase: runtime
  type: ReferenceError
flags: [module]
---*/
throw new TypeError('wrong kind');
""");

            var runner = new Test262Runner(fixture.RootPath, timeoutMs: 5_000);
            var result = await runner.RunSingleTestAsync(testFile);

            Assert.False(result.Passed);
            Assert.Contains("ReferenceError", result.Expected, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RunSingleTestAsync_RelativeModulePath_ResolvesImportsFromCanonicalTestFile()
        {
            using var fixture = CreateFixture(
                assertJs: """
globalThis.assert = {
  sameValue: function(actual, expected) {
    if (actual !== expected) {
      throw new Error('Expected ' + expected + ' but got ' + actual);
    }
  }
};
""");

            fixture.WriteTest("dep.js", "export const value = 7;");
            fixture.WriteTest("main.js", """
/*---
description: relative module imports should resolve from the actual test file path
flags: [module]
---*/
import { value } from './dep.js';
assert.sameValue(value, 7);
""");

            var runner = new Test262Runner(fixture.RootPath, timeoutMs: 5_000);
            var result = await runner.RunSingleTestAsync(Path.Combine("test", "local", "main.js"));

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_AtomicsWaitAsync_NotEqual_ReturnsSyncString()
        {
            using var fixture = CreateFixture(assertJs: CreateBasicAssertHarness());

            string testFile = fixture.WriteTest("atomics-waitasync-not-equal.js", """
/*---
description: waitAsync returns a sync not-equal result when the observed value does not match
---*/
const i32a = new Int32Array(new SharedArrayBuffer(Int32Array.BYTES_PER_ELEMENT * 4));
Atomics.store(i32a, 0, 42);
let result = Atomics.waitAsync(i32a, 0, 0);
assert.sameValue(result.async, false);
assert.sameValue(result.value, "not-equal");
""");

            var runner = new Test262Runner(fixture.RootPath, timeoutMs: 5_000);
            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_AtomicsWaitAsync_UndefinedTimeout_WakesToOk()
        {
            using var fixture = CreateFixture(assertJs: CreateBasicAssertHarness());

            string testFile = fixture.WriteTest("atomics-waitasync-undefined-timeout.js", """
/*---
description: waitAsync with undefined timeout returns a promise and wakes via notify
flags: [async]
---*/
const i32a = new Int32Array(new SharedArrayBuffer(Int32Array.BYTES_PER_ELEMENT * 4));
let result = Atomics.waitAsync(i32a, 0, 0);
assert.sameValue(result.async, true);
assert.sameValue(typeof result.value.then, "function");
result.value.then(outcome => {
  assert.sameValue(outcome, "ok");
}).then($DONE, $DONE);
Atomics.notify(i32a, 0, 1);
""");

            var runner = new Test262Runner(fixture.RootPath, timeoutMs: 5_000);
            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_AtomicsWaitAsync_ZeroTimeout_ReturnsTimedOutString()
        {
            using var fixture = CreateFixture(assertJs: CreateBasicAssertHarness());

            string testFile = fixture.WriteTest("atomics-waitasync-zero-timeout.js", """
/*---
description: waitAsync with zero timeout returns a sync timed-out result
---*/
const i32a = new Int32Array(new SharedArrayBuffer(Int32Array.BYTES_PER_ELEMENT * 4));
let result = Atomics.waitAsync(i32a, 0, 0, 0);
assert.sameValue(result.async, false);
assert.sameValue(result.value, "timed-out");
""");

            var runner = new Test262Runner(fixture.RootPath, timeoutMs: 5_000);
            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_AtomicsWaitAsync_NonSharedBuffer_ThrowsTypeErrorBeforeTimeoutCoercion()
        {
            using var fixture = CreateFixture(assertJs: CreateBasicAssertHarness());

            string testFile = fixture.WriteTest("atomics-waitasync-non-shared-buffer.js", """
/*---
description: waitAsync rejects non-shared buffers before coercing timeout
---*/
const i32a = new Int32Array(new ArrayBuffer(Int32Array.BYTES_PER_ELEMENT * 4));
const poisoned = {
  valueOf() {
    throw new Error("timeout coercion should not run");
  }
};
assert.throws(TypeError, function() {
  Atomics.waitAsync(i32a, 0, 0, poisoned);
});
""");

            var runner = new Test262Runner(fixture.RootPath, timeoutMs: 5_000);
            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_AgentBroadcast_WakesSynchronousAtomicsWaiter()
        {
            using var fixture = CreateFixture(assertJs: CreateBasicAssertHarness());

            string testFile = fixture.WriteTest("atomics-agent-wake.js", """
/*---
description: $262.agent can start workers, broadcast shared buffers, and collect reports from Atomics.wait
---*/
const WAIT_INDEX = 0;
const RUNNING = 1;
const i32a = new Int32Array(new SharedArrayBuffer(Int32Array.BYTES_PER_ELEMENT * 2));
const TIMEOUT = $262.agent.timeouts.long;

$262.agent.start(`
  $262.agent.receiveBroadcast(function(sab) {
    const view = new Int32Array(sab);
    Atomics.add(view, 1, 1);
    $262.agent.report(Atomics.wait(view, 0, 0, ${TIMEOUT}));
    $262.agent.leaving();
  });
`);

$262.agent.safeBroadcast(i32a);
$262.agent.waitUntil(i32a, RUNNING, 1);
$262.agent.tryYield();

assert.sameValue(Atomics.notify(i32a, WAIT_INDEX, 1), 1);

let report = null;
while ((report = $262.agent.getReport()) == null) {
  $262.agent.sleep(1);
}

assert.sameValue(report, "ok");
""");

            var runner = new Test262Runner(fixture.RootPath, timeoutMs: 5_000);
            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_InferredFunctionName_DoesNotShadowOuterLexicalBinding()
        {
            using var fixture = CreateFixture();

            string testFile = fixture.WriteTest("inferred-function-name-shadowing.js", """
/*---
description: inferred function names must not create inner bindings that shadow outer lexical names
---*/
var holder = {
  getReport: function() {
    return "ok";
  }
};

let getReport = holder.getReport.bind(holder);
holder.getReport = function() {
  return getReport();
};

if (holder.getReport() !== "ok") {
  throw new Error("wrong result");
}
""");

            var runner = new Test262Runner(fixture.RootPath, timeoutMs: 5_000);
            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_AtomicsHelper_LoadsWithoutStackOverflow()
        {
            using var fixture = CreateFixture(assertJs: CreateBasicAssertHarness());

            string testFile = fixture.WriteTest("atomics-helper-smoke.js", """
/*---
description: atomics helper should load without overflowing the VM stack
includes: [atomicsHelper.js]
---*/
assert.sameValue(typeof $262.agent.safeBroadcast, "function");
assert.sameValue(typeof $262.agent.waitUntil, "function");
assert.sameValue(typeof $262.agent.getReportAsync, "function");
""");

            var runner = new Test262Runner(fixture.RootPath, timeoutMs: 5_000);
            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        [Fact]
        public async Task RunSingleTestAsync_AtomicsHelper_GetReportWrapper_DoesNotSelfRecurse()
        {
            using var fixture = CreateFixture(assertJs: CreateBasicAssertHarness());

            string testFile = fixture.WriteTest("atomics-helper-getreport.js", """
/*---
description: atomics helper getReport wrapper must resolve worker reports without self-recursing
includes: [atomicsHelper.js]
---*/
const WAIT_INDEX = 0;
const RUNNING = 1;
const i32a = new Int32Array(new SharedArrayBuffer(Int32Array.BYTES_PER_ELEMENT * 2));

$262.agent.start(`
  $262.agent.receiveBroadcast(function(sab) {
    const view = new Int32Array(sab);
    Atomics.add(view, ${RUNNING}, 1);
    $262.agent.report(Atomics.wait(view, ${WAIT_INDEX}, 0, $262.agent.timeouts.long));
    $262.agent.leaving();
  });
`);

$262.agent.safeBroadcast(i32a);
$262.agent.waitUntil(i32a, RUNNING, 1);
$262.agent.tryYield();
assert.sameValue(Atomics.notify(i32a, WAIT_INDEX, 1), 1);
assert.sameValue($262.agent.getReport(), "ok");
""");

            var runner = new Test262Runner(fixture.RootPath, timeoutMs: 5_000);
            var result = await runner.RunSingleTestAsync(testFile);

            Assert.True(result.Passed, result.Error);
        }

        private static Test262Fixture CreateFixture(string assertJs = "", string staJs = "")
        {
            return new Test262Fixture(assertJs, staJs);
        }

        private static string CreateBasicAssertHarness()
        {
            return """
function assert(condition, message) {
  if (!condition) {
    throw new Error(message || "Assertion failed");
  }
}

assert.sameValue = function(actual, expected, message) {
  if (actual !== expected) {
    throw new Error(message || ("Expected " + String(expected) + " but got " + String(actual)));
  }
};

assert.throws = function(ExpectedError, callback, message) {
  let threw = false;
  try {
    callback();
  } catch (error) {
    threw = true;
    if (!(error instanceof ExpectedError)) {
      throw new Error(message || ("Expected " + ExpectedError.name + " but got " + error));
    }
  }

  if (!threw) {
    throw new Error(message || ("Expected " + ExpectedError.name + " to be thrown"));
  }
};
""";
        }

        private sealed class Test262Fixture : IDisposable
        {
            public string RootPath { get; }
            private readonly string _testDir;

            public Test262Fixture(string assertJs, string staJs)
            {
                RootPath = Path.Combine(Path.GetTempPath(), "fen-test262-" + Guid.NewGuid().ToString("N"));
                _testDir = Path.Combine(RootPath, "test", "local");
                Directory.CreateDirectory(_testDir);
                Directory.CreateDirectory(Path.Combine(RootPath, "harness"));

                File.WriteAllText(Path.Combine(RootPath, "harness", "assert.js"), assertJs ?? string.Empty);
                File.WriteAllText(Path.Combine(RootPath, "harness", "sta.js"), staJs ?? string.Empty);
                File.WriteAllText(Path.Combine(RootPath, "harness", "doneprintHandle.js"), """
function __consolePrintHandle__(msg) {
  print(msg);
}

function $DONE(error) {
  if (error) {
    if (typeof error === 'object' && error !== null && 'name' in error) {
      __consolePrintHandle__('Test262:AsyncTestFailure:' + error.name + ': ' + error.message);
    } else {
      __consolePrintHandle__('Test262:AsyncTestFailure:Test262Error: ' + String(error));
    }
  } else {
    __consolePrintHandle__('Test262:AsyncTestComplete');
  }
}
""");
                File.WriteAllText(Path.Combine(RootPath, "harness", "atomicsHelper.js"), """
{
  let getReport = $262.agent.getReport.bind($262.agent);

  $262.agent.getReport = function() {
    var report;
    while ((report = getReport()) == null) {
      $262.agent.sleep(1);
    }
    return report;
  };

  if (this.setTimeout === undefined) {
    (function(that) {
      that.setTimeout = function(callback, delay) {
        let p = Promise.resolve();
        let start = Date.now();
        let end = start + delay;
        function check() {
          if ((end - Date.now()) > 0) {
            p.then(check);
          } else {
            callback();
          }
        }
        p.then(check);
      };
    })(this);
  }

  $262.agent.setTimeout = setTimeout;
  $262.agent.getReportAsync = function() {
    return new Promise(function(resolve) {
      (function loop() {
        let result = getReport();
        if (!result) {
          setTimeout(loop, 1);
        } else {
          resolve(result);
        }
      })();
    });
  };
}

$262.agent.timeouts = {
  yield: 25,
  small: 100,
  long: 1000,
  huge: 5000,
};

$262.agent.safeBroadcast = function(typedArray) {
  let Constructor = Object.getPrototypeOf(typedArray).constructor;
  let temp = new Constructor(new SharedArrayBuffer(Constructor.BYTES_PER_ELEMENT));
  Atomics.wait(temp, 0, Constructor === Int32Array ? 1 : BigInt(1), 0);
  $262.agent.broadcast(typedArray.buffer);
};

$262.agent.safeBroadcastAsync = async function(typedArray, index, expected) {
  $262.agent.safeBroadcast(typedArray);
  $262.agent.waitUntil(typedArray, index, expected);
  $262.agent.tryYield();
  return Atomics.load(typedArray, index);
};

$262.agent.waitUntil = function(typedArray, index, expected) {
  var agents = 0;
  while ((agents = Atomics.load(typedArray, index)) !== expected) {}
  assert.sameValue(agents, expected);
};

$262.agent.tryYield = function() {
  $262.agent.sleep($262.agent.timeouts.yield);
};

$262.agent.trySleep = function(ms) {
  $262.agent.sleep(ms);
};
""");
            }

            public string WriteTest(string fileName, string content)
            {
                var path = Path.Combine(_testDir, fileName);
                File.WriteAllText(path, content);
                return path;
            }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(RootPath))
                    {
                        Directory.Delete(RootPath, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }
    }
}
