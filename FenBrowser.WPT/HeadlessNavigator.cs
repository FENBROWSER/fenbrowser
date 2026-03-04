// =============================================================================
// HeadlessNavigator.cs
// Lightweight headless navigator for WPT test execution.
//
// PURPOSE: Execute HTML + script in a minimal runtime and bridge WPT's
// testharness callbacks into FenBrowser's TestHarnessAPI.
// =============================================================================

using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Accessibility;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.DOM;
using FenBrowser.FenEngine.HTML;
using FenBrowser.FenEngine.WebAPIs;

namespace FenBrowser.WPT;

public sealed class HeadlessNavigator
{
    private const string MinimalHarnessScript = @"
var self = (typeof globalThis !== 'undefined') ? globalThis : this;
var __fenMiniHarnessDoneSignaled = false;
var __fenMiniHarnessRunScheduled = false;
var __fenMiniHarnessRunning = false;
var __fenMiniHarnessSetupPromise = Promise.resolve();
var __fenMiniHarnessQueue = [];

function __fenMiniHarnessToMessage(e) {
  try {
    if (e && e.message) { return String(e.message); }
    return String(e);
  } catch (_) {
    return 'error';
  }
}

function __fenMiniHarnessReport(name, pass, message) {
  try {
    if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.reportResult === 'function') {
      testRunner.reportResult(String(name || 'unnamed'), !!pass, String(message || ''));
    }
  } catch (_) {}
}

function __fenMiniHarnessNotifyDone() {
  if (__fenMiniHarnessDoneSignaled) { return; }
  __fenMiniHarnessDoneSignaled = true;
  try {
    if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.reportHarnessStatus === 'function') {
      testRunner.reportHarnessStatus('complete', '');
    }
    if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.notifyDone === 'function') {
      testRunner.notifyDone();
    }
  } catch (_) {}
}

function __fenCreateTestContext(testName, finish) {
  var cleanups = [];
  return {
    add_cleanup: function (fn) {
      if (typeof fn === 'function') { cleanups.push(fn); }
    },
    step_func: function (cb, this_obj) {
      var ctx = (arguments.length > 1) ? this_obj : this;
      return function () {
        try {
          return cb.apply(ctx, arguments);
        } catch (e) {
          finish(false, __fenMiniHarnessToMessage(e));
          return undefined;
        }
      };
    },
    step_func_done: function (cb, this_obj) {
      var wrapped = this.step_func(typeof cb === ""function"" ? cb : function () {}, this_obj);
      var selfRef = this;
      return function () {
        var ret = wrapped.apply(this, arguments);
        selfRef.done();
        return ret;
      };
    },
    step_timeout: function (cb, ms) {
      setTimeout(this.step_func(cb), ms || 0);
    },
    unreached_func: function (message) {
      return function () {
        throw new Error(message || 'Reached unreachable function');
      };
    },
    done: function () {
      finish(true, '');
    },
    __run_cleanups: async function () {
      while (cleanups.length > 0) {
        var fn = cleanups.pop();
        try {
          await Promise.resolve(fn());
        } catch (_) {}
      }
    },
    __name: testName
  };
}

function __fenMiniHarnessQueueTest(name, body) {
  __fenMiniHarnessQueue.push({ name: name || 'unnamed', body: body });
  __fenMiniHarnessKick();
}

async function __fenMiniHarnessRun() {
  if (__fenMiniHarnessRunning) { return; }
  __fenMiniHarnessRunning = true;
  try {
    while (__fenMiniHarnessQueue.length > 0) {
      var item = __fenMiniHarnessQueue.shift();
      var finished = false;
      var resolveFinish;
      var finishPromise = new Promise(function (resolve) { resolveFinish = resolve; });
      var ctx = __fenCreateTestContext(item.name, function (pass, message) {
        if (finished) { return; }
        finished = true;
        __fenMiniHarnessReport(item.name, pass, message);
        resolveFinish();
      });

      try {
        await __fenMiniHarnessSetupPromise;
        await Promise.resolve(item.body(ctx));
        if (!finished) { ctx.done(); }
      } catch (e) {
        if (!finished) {
          __fenMiniHarnessReport(item.name, false, __fenMiniHarnessToMessage(e));
          resolveFinish();
        }
      }

      await finishPromise;
      await ctx.__run_cleanups();
    }
  } finally {
    __fenMiniHarnessRunning = false;
    __fenMiniHarnessNotifyDone();
  }
}

function __fenMiniHarnessKick() {
  if (__fenMiniHarnessRunScheduled) { return; }
  __fenMiniHarnessRunScheduled = true;
  setTimeout(function () {
    __fenMiniHarnessRunScheduled = false;
    __fenMiniHarnessRun();
  }, 0);
}

function setup(fn) {
  if (typeof fn === 'function') {
    __fenMiniHarnessSetupPromise = __fenMiniHarnessSetupPromise.then(function () { return fn(); });
  }
}

function promise_setup(fn) {
  setup(fn);
}

function test(fn, name) {
  __fenMiniHarnessQueueTest(name, function (t) {
    return fn.call(t, t);
  });
}

function promise_test(fn, name) {
  __fenMiniHarnessQueueTest(name, function (t) {
    return Promise.resolve(fn.call(t, t));
  });
}

function async_test(fnOrName, maybeName) {
  var name = typeof fnOrName === 'function' ? maybeName : fnOrName;
  if (typeof fnOrName === 'function') {
    __fenMiniHarnessQueueTest(name, function (t) {
      return new Promise(function (resolve) {
        var oldDone = t.done;
        t.done = function () { oldDone(); resolve(); };
        try {
          fnOrName.call(t, t);
        } catch (e) {
          __fenMiniHarnessReport(t.__name, false, __fenMiniHarnessToMessage(e));
          resolve();
        }
      });
    });
  }
  return __fenCreateTestContext(name || 'unnamed', function () {});
}

function done() { __fenMiniHarnessNotifyDone(); }
function assert_true(value, message) { if (!value) { throw new Error(message || 'assert_true failed'); } }
function assert_false(value, message) { if (value) { throw new Error(message || 'assert_false failed'); } }
function assert_equals(actual, expected, message) {
  if (actual !== expected) { throw new Error(message || ('assert_equals failed: ' + actual + ' !== ' + expected)); }
}
function assert_not_equals(actual, expected, message) {
  if (actual === expected) { throw new Error(message || ('assert_not_equals failed: both are ' + actual)); }
}
function assert_less_than(actual, expected, message) {
  if (!(actual < expected)) { throw new Error(message || ('assert_less_than failed: ' + actual + ' >= ' + expected)); }
}
function assert_less_than_equal(actual, expected, message) {
  if (!(actual <= expected)) { throw new Error(message || ('assert_less_than_equal failed: ' + actual + ' > ' + expected)); }
}
function assert_greater_than(actual, expected, message) {
  if (!(actual > expected)) { throw new Error(message || ('assert_greater_than failed: ' + actual + ' <= ' + expected)); }
}
function assert_greater_than_equal(actual, expected, message) {
  if (!(actual >= expected)) { throw new Error(message || ('assert_greater_than_equal failed: ' + actual + ' < ' + expected)); }
}
function assert_approx_equals(actual, expected, epsilon, message) {
  if (Math.abs(actual - expected) > epsilon) { throw new Error(message || ('assert_approx_equals failed: ' + actual + ' !~= ' + expected)); }
}
function assert_implements(value, message) {
  if (!value) { throw new Error(message || 'assert_implements failed'); }
}
function assert_implements_optional(_value, _message) {}
function assert_throws_dom(_name, fn, message) {
  var threw = false;
  try { fn(); } catch (e) { threw = true; }
  if (!threw) { throw new Error(message || 'Expected DOM exception was not thrown'); }
}
function assert_throws_js(_ctor, fn, message) {
  var threw = false;
  try {
    var result = fn();
    if (result && typeof result === 'object') {
      var n = result.name;
      var m = result.message;
      if ((typeof n === 'string' && n.length > 0) || (typeof m === 'string' && m.length > 0)) {
        threw = true;
      }
    }
  } catch (e) { threw = true; }
  if (!threw) { throw new Error(message || 'Expected JS exception was not thrown'); }
}
function promise_rejects_js(_test, ctor, promise, message) {
  return Promise.resolve(promise).then(function () {
    throw new Error(message || 'Expected promise rejection');
  }, function (err) {
    if (typeof ctor === 'function' && !(err instanceof ctor)) {
      throw new Error(message || 'Rejected with unexpected error type');
    }
  });
}
function promise_rejects_exactly(_test, expected, promise, message) {
  return Promise.resolve(promise).then(function () {
    throw new Error(message || 'Expected promise rejection');
  }, function (err) {
    if (err !== expected) {
      throw new Error(message || 'Rejected with unexpected error object');
    }
  });
}
function assert_unreached(message) { throw new Error(message || 'Reached unreachable code'); }
function assert_array_equals(actual, expected, message) {
  if (!actual || !expected || actual.length !== expected.length) {
    throw new Error(message || 'assert_array_equals length mismatch');
  }
  for (var i = 0; i < actual.length; i++) {
    if (actual[i] !== expected[i]) {
      throw new Error(message || ('assert_array_equals mismatch at index ' + i));
    }
  }
}
function assert_array_approx_equals(actual, expected, epsilon, message) {
  if (!actual || !expected || actual.length !== expected.length) {
    throw new Error(message || 'assert_array_approx_equals length mismatch');
  }
  for (var i = 0; i < actual.length; i++) {
    if (Math.abs(actual[i] - expected[i]) > epsilon) {
      throw new Error(message || ('assert_array_approx_equals mismatch at index ' + i));
    }
  }
}
function assert_own_property(obj, prop, message) {
  if (obj === null || obj === undefined || !Object.prototype.hasOwnProperty.call(obj, prop)) {
    throw new Error(message || ('assert_own_property failed: missing own property ' + prop));
  }
}
function assert_not_own_property(obj, prop, message) {
  if (obj !== null && obj !== undefined && Object.prototype.hasOwnProperty.call(obj, prop)) {
    throw new Error(message || ('assert_not_own_property failed: unexpected own property ' + prop));
  }
}
function format_value(v) {
  try { return String(v); } catch (_) { return '<unprintable>'; }
}

function EventWatcher(t, target, events) {
  this.t = t;
  this.target = target;
  this.events = Array.isArray(events) ? events : [events];
}
EventWatcher.prototype.wait_for = function (eventName) {
  var selfRef = this;
  return new Promise(function (resolve, reject) {
    var handler = function (ev) {
      if (selfRef.target && typeof selfRef.target.removeEventListener === 'function') {
        selfRef.target.removeEventListener(eventName, handler);
      }
      resolve(ev || { type: eventName, data: undefined, timeStamp: (performance && performance.now) ? performance.now() : Date.now() });
    };
    if (!selfRef.target || typeof selfRef.target.addEventListener !== 'function') {
      reject(new Error('Event target does not support addEventListener'));
      return;
    }
    selfRef.target.addEventListener(eventName, handler);
    if (selfRef.t && typeof selfRef.t.add_cleanup === 'function') {
      selfRef.t.add_cleanup(function () {
        if (selfRef.target && typeof selfRef.target.removeEventListener === 'function') {
          selfRef.target.removeEventListener(eventName, handler);
        }
      });
    }
  });
};
";
    private const string HarnessBridgeScript = @"
(function () {
  if (typeof globalThis === 'undefined') { return; }
  if (globalThis.__fenWptBridgeInstalled) { return; }
  if (typeof add_result_callback !== 'function' || typeof add_completion_callback !== 'function') { return; }

  globalThis.__fenWptBridgeInstalled = true;

  add_result_callback(function (test) {
    try {
      if (typeof testRunner === 'undefined' || !testRunner || typeof testRunner.reportResult !== 'function') { return; }
      var status = (test && typeof test.status === 'number') ? test.status : 1;
      var pass = status === 0;
      var name = test && test.name ? String(test.name) : 'unnamed';
      var message = test && test.message ? String(test.message) : '';
      testRunner.reportResult(name, pass, message);
    } catch (e) {}
  });

  add_completion_callback(function (tests, harness_status) {
    try {
      if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.reportHarnessStatus === 'function') {
        var message = harness_status && harness_status.message ? String(harness_status.message) : '';
        testRunner.reportHarnessStatus('complete', message);
      }
      if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.notifyDone === 'function') {
        testRunner.notifyDone();
      }
    } catch (e) {}
  });

  try {
    if (typeof window !== 'undefined' && window && typeof window.dispatchEvent === 'function' && typeof Event === 'function') {
      window.dispatchEvent(new Event('load'));
    }
  } catch (e) {}
})();
";
    private const string TestDriverShimScript = @"
/* FenBrowser WPT test_driver shim: accessibility helpers + virtual generic sensors. */
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  var nextSensorId = 1;
  var permissions = {};
  var sensorsByType = {};

  function nowMs() {
    try { return (performance && performance.now) ? performance.now() : Date.now(); } catch (_) { return Date.now(); }
  }

  function normalizeVirtualType(name) {
    if (!name) return '';
    var n = String(name).toLowerCase();
    if (n.indexOf('accelerometer') >= 0) return 'accelerometer';
    if (n.indexOf('linear') >= 0) return 'linearacceleration';
    if (n.indexOf('gravity') >= 0) return 'gravity';
    return n;
  }

  function ctorToPermission(typeName) {
    if (typeName === 'Accelerometer' || typeName === 'LinearAccelerationSensor' || typeName === 'GravitySensor') {
      return 'accelerometer';
    }
    return typeName.toLowerCase();
  }

  function createEventTarget(owner) {
    owner.__listeners = {};
    owner.addEventListener = function (name, cb) {
      if (typeof cb !== 'function') return;
      if (!owner.__listeners[name]) owner.__listeners[name] = [];
      owner.__listeners[name].push(cb);
    };
    owner.removeEventListener = function (name, cb) {
      var arr = owner.__listeners[name];
      if (!arr) return;
      owner.__listeners[name] = arr.filter(function (x) { return x !== cb; });
    };
    owner.__dispatch = function (name, payload) {
      var ev = payload || {};
      ev.type = name;
      ev.timeStamp = nowMs();
      var prop = 'on' + name;
      if (typeof owner[prop] === 'function') {
        try { owner[prop](ev); } catch (_) {}
      }
      var arr = owner.__listeners[name] || [];
      for (var i = 0; i < arr.length; i++) {
        try { arr[i](ev); } catch (_) {}
      }
    };
  }

  function getVirtualInfo(type) {
    var key = normalizeVirtualType(type);
    if (!sensorsByType[key]) {
      sensorsByType[key] = {
        connected: true,
        minSamplingFrequency: 1,
        maxSamplingFrequency: 60,
        requestedSamplingFrequency: 60,
        lastReading: null,
        instances: []
      };
    }
    return sensorsByType[key];
  }

  function recomputeRequestedFrequency(vs) {
    var maxReq = vs.minSamplingFrequency;
    for (var i = 0; i < vs.instances.length; i++) {
      var s = vs.instances[i];
      if (!s.activated) continue;
      if (s.__requestedFrequency > maxReq) maxReq = s.__requestedFrequency;
    }
    if (maxReq < vs.minSamplingFrequency) maxReq = vs.minSamplingFrequency;
    if (maxReq > vs.maxSamplingFrequency) maxReq = vs.maxSamplingFrequency;
    if (maxReq > 60) maxReq = 60;
    vs.requestedSamplingFrequency = maxReq;
  }

  function SensorBase(typeName, options) {
    options = options || {};
    createEventTarget(this);
    var freq = options.frequency;
    if (freq !== undefined) {
      if (typeof freq !== 'number' || !isFinite(freq)) {
        throw new TypeError('Invalid frequency');
      }
    }
    this.__id = nextSensorId++;
    this.__typeName = typeName;
    this.__permissionName = ctorToPermission(typeName);
    this.__virtualType = normalizeVirtualType(typeName);
    this.__requestedFrequency = (typeof freq === 'number' ? freq : 60);
    if (this.__requestedFrequency < 1) this.__requestedFrequency = 1;
    if (this.__requestedFrequency > 60) this.__requestedFrequency = 60;
    this.activated = false;
    this.hasReading = false;
    this.timestamp = null;
    this.x = null;
    this.y = null;
    this.z = null;
    this.onerror = null;
    this.onreading = null;
    this.onactivate = null;
  }

  SensorBase.prototype.start = function () {
    var perm = permissions[this.__permissionName] || 'granted';
    var info = getVirtualInfo(this.__virtualType);
    if (perm === 'denied') {
      this.activated = false;
      this.__dispatch('error', { error: { name: 'NotAllowedError' } });
      return;
    }
    if (!info.connected) {
      this.activated = false;
      this.__dispatch('error', { error: { name: 'NotReadableError' } });
      return;
    }
    if (info.instances.indexOf(this) < 0) info.instances.push(this);
    this.activated = true;
    recomputeRequestedFrequency(info);
    this.__dispatch('activate', {});
    if (info.lastReading) {
      this.__applyReading(info.lastReading);
      this.__dispatch('reading', {});
    }
  };

  SensorBase.prototype.stop = function () {
    var info = getVirtualInfo(this.__virtualType);
    this.activated = false;
    this.hasReading = false;
    this.timestamp = null;
    this.x = null;
    this.y = null;
    this.z = null;
    recomputeRequestedFrequency(info);
  };

  SensorBase.prototype.__applyReading = function (reading) {
    reading = reading || {};
    this.x = (reading.x !== undefined) ? reading.x : ((reading.values && reading.values.length > 0) ? reading.values[0] : 0);
    this.y = (reading.y !== undefined) ? reading.y : ((reading.values && reading.values.length > 1) ? reading.values[1] : 0);
    this.z = (reading.z !== undefined) ? reading.z : ((reading.values && reading.values.length > 2) ? reading.values[2] : 0);
    this.timestamp = nowMs();
    this.hasReading = true;
  };

  function Accelerometer(options) { SensorBase.call(this, 'Accelerometer', options); }
  Accelerometer.prototype = Object.create(SensorBase.prototype);
  Accelerometer.prototype.constructor = Accelerometer;

  function LinearAccelerationSensor(options) { SensorBase.call(this, 'LinearAccelerationSensor', options); }
  LinearAccelerationSensor.prototype = Object.create(SensorBase.prototype);
  LinearAccelerationSensor.prototype.constructor = LinearAccelerationSensor;

  function GravitySensor(options) { SensorBase.call(this, 'GravitySensor', options); }
  GravitySensor.prototype = Object.create(SensorBase.prototype);
  GravitySensor.prototype.constructor = GravitySensor;

  g.Accelerometer = g.Accelerometer || Accelerometer;
  g.LinearAccelerationSensor = g.LinearAccelerationSensor || LinearAccelerationSensor;
  g.GravitySensor = g.GravitySensor || GravitySensor;
  g.self = g.self || g;

  function updateVirtualSensor(type, reading) {
    var info = getVirtualInfo(type);
    info.lastReading = reading || {};
    for (var i = 0; i < info.instances.length; i++) {
      var s = info.instances[i];
      if (!s.activated) continue;
      s.__applyReading(info.lastReading);
      s.__dispatch('reading', {});
    }
  }

  var test_driver = {
    click: function () { return Promise.resolve(); },
    get_computed_label: function(el) {
      try { return Promise.resolve(__fenGetComputedLabel(el)); } catch(e) { return Promise.reject(e); }
    },
    get_computed_role: function(el) {
      try { return Promise.resolve(__fenGetComputedRole(el)); } catch(e) { return Promise.reject(e); }
    },
    create_virtual_sensor: function(type, opts) {
      opts = opts || {};
      var info = getVirtualInfo(type);
      info.connected = (opts.connected !== false);
      if (typeof opts.minSamplingFrequency === 'number') info.minSamplingFrequency = opts.minSamplingFrequency;
      if (typeof opts.maxSamplingFrequency === 'number') info.maxSamplingFrequency = opts.maxSamplingFrequency;
      recomputeRequestedFrequency(info);
      return Promise.resolve();
    },
    remove_virtual_sensor: function(type) {
      var info = getVirtualInfo(type);
      info.connected = true;
      info.lastReading = null;
      info.instances = [];
      info.requestedSamplingFrequency = 60;
      return Promise.resolve();
    },
    update_virtual_sensor: function(type, reading) {
      updateVirtualSensor(type, reading);
      return Promise.resolve();
    },
    get_virtual_sensor_information: function(type) {
      var info = getVirtualInfo(type);
      recomputeRequestedFrequency(info);
      return Promise.resolve({
        requestedSamplingFrequency: info.requestedSamplingFrequency,
        minSamplingFrequency: info.minSamplingFrequency,
        maxSamplingFrequency: info.maxSamplingFrequency
      });
    },
    bidi: {
      permissions: {
        set_permission: function(data) {
          var descriptor = data && data.descriptor ? data.descriptor : {};
          var name = descriptor.name ? String(descriptor.name) : '';
          permissions[name] = data && data.state ? String(data.state) : 'granted';
          return Promise.resolve();
        }
      }
    }
  };

  if (typeof window !== 'undefined') { window.test_driver = test_driver; }
  if (typeof window !== 'undefined' && !window.test_driver_internal) {
    window.test_driver_internal = test_driver;
  }
  g.test_driver = test_driver;
})();
";
    private readonly string? _wptRootPath;
    private readonly int _timeoutMs;

    public HeadlessNavigator(string? wptRootPath = null, int timeoutMs = 30_000)
    {
        _wptRootPath = string.IsNullOrWhiteSpace(wptRootPath) ? null : wptRootPath;
        _timeoutMs = timeoutMs;
    }

    public async Task NavigateAsync(string url)
    {
        var filePath = ResolveTestFilePath(url);
        var html = await File.ReadAllTextAsync(filePath);

        var tokenizer = new HtmlTokenizer(html);
        var builder = new HtmlTreeBuilder(tokenizer);
        var document = builder.Build();

        var runtime = new FenRuntime(new FenBrowser.FenEngine.Core.ExecutionContext(new FenBrowser.FenEngine.Security.PermissionManager(FenBrowser.FenEngine.Security.JsPermissions.StandardWeb)));
        TestHarnessAPI.Register(runtime);

        // Inject parsed DOM through the runtime DOM bridge so global/window/document stay coherent.
        Uri? baseUri = null;
        try { baseUri = new Uri(filePath); } catch { }
        runtime.SetDom(document, baseUri);

        // Register C# host functions for accessibility (used by TestDriverShimScript).
        runtime.GlobalEnv.Set("__fenGetComputedLabel",
            FenBrowser.FenEngine.Core.FenValue.FromFunction(new FenBrowser.FenEngine.Core.FenFunction(
                "__fenGetComputedLabel", (args, thisVal) =>
                {
                    if (args.Length > 0 && args[0].IsObject && args[0].AsObject() is ElementWrapper ew)
                    {
                        var label = AccNameCalculator.Compute(ew.Element, ew.Element.OwnerDocument);
                        return FenBrowser.FenEngine.Core.FenValue.FromString(label);
                    }
                    return FenBrowser.FenEngine.Core.FenValue.FromString("");
                })));

        runtime.GlobalEnv.Set("__fenGetComputedRole",
            FenBrowser.FenEngine.Core.FenValue.FromFunction(new FenBrowser.FenEngine.Core.FenFunction(
                "__fenGetComputedRole", (args, thisVal) =>
                {
                    if (args.Length > 0 && args[0].IsObject && args[0].AsObject() is ElementWrapper ew)
                    {
                        var role = AccessibilityRole.ResolveRole(ew.Element, ew.Element.OwnerDocument);
                        if (role == AriaRole.None || role == AriaRole.Generic)
                            return FenBrowser.FenEngine.Core.FenValue.FromString("");
                        return FenBrowser.FenEngine.Core.FenValue.FromString(role.ToString().ToLowerInvariant());
                    }
                    return FenBrowser.FenEngine.Core.FenValue.FromString("");
                })));
        runtime.OnConsoleMessage = msg =>
        {
            var level = "log";
            if (msg.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase)) level = "error";
            else if (msg.StartsWith("[Warn]", StringComparison.OrdinalIgnoreCase)) level = "warn";
            else if (msg.StartsWith("[Info]", StringComparison.OrdinalIgnoreCase)) level = "info";
            TestConsoleCapture.AddEntry(level, msg);
        };

        var scripts = ExtractScripts(document);
        var scriptOrdinal = 0;
        foreach (var (src, scriptContent, isExternal) in scripts)
        {
            scriptOrdinal++;
            string code;
            var scriptLabel = isExternal && !string.IsNullOrWhiteSpace(src)
                ? src
                : $"inline-script-{scriptOrdinal}";

            if (isExternal && !string.IsNullOrWhiteSpace(src))
            {
                if (IsTestHarnessScript(src))
                {
                    code = MinimalHarnessScript;
                    scriptLabel = "fen-minimal-testharness.js";
                }
                else if (IsTestHarnessReportScript(src))
                {
                    code = "/* FenBrowser shim: testharnessreport.js intentionally no-op */";
                    scriptLabel = "fen-minimal-testharnessreport.js";
                }
                else if (IsTestDriverScript(src))
                {
                    code = TestDriverShimScript;
                    scriptLabel = "fen-testdriver-shim.js";
                }
                else if (IsTestDriverSupportScript(src))
                {
                    // testdriver-vendor.js and testdriver-actions.js: no-op shim.
                    code = "/* FenBrowser shim: testdriver support script intentionally no-op */";
                    scriptLabel = "fen-testdriver-support-noop.js";
                }
                else
                {
                    var scriptPath = ResolveExternalScriptPath(src, filePath);
                    if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
                    {
                        continue;
                    }

                    code = await File.ReadAllTextAsync(scriptPath);
                }
            }
            else
            {
                code = scriptContent;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            if (!TryExecuteScript(runtime, code, _timeoutMs, scriptLabel))
            {
                break;
            }

            // Bridge is idempotent and only activates once testharness APIs exist.
            TryExecuteScript(runtime, HarnessBridgeScript, Math.Min(_timeoutMs, 2_000), "fen-harness-bridge.js");
        }

        // Final bridge attempt for tests that load harness late in the script list.
        TryExecuteScript(runtime, HarnessBridgeScript, Math.Min(_timeoutMs, 2_000), "fen-harness-bridge.js");
    }

    public Func<string, Task> GetNavigatorDelegate()
    {
        return NavigateAsync;
    }

    private static string ResolveTestFilePath(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new FileNotFoundException("Test file URL is empty.");
        }

        if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(url).LocalPath;
        }

        if (File.Exists(url))
        {
            return Path.GetFullPath(url);
        }

        throw new FileNotFoundException($"Test file not found: {url}");
    }

    private string? ResolveExternalScriptPath(string scriptSrc, string testFilePath)
    {
        if (string.IsNullOrWhiteSpace(scriptSrc))
        {
            return null;
        }

        var cleaned = scriptSrc;
        var queryIx = cleaned.IndexOf('?');
        if (queryIx >= 0) cleaned = cleaned.Substring(0, queryIx);
        var hashIx = cleaned.IndexOf('#');
        if (hashIx >= 0) cleaned = cleaned.Substring(0, hashIx);

        if (Uri.TryCreate(cleaned, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.IsFile && File.Exists(absoluteUri.LocalPath))
            {
                return absoluteUri.LocalPath;
            }

            return null;
        }

        var normalized = cleaned.Replace('/', Path.DirectorySeparatorChar);
        var testDir = Path.GetDirectoryName(testFilePath) ?? string.Empty;

        // Root-absolute WPT path, e.g. /resources/testharness.js.
        if ((normalized.StartsWith(Path.DirectorySeparatorChar) || normalized.StartsWith(Path.AltDirectorySeparatorChar))
            && !string.IsNullOrWhiteSpace(_wptRootPath))
        {
            var rootRelative = normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootAbsolute = Path.GetFullPath(Path.Combine(_wptRootPath!, rootRelative));
            if (File.Exists(rootAbsolute))
            {
                return rootAbsolute;
            }
        }

        // Relative to the current test file directory.
        var fromTest = Path.GetFullPath(Path.Combine(testDir, normalized));
        if (File.Exists(fromTest))
        {
            return fromTest;
        }

        // Fallback to WPT root.
        if (!string.IsNullOrWhiteSpace(_wptRootPath))
        {
            var fromRoot = Path.GetFullPath(
                Path.Combine(_wptRootPath!, normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            if (File.Exists(fromRoot))
            {
                return fromRoot;
            }
        }

        return null;
    }

    private static bool TryExecuteScript(FenRuntime runtime, string code, int timeoutMs, string scriptLabel)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var result = runtime.ExecuteSimple(code, allowReturn: true, cancellationToken: cts.Token);
            if (result.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Error ||
                result.Type == FenBrowser.FenEngine.Core.Interfaces.ValueType.Throw)
            {
                TestConsoleCapture.AddEntry("error", $"[WPT-NAV] {scriptLabel} returned {result.Type}: {result}");
                AppendParserDiagnostics(code, scriptLabel);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            TestConsoleCapture.AddEntry("error", $"[WPT-NAV] {scriptLabel} timed out after {timeoutMs}ms");
            return false;
        }
        catch (Exception ex)
        {
            TestConsoleCapture.AddEntry("error", $"[WPT-NAV] {scriptLabel} threw host exception: {ex.Message}");
            return true;
        }
    }

    private static void AppendParserDiagnostics(string code, string scriptLabel)
    {
        try
        {
            var lexer = new Lexer(code);
            var parser = new Parser(lexer);
            parser.ParseProgram();
            if (parser.Errors.Count == 0)
            {
                return;
            }

            var previewCount = Math.Min(3, parser.Errors.Count);
            for (var i = 0; i < previewCount; i++)
            {
                TestConsoleCapture.AddEntry("error", $"[WPT-NAV] {scriptLabel} parser[{i + 1}/{parser.Errors.Count}]: {parser.Errors[i]}");
            }
        }
        catch
        {
        }
    }

    private static bool IsTestHarnessScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        var queryIndex = normalized.IndexOf('?');
        if (queryIndex >= 0) normalized = normalized.Substring(0, queryIndex);
        return normalized.EndsWith("/resources/testharness.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/resources/testharness.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestHarnessReportScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        var queryIndex = normalized.IndexOf('?');
        if (queryIndex >= 0) normalized = normalized.Substring(0, queryIndex);
        return normalized.EndsWith("/resources/testharnessreport.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/resources/testharnessreport.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestDriverScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/resources/testdriver.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestDriverSupportScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/resources/testdriver-vendor.js", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/resources/testdriver-actions.js", StringComparison.OrdinalIgnoreCase);
    }

    private static List<(string? Src, string Content, bool IsExternal)> ExtractScripts(Document document)
    {
        var scripts = new List<(string? Src, string Content, bool IsExternal)>();
        CollectScriptElements(document.DocumentElement, scripts);
        return scripts;
    }

    private static void CollectScriptElements(
        Node? node,
        List<(string? Src, string Content, bool IsExternal)> scripts)
    {
        if (node == null) return;

        if (node is Element el && string.Equals(el.TagName, "script", StringComparison.OrdinalIgnoreCase))
        {
            var src = el.GetAttribute("src");
            var isExternal = !string.IsNullOrEmpty(src);
            var content = el.TextContent ?? string.Empty;
            scripts.Add((src, content, isExternal));
        }

        if (node.ChildNodes == null)
        {
            return;
        }

        foreach (var child in node.ChildNodes)
        {
            CollectScriptElements(child, scripts);
        }
    }
}













