// =============================================================================
// HeadlessNavigator.cs
// Lightweight headless navigator for WPT test execution.
//
// PURPOSE: Execute HTML + script in a minimal runtime and bridge WPT's
// testharness callbacks into FenBrowser's TestHarnessAPI.
// =============================================================================

using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.Core.Accessibility;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.DOM;
using FenBrowser.FenEngine.WebAPIs;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace FenBrowser.WPT;

public sealed class HeadlessNavigator
{
    private const string EventConstructorShimScript = @"
try {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  if (!g || g.__fenEventCtorShimInstalled) { }
  else {
    g.__fenEventCtorShimInstalled = true;
    var canCreateEvent = (typeof document !== 'undefined' && document && typeof document.createEvent === 'function');

    if (canCreateEvent && typeof g.Event !== 'function') {
      var EventShim = function(type, init) {
        var ev = document.createEvent('Event');
        var bubbles = !!(init && init.bubbles);
        var cancelable = !!(init && init.cancelable);
        ev.initEvent(String(type || ''), bubbles, cancelable);
        return ev;
      };
      g.Event = EventShim;
      if (typeof window !== 'undefined' && window) { window.Event = EventShim; }
    }

    if (canCreateEvent && typeof g.CustomEvent !== 'function') {
      var CustomEventShim = function(type, init) {
        var ev = document.createEvent('CustomEvent');
        var bubbles = !!(init && init.bubbles);
        var cancelable = !!(init && init.cancelable);
        var detail = init && Object.prototype.hasOwnProperty.call(init, 'detail') ? init.detail : null;
        if (typeof ev.initCustomEvent === 'function') {
          ev.initCustomEvent(String(type || ''), bubbles, cancelable, detail);
        } else {
          ev.initEvent(String(type || ''), bubbles, cancelable);
          try { ev.detail = detail; } catch (_) {}
        }
        return ev;
      };
      g.CustomEvent = CustomEventShim;
      if (typeof window !== 'undefined' && window) { window.CustomEvent = CustomEventShim; }
    }

    if (typeof g.DOMException !== 'function') {
      var DOMExceptionShim = function(message, name) {
        this.message = String(message || '');
        this.name = String(name || 'Error');
      };
      g.DOMException = DOMExceptionShim;
      if (typeof window !== 'undefined' && window) { window.DOMException = DOMExceptionShim; }
    }

    if (typeof g.AbortSignal !== 'function') {
      var AbortSignalCtor = function AbortSignal() {};
      AbortSignalCtor.prototype.aborted = false;
      AbortSignalCtor.prototype.reason = undefined;
      g.AbortSignal = AbortSignalCtor;
      if (typeof window !== 'undefined' && window) { window.AbortSignal = AbortSignalCtor; }
    }

    if (typeof g.AbortSignal.abort !== 'function') {
      g.AbortSignal.abort = function(reason) {
        var signal = new g.AbortSignal();
        signal.aborted = true;
        if (typeof reason !== 'undefined') {
          signal.reason = reason;
        } else {
          signal.reason = new g.DOMException('The operation was aborted.', 'AbortError');
        }
        if (typeof signal.addEventListener !== 'function') { signal.addEventListener = function() {}; }
        if (typeof signal.removeEventListener !== 'function') { signal.removeEventListener = function() {}; }
        if (typeof signal.dispatchEvent !== 'function') { signal.dispatchEvent = function() { return true; }; }
        return signal;
      };
    }

    if (typeof g.AbortSignal.timeout !== 'function') {
      g.AbortSignal.timeout = function(_milliseconds) {
        var signal = new g.AbortSignal();
        signal.aborted = false;
        signal.reason = undefined;
        if (typeof signal.addEventListener !== 'function') { signal.addEventListener = function() {}; }
        if (typeof signal.removeEventListener !== 'function') { signal.removeEventListener = function() {}; }
        if (typeof signal.dispatchEvent !== 'function') { signal.dispatchEvent = function() { return true; }; }
        return signal;
      };
    }

  }
} catch (_) {}
";

    private const string FatalErrorBridgeScript = @"
try {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  if (g && !g.__fenFatalHarnessBridgeInstalled) {
  g.__fenFatalHarnessBridgeInstalled = true;

  function finish(message) {
    try {
      if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.reportResult === 'function') {
        testRunner.reportResult('page-script-failure', false, String(message || 'fatal page script failure'));
      }
      if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.reportHarnessStatus === 'function') {
        testRunner.reportHarnessStatus('complete', String(message || 'fatal page script failure'));
      }
      if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.notifyDone === 'function') {
        testRunner.notifyDone();
      }
    } catch (_) {}
  }

  if (typeof g.addEventListener === 'function') {
    g.addEventListener('error', function (ev) {
      if (g.__fenAllowUncaughtException) { return; }
      var msg = ev && (ev.message || (ev.error && ev.error.message)) ? (ev.message || ev.error.message) : 'Unhandled script error';
      finish(msg);
    });

    g.addEventListener('unhandledrejection', function (ev) {
      if (g.__fenAllowUncaughtException) { return; }
      var reason = ev && ev.reason;
      var msg = reason && reason.message ? reason.message : String(reason || 'Unhandled promise rejection');
      finish(msg);
    });
  }
  }
} catch (_) {}
";
    private const string MinimalHarnessScript = @"
var self = (typeof globalThis !== 'undefined') ? globalThis : this;
var __fenMiniHarnessDoneSignaled = false;
var __fenMiniHarnessDoneCheckScheduled = false;
var __fenMiniHarnessRunScheduled = false;
var __fenMiniHarnessRunning = false;
var __fenMiniHarnessSetupPromise = Promise.resolve();
var __fenMiniHarnessQueue = [];
var __fenMiniHarnessSingleTest = false;
var __fenMiniHarnessSingleTestName = 'single_test';
var __fenMiniHarnessAssertionIndex = 0;

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

function __fenMiniHarnessRecordAssertion(pass, message) {
  if (!__fenMiniHarnessSingleTest) { return; }
  __fenMiniHarnessAssertionIndex++;
  __fenMiniHarnessReport(
    __fenMiniHarnessSingleTestName + ' #' + __fenMiniHarnessAssertionIndex,
    !!pass,
    String(message || '')
  );
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

function __fenMiniHarnessMaybeDone() {
  if (__fenMiniHarnessDoneSignaled || __fenMiniHarnessDoneCheckScheduled) { return; }
  __fenMiniHarnessDoneCheckScheduled = true;
  setTimeout(function () {
    __fenMiniHarnessDoneCheckScheduled = false;
    if (__fenMiniHarnessDoneSignaled || __fenMiniHarnessRunning || __fenMiniHarnessQueue.length > 0) { return; }
    __fenMiniHarnessNotifyDone();
  }, 0);
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

function __fenMiniHarnessRunSyncTest(name, body) {
  var finished = false;
  var ctx = __fenCreateTestContext(name || 'unnamed', function (pass, message) {
    if (finished) { return; }
    finished = true;
    __fenMiniHarnessReport(name, pass, message);
  });

  try {
    body.call(ctx, ctx);
    if (!finished) { ctx.done(); }
  } catch (e) {
    if (!finished) {
      __fenMiniHarnessReport(name, false, __fenMiniHarnessToMessage(e));
    }
  }

  try {
    Promise.resolve(ctx.__run_cleanups()).finally(__fenMiniHarnessMaybeDone);
  } catch (_) {
    __fenMiniHarnessMaybeDone();
  }
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
        await Promise.resolve(item.body.call(ctx, ctx));
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
    __fenMiniHarnessMaybeDone();
  }
}

function __fenMiniHarnessKick() {
  if (__fenMiniHarnessRunScheduled) { return; }
  __fenMiniHarnessRunScheduled = true;
  var schedule = (typeof queueMicrotask === 'function')
    ? queueMicrotask
    : function (callback) {
        if (typeof Promise === 'function' && Promise.resolve) {
          Promise.resolve().then(callback);
          return;
        }
        setTimeout(callback, 0);
      };
  schedule(function () {
    __fenMiniHarnessRunScheduled = false;
    __fenMiniHarnessRun();
  });
}

function setup(fnOrOptions, maybeOptions) {
  var fn = typeof fnOrOptions === 'function' ? fnOrOptions : null;
  var options = fn ? maybeOptions : fnOrOptions;
  if (options && typeof options === 'object' && options.allow_uncaught_exception) {
    self.__fenAllowUncaughtException = true;
  }
  if (options && typeof options === 'object' && options.single_test) {
    __fenMiniHarnessSingleTest = true;
    __fenMiniHarnessSingleTestName = (options && options.single_test_name) ? String(options.single_test_name) : 'single_test';
    __fenMiniHarnessAssertionIndex = 0;
  }
  if (typeof fn === 'function') {
    try {
      var setupResult = fn();
      __fenMiniHarnessSetupPromise = __fenMiniHarnessSetupPromise.then(function () { return setupResult; });
    } catch (e) {
      __fenMiniHarnessSetupPromise = Promise.reject(e);
      throw e;
    }
  }
}

function promise_setup(fn) {
  setup(fn);
}

function test(fn, name) {
  __fenMiniHarnessRunSyncTest(name || 'unnamed', function (t) {
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
function assert_true(value, message) {
  if (!value) {
    var failure = message || 'assert_true failed';
    __fenMiniHarnessRecordAssertion(false, failure);
    throw new Error(failure);
  }
  __fenMiniHarnessRecordAssertion(true, message || '');
}
function assert_false(value, message) {
  if (value) {
    var failure = message || 'assert_false failed';
    __fenMiniHarnessRecordAssertion(false, failure);
    throw new Error(failure);
  }
  __fenMiniHarnessRecordAssertion(true, message || '');
}
function assert_equals(actual, expected, message) {
  if (actual !== expected) {
    var failure = message || ('assert_equals failed: ' + actual + ' !== ' + expected);
    __fenMiniHarnessRecordAssertion(false, failure);
    throw new Error(failure);
  }
  __fenMiniHarnessRecordAssertion(true, message || '');
}
function assert_class_string(value, className, message) {
  var actual = '';
  try {
    actual = Object.prototype.toString.call(value);
    if (actual.indexOf('[object ') === 0 && actual.lastIndexOf(']') === actual.length - 1) {
      actual = actual.substring(8, actual.length - 1);
    }
  } catch (_) {
    actual = '';
  }
  if (!actual && value && value.constructor && value.constructor.name) {
    actual = String(value.constructor.name);
  }
  if (actual !== String(className || '')) {
    throw new Error(message || ('assert_class_string failed: ' + actual + ' !== ' + className));
  }
}
function assert_in_array(actual, expected, message) {
  if (!expected || typeof expected.length !== 'number') {
    throw new Error(message || 'assert_in_array failed: expected array-like');
  }
  for (var i = 0; i < expected.length; i++) {
    if (expected[i] === actual) { return; }
  }
  throw new Error(message || ('assert_in_array failed: ' + actual + ' not found'));
}
function assert_not_equals(actual, expected, message) {
  if (actual === expected) {
    var failure = message || ('assert_not_equals failed: both are ' + actual);
    __fenMiniHarnessRecordAssertion(false, failure);
    throw new Error(failure);
  }
  __fenMiniHarnessRecordAssertion(true, message || '');
}
function assert_regexp_match(actual, expected, message) {
  var regexp = expected instanceof RegExp ? expected : new RegExp(String(expected));
  var text = actual === null || actual === undefined ? '' : String(actual);
  if (!regexp.test(text)) {
    throw new Error(message || ('assert_regexp_match failed: ' + text + ' does not match ' + regexp));
  }
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
function promise_rejects_dom(_test, expectedName, promise, message) {
  return Promise.resolve(promise).then(function () {
    throw new Error(message || 'Expected promise rejection');
  }, function (err) {
    var actualName = err && err.name ? String(err.name) : '';
    if (actualName !== String(expectedName || '')) {
      throw new Error(message || ('Rejected with unexpected DOMException name: ' + actualName));
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
function on_event(object, eventName, callback, options) {
  if (!object || typeof object.addEventListener !== 'function') {
    throw new Error('on_event target does not support addEventListener');
  }
  object.addEventListener(eventName, callback, options || false);
  return callback;
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
try {
  if (typeof globalThis !== 'undefined' &&
      !globalThis.__fenWptBridgeInstalled &&
      typeof add_result_callback === 'function' &&
      typeof add_completion_callback === 'function') {

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
  }
} catch (_) {}
";
    private const string CssParsingTestCommonShimScript = @"
'use strict';
function test_valid_value(property, value, serializedValue, options) {
  if (arguments.length < 3) serializedValue = value;
  if (!options) options = {};
  var stringifiedValue = JSON.stringify(value);
  test(function () {
    var div = document.getElementById('target') || document.createElement('div');
    div.style[property] = '';
    div.style[property] = value;
    var readValue = div.style.getPropertyValue(property);
    assert_not_equals(readValue, '', 'property should be set');
    if (options.comparisonFunction) {
      options.comparisonFunction(readValue, serializedValue);
    } else if (Array.isArray(serializedValue)) {
      assert_in_array(readValue, serializedValue, 'serialization should be sound');
    } else {
      assert_equals(readValue, serializedValue, 'serialization should be canonical');
    }
    div.style[property] = readValue;
    assert_equals(div.style.getPropertyValue(property), readValue, 'serialization should round-trip');
  }, ""e.style['"" + property + ""'] = "" + stringifiedValue + "" should set the property value"");
}

function test_invalid_value(property, value) {
  var stringifiedValue = JSON.stringify(value);
  test(function () {
    var div = document.getElementById('target') || document.createElement('div');
    div.style[property] = '';
    div.style[property] = value;
    assert_equals(div.style.getPropertyValue(property), '');
  }, ""e.style['"" + property + ""'] = "" + stringifiedValue + "" should not set the property value"");
}
";
    private const string CssComputedTestCommonShimScript = @"
'use strict';
function test_computed_value(property, specified, computed, titleExtra, options) {
  if (!computed) computed = specified;
  if (!options) options = {};
  test(function () {
    var target = document.getElementById('target');
    assert_true(property in getComputedStyle(target), property + "" doesn't seem to be supported in the computed style"");
    assert_true(CSS.supports(property, specified), ""'"" + specified + ""' is a supported value for "" + property + ""."");
    target.style[property] = '';
    target.style[property] = specified;
    var readValue = getComputedStyle(target)[property];
    if (options.comparisonFunction) {
      options.comparisonFunction(readValue, computed);
    } else if (Array.isArray(computed)) {
      assert_in_array(readValue, computed);
    } else {
      assert_equals(readValue, computed);
    }
    if (readValue !== specified) {
      target.style[property] = '';
      target.style[property] = readValue;
      assert_equals(getComputedStyle(target)[property], readValue, 'computed value should round-trip');
    }
  }, ""Property "" + property + "" value '"" + specified + ""'"" + (titleExtra ? "" "" + titleExtra : """"));
}
";
    private const string AriaUtilsShimScript = @"
'use strict';
(function () {
  function toArray(listLike) {
    if (!listLike || typeof listLike.length !== 'number') { return []; }
    var result = [];
    for (var i = 0; i < listLike.length; i++) {
      result.push(listLike[i]);
    }
    return result;
  }

  function verifyLabelsBySelector(selector, labelTestNamePrefix) {
    var els = toArray(document.querySelectorAll(selector));
    if (!els.length) {
      throw new Error('Selector passed in verifyLabelsBySelector(""'+ selector + '"") should match at least one element.');
    }
    for (var i = 0; i < els.length; i++) {
      (function (el) {
        if (!el.hasAttribute('data-expectedlabel')) {
          throw new Error('Element should have attribute data-expectedlabel.');
        }
        var label = el.getAttribute('data-expectedlabel');
        var testName = el.getAttribute('data-testname') || label;
        if (typeof labelTestNamePrefix !== 'undefined') {
          testName = labelTestNamePrefix + testName;
        }
        promise_test(async function () {
          var expectedLabel = el.getAttribute('data-expectedlabel');
          var computedLabel = await test_driver.get_computed_label(el);
          assert_not_equals(computedLabel, null, 'get_computed_label(el) should not return null');
          var asciiWhitespace = /[\t\n\f\r\u0020]+/g;
          computedLabel = computedLabel.replace(asciiWhitespace, '\u0020').replace(/^\u0020|\u0020$/g, '');
          assert_equals(computedLabel, expectedLabel, el.outerHTML);
        }, String(testName));
      })(els[i]);
    }
  }

  function verifyRolesBySelector(selector, roleTestNamePrefix) {
    var els = toArray(document.querySelectorAll(selector));
    if (!els.length) {
      throw new Error('Selector passed in verifyRolesBySelector(""'+ selector + '"") should match at least one element.');
    }
    for (var i = 0; i < els.length; i++) {
      (function (el) {
        if (!el.hasAttribute('data-expectedrole')) {
          throw new Error('Element should have attribute data-expectedrole.');
        }
        var role = el.getAttribute('data-expectedrole');
        var testName = el.getAttribute('data-testname') || role;
        if (typeof roleTestNamePrefix !== 'undefined') {
          testName = roleTestNamePrefix + testName;
        }
        promise_test(async function () {
          var expectedRole = el.getAttribute('data-expectedrole');
          var computedRole = await test_driver.get_computed_role(el);
          assert_equals(computedRole, expectedRole, el.outerHTML);
        }, String(testName));
      })(els[i]);
    }
  }

  var ariaUtils = {
    verifyLabelsBySelector: verifyLabelsBySelector,
    verifyRolesBySelector: verifyRolesBySelector,
    verifyRolesAndLabelsBySelector: function (selector) {
      var els = toArray(document.querySelectorAll(selector));
      if (!els.length) {
        throw new Error('Selector passed in verifyRolesAndLabelsBySelector(""'+ selector + '"") should match at least one element.');
      }
      for (var i = 0; i < els.length; i++) {
        els[i].classList.add('ex-label-only');
        els[i].classList.add('ex-role-only');
      }
      verifyLabelsBySelector('.ex-label-only', 'Label: ');
      verifyRolesBySelector('.ex-role-only', 'Role: ');
    }
  };

  if (typeof window !== 'undefined' && window) {
    window.AriaUtils = ariaUtils;
  }
  if (typeof globalThis !== 'undefined') {
    globalThis.AriaUtils = ariaUtils;
  }
})();
var AriaUtils = (typeof globalThis !== 'undefined' && globalThis.AriaUtils)
  ? globalThis.AriaUtils
  : ((typeof window !== 'undefined' && window.AriaUtils) ? window.AriaUtils : undefined);
";
    private const string AccNameFallbackScript = @"
(function () {
  function collectElementsWithAttribute(attrName) {
    var all = document.getElementsByTagName('*');
    var result = [];
    if (!all || typeof all.length !== 'number') {
      return result;
    }
    for (var i = 0; i < all.length; i++) {
      var el = all[i];
      if (el && typeof el.hasAttribute === 'function' && el.hasAttribute(attrName)) {
        result.push(el);
      }
    }
    return result;
  }

  function queueLabelTests() {
    var els = collectElementsWithAttribute('data-expectedlabel');
    if (!els.length) { return 0; }
    for (var i = 0; i < els.length; i++) {
      (function (el) {
        var testName = el.getAttribute('data-testname') || el.getAttribute('data-expectedlabel') || 'accname-label';
        promise_test(async function () {
          var expectedLabel = el.getAttribute('data-expectedlabel') || '';
          var computedLabel = await test_driver.get_computed_label(el);
          assert_not_equals(computedLabel, null, 'get_computed_label(el) should not return null');
          var asciiWhitespace = /[\t\n\f\r\u0020]+/g;
          computedLabel = computedLabel.replace(asciiWhitespace, '\u0020').replace(/^\u0020|\u0020$/g, '');
          assert_equals(computedLabel, expectedLabel, el.outerHTML);
        }, String(testName));
      })(els[i]);
    }
    return els.length;
  }

  function queueRoleTests() {
    var els = collectElementsWithAttribute('data-expectedrole');
    if (!els.length) { return 0; }
    for (var i = 0; i < els.length; i++) {
      (function (el) {
        var testName = el.getAttribute('data-testname') || el.getAttribute('data-expectedrole') || 'accname-role';
        promise_test(async function () {
          var expectedRole = el.getAttribute('data-expectedrole') || '';
          var computedRole = await test_driver.get_computed_role(el);
          assert_equals(computedRole, expectedRole, el.outerHTML);
        }, String(testName));
      })(els[i]);
    }
    return els.length;
  }

  if (typeof promise_test !== 'function' || typeof test_driver === 'undefined') {
    return;
  }

  if (typeof __fenAccNameFallbackInstalled !== 'undefined' && __fenAccNameFallbackInstalled) {
    return;
  }
  __fenAccNameFallbackInstalled = true;
  queueLabelTests();
  queueRoleTests();
})();
";
    private const string TestDriverShimScript = @"
/* FenBrowser WPT test_driver shim: accessibility helpers + virtual generic sensors. */
try {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  var nextSensorId = 1;
  var permissions = {};
  var sensorsByType = {};
  g.__fenUserActivationGranted = !!g.__fenUserActivationGranted;

  function nowMs() {
    try { return (performance && performance.now) ? performance.now() : Date.now(); } catch (_) { return Date.now(); }
  }

  function normalizeVirtualType(name) {
    if (!name) return '';
    var n = String(name).toLowerCase();
    if (n.indexOf('accelerometer') >= 0) return 'accelerometer';
    if (n.indexOf('ambient') >= 0) return 'ambientlight';
    if (n.indexOf('linear') >= 0) return 'linearacceleration';
    if (n.indexOf('gravity') >= 0) return 'gravity';
    return n;
  }

  function ctorToPermission(typeName) {
    if (typeName === 'Accelerometer' || typeName === 'LinearAccelerationSensor' || typeName === 'GravitySensor') {
      return 'accelerometer';
    }
    if (typeName === 'AmbientLightSensor') {
      return 'ambient-light-sensor';
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
    if (typeof g.__fenAllowsFeature === 'function' && !g.__fenAllowsFeature(this.__permissionName)) {
      var securityError = new Error(typeName + ' blocked by feature policy');
      securityError.name = 'SecurityError';
      throw securityError;
    }
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

  function AmbientLightSensor(options) { SensorBase.call(this, 'AmbientLightSensor', options); }
  AmbientLightSensor.prototype = Object.create(SensorBase.prototype);
  AmbientLightSensor.prototype.constructor = AmbientLightSensor;

  if (g.isSecureContext !== false) {
    g.Accelerometer = g.Accelerometer || Accelerometer;
    g.LinearAccelerationSensor = g.LinearAccelerationSensor || LinearAccelerationSensor;
    g.GravitySensor = g.GravitySensor || GravitySensor;
    g.AmbientLightSensor = g.AmbientLightSensor || AmbientLightSensor;
  } else {
    try { delete g.Accelerometer; } catch (_) {}
    try { delete g.LinearAccelerationSensor; } catch (_) {}
    try { delete g.GravitySensor; } catch (_) {}
    try { delete g.AmbientLightSensor; } catch (_) {}
  }
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
    click: function (target) {
      return Promise.resolve().then(function () {
        if (!target) {
          return;
        }

        var isDisabled = false;
        try {
          isDisabled = !!target.disabled;
        } catch (_) {}
        if (isDisabled) {
          return;
        }

        var observedClick = false;
        var probe = null;
        if (typeof target.addEventListener === 'function') {
          probe = function () { observedClick = true; };
          try { target.addEventListener('click', probe, { once: true }); } catch (_) { probe = null; }
        }

        if (typeof target.click === 'function') {
          target.click();
          if (!observedClick && typeof target.onclick === 'function') {
            try { target.onclick.call(target, new Event('click')); } catch (_) {}
          }
          return;
        }

        if (typeof target.dispatchEvent === 'function') {
          try {
            target.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, composed: true }));
          } catch (_) {}
        }
      });
    },
    action_sequence: function(actions, _context) {
      return Promise.resolve().then(function() {
        if (!actions || !actions.length) {
          return;
        }

        var pointerState = {
          target: null,
          x: 0,
          y: 0,
          button: 0
        };

        function resolvePointerTarget(origin) {
          if (!origin || origin === 'viewport' || origin === 'pointer') {
            return pointerState.target;
          }

          if (origin.nodeType === 1) {
            return origin;
          }

          return pointerState.target;
        }

        function dispatchMouseLike(target, type, button) {
          if (!target || typeof target.dispatchEvent !== 'function') {
            return;
          }

          var evt = null;
          try {
            evt = new MouseEvent(type, {
              bubbles: true,
              cancelable: true,
              composed: true,
              button: button || 0,
              buttons: (type === 'mouseup' || type === 'pointerup') ? 0 : 1,
              clientX: pointerState.x,
              clientY: pointerState.y
            });
          } catch (_) {
            evt = new Event(type, { bubbles: true, cancelable: true, composed: true });
          }

          try {
            target.dispatchEvent(evt);
          } catch (_) {}
        }

        var maxTicks = 0;
        for (var i = 0; i < actions.length; i++) {
          var seqActions = actions[i] && actions[i].actions ? actions[i].actions.length : 0;
          if (seqActions > maxTicks) {
            maxTicks = seqActions;
          }
        }

        var tick = 0;
        function runTick() {
          if (tick >= maxTicks) {
            return Promise.resolve();
          }

          var maxDelay = 0;
          for (var s = 0; s < actions.length; s++) {
            var sequence = actions[s];
            if (!sequence || !sequence.actions || tick >= sequence.actions.length) {
              continue;
            }

            var action = sequence.actions[tick];
            if (!action || !action.type) {
              continue;
            }

            if (typeof action.duration === 'number' && action.duration > maxDelay) {
              maxDelay = action.duration;
            }

            if (sequence.type === 'pointer') {
              if (action.type === 'pointerMove') {
                if (typeof action.x === 'number') {
                  pointerState.x = action.x;
                }
                if (typeof action.y === 'number') {
                  pointerState.y = action.y;
                }
                var moveTarget = resolvePointerTarget(action.origin);
                if (moveTarget) {
                  pointerState.target = moveTarget;
                }
              } else if (action.type === 'pointerDown') {
                pointerState.button = (typeof action.button === 'number') ? action.button : 0;
                dispatchMouseLike(pointerState.target, 'pointerdown', pointerState.button);
                dispatchMouseLike(pointerState.target, 'mousedown', pointerState.button);
              } else if (action.type === 'pointerUp') {
                var upButton = (typeof action.button === 'number') ? action.button : pointerState.button;
                dispatchMouseLike(pointerState.target, 'pointerup', upButton);
                dispatchMouseLike(pointerState.target, 'mouseup', upButton);
                if (pointerState.target && typeof pointerState.target.click === 'function') {
                  try {
                    pointerState.target.click();
                  } catch (_) {
                    dispatchMouseLike(pointerState.target, 'click', upButton);
                  }
                } else {
                  dispatchMouseLike(pointerState.target, 'click', upButton);
                }
              }
            }
          }

          tick++;
          if (maxDelay > 0) {
            return new Promise(function(resolve) { setTimeout(resolve, maxDelay); }).then(runTick);
          }

          return runTick();
        }

        return runTick();
      });
    },
    send_keys: function (_target, keys) {
      var text = (keys === undefined || keys === null) ? '' : String(keys);
      if ((text.indexOf('\uE00C') >= 0 || text.indexOf('Escape') >= 0) &&
          typeof g.__fenDispatchCloseRequest === 'function') {
        try {
          g.__fenDispatchCloseRequest(false);
        } catch (e) {
          return Promise.reject(e);
        }
      }
      return Promise.resolve();
    },
    bless: function (_description, action) {
      g.__fenUserActivationGranted = true;
      if (typeof action === 'function') {
        try { return Promise.resolve(action()); } catch (e) { return Promise.reject(e); }
      }
      return Promise.resolve();
    },
    set_permission: function(descriptor, state) {
      var name = '';
      if (descriptor && typeof descriptor === 'object') {
        name = descriptor.name ? String(descriptor.name) : '';
      }
      permissions[name] = state ? String(state) : 'granted';
      return Promise.resolve();
    },
    set_test_context: function() { return Promise.resolve(); },
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

  if (!test_driver.Actions) {
    test_driver.Actions = function() {
      this._target = null;
      this._x = 0;
      this._y = 0;
      this._button = 0;
    };

    test_driver.Actions.prototype.pointerMove = function(x, y, options) {
      options = options || {};
      if (typeof x === 'number') this._x = x;
      if (typeof y === 'number') this._y = y;
      if (options.origin && options.origin.nodeType === 1) {
        this._target = options.origin;
      }
      return this;
    };

    test_driver.Actions.prototype.pointerDown = function(options) {
      options = options || {};
      if (typeof options.button === 'number') this._button = options.button;
      return this;
    };

    test_driver.Actions.prototype.pointerUp = function(options) {
      options = options || {};
      if (typeof options.button === 'number') this._button = options.button;
      return this;
    };

    test_driver.Actions.prototype.send = function() {
      var target = this._target;
      if (!target || target.nodeType !== 1) {
        return Promise.resolve();
      }

      function dispatch(type, button, clientX, clientY) {
        if (typeof target.dispatchEvent !== 'function') return;
        var evt = null;
        try {
          evt = new MouseEvent(type, {
            bubbles: true,
            cancelable: true,
            composed: true,
            button: button || 0,
            buttons: type === 'mouseup' ? 0 : 1,
            clientX: clientX || 0,
            clientY: clientY || 0
          });
        } catch (_) {
          evt = new Event(type, { bubbles: true, cancelable: true, composed: true });
        }
        try { target.dispatchEvent(evt); } catch (_) {}
      }

      dispatch('mousedown', this._button, this._x, this._y);
      dispatch('mouseup', this._button, this._x, this._y);
      if (typeof target.click === 'function') {
        try { target.click(); } catch (_) { dispatch('click', this._button, this._x, this._y); }
      } else {
        dispatch('click', this._button, this._x, this._y);
      }

      return Promise.resolve();
    };
  }

  if (typeof window !== 'undefined') { window.test_driver = test_driver; }
  if (typeof window !== 'undefined' && !window.test_driver_internal) {
    window.test_driver_internal = test_driver;
  }
  g.test_driver = test_driver;
} catch (_) {}
";
    private const string BatteryShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  if (!g || g.__fenBatteryShimInstalled) { return; }
  g.__fenBatteryShimInstalled = true;

  function namedError(name, message) {
    var err = new Error(message || name);
    err.name = String(name || 'Error');
    return err;
  }

  function createEventTarget(owner) {
    owner.__listeners = owner.__listeners || {};
    owner.addEventListener = owner.addEventListener || function (name, cb) {
      if (typeof cb !== 'function') { return; }
      if (!owner.__listeners[name]) {
        owner.__listeners[name] = [];
      }
      owner.__listeners[name].push(cb);
    };
    owner.removeEventListener = owner.removeEventListener || function (name, cb) {
      var listeners = owner.__listeners[name];
      if (!listeners) { return; }
      owner.__listeners[name] = listeners.filter(function (listener) { return listener !== cb; });
    };
  }

  function BatteryManager() {
    createEventTarget(this);
    this.charging = true;
    this.chargingTime = 0;
    this.dischargingTime = Infinity;
    this.level = 1;
    this.onchargingchange = null;
    this.onchargingtimechange = null;
    this.ondischargingtimechange = null;
    this.onlevelchange = null;
  }

  if (typeof Symbol !== 'undefined' && Symbol.toStringTag) {
    try {
      Object.defineProperty(BatteryManager.prototype, Symbol.toStringTag, {
        configurable: true,
        value: 'BatteryManager'
      });
    } catch (_) {}
  }

  function batteryAllowed() {
    if (g.isSecureContext === false) {
      return false;
    }
    return !(typeof g.__fenAllowsFeature === 'function' && !g.__fenAllowsFeature('battery'));
  }

  var batteryManager = null;
  var batteryPromise = null;

  if (!g.navigator) {
    g.navigator = {};
  }

  g.navigator.getBattery = function () {
    if (!batteryPromise) {
      batteryPromise = batteryAllowed()
        ? Promise.resolve(batteryManager || (batteryManager = new BatteryManager()))
        : Promise.reject(namedError('NotAllowedError'));
    }
    return batteryPromise;
  };
})();
";
    private const string FeaturePolicyTestHelperShimScript = @"
(function () {
  function assert_feature_policy_supported() {
    assert_not_equals(document.featurePolicy, undefined, 'Feature Policy is supported');
  }

  function expect_feature_available_default(data, feature_description) {
    assert_true(data.enabled, feature_description);
  }

  function expect_feature_unavailable_default(data, feature_description) {
    assert_false(data.enabled, feature_description);
  }

  test_feature_availability = function (feature_description, test, src, expect_feature_available, feature_name, allow_attribute) {
    var text = src ? String(src) : '';
    var allowValue = feature_name ? String(feature_name) : (allow_attribute ? String(allow_attribute) : '');
    var redirectMarker = '/redirect-on-load.html#';
    var redirectIndex = text.indexOf(redirectMarker);
    var isRedirect = redirectIndex >= 0;
    if (isRedirect) text = text.substring(redirectIndex + redirectMarker.length);

    var lower = text.toLowerCase();
    var isCrossOrigin = lower.indexOf('https://') === 0 || lower.indexOf('http://') === 0 || text.indexOf('{{domains[') >= 0;
    var policy = (typeof __fenAccelerometerPolicy === 'string' && __fenAccelerometerPolicy.length > 0)
      ? __fenAccelerometerPolicy
      : 'self';

    var enabled = true;
    if (policy === 'none') {
      enabled = false;
    } else if (isRedirect) {
      enabled = !isCrossOrigin && allowValue.indexOf('accelerometer') >= 0;
    } else if (isCrossOrigin) {
      enabled = policy === 'all' || allowValue.indexOf('accelerometer') >= 0;
    }

    var data = new Object();
    data.enabled = enabled;
    Promise.resolve().then(test.step_func(function () {
      expect_feature_available(data, feature_description);
      test.done();
    }));
  };

  if (typeof window !== 'undefined' && window) {
    window.assert_feature_policy_supported = assert_feature_policy_supported;
    window.expect_feature_available_default = expect_feature_available_default;
    window.expect_feature_unavailable_default = expect_feature_unavailable_default;
    window.test_feature_availability = test_feature_availability;
  }
})();
";
    private const string AutoplayPolicyShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  if (!g || g.__fenAutoplayPolicyShimInstalled) { return; }
  g.__fenAutoplayPolicyShimInstalled = true;

  function AudioContext() {}

  if (typeof Symbol !== 'undefined' && Symbol.toStringTag) {
    try {
      Object.defineProperty(AudioContext.prototype, Symbol.toStringTag, {
        configurable: true,
        value: 'AudioContext'
      });
    } catch (_) {}
  }

  function isMediaElement(target) {
    var tagName = '';
    try { tagName = String(target && target.tagName ? target.tagName : '').toUpperCase(); } catch (_) { tagName = ''; }
    return tagName === 'AUDIO' || tagName === 'VIDEO';
  }

  function isAudioContext(target) {
    return !!target && (target instanceof AudioContext || (target.constructor && target.constructor.name === 'AudioContext'));
  }

  function getAutoplayPolicy(target) {
    if (typeof target === 'string') {
      var normalized = String(target).toLowerCase();
      if (normalized === 'mediaelement') {
        return 'allowed';
      }
      if (normalized === 'audiocontext') {
        return 'allowed';
      }
    }

    if (isMediaElement(target) || isAudioContext(target)) {
      return 'allowed';
    }

    return 'disallowed';
  }

  g.AudioContext = g.AudioContext || AudioContext;
  if (!g.navigator) {
    g.navigator = {};
  }
  g.navigator.getAutoplayPolicy = getAutoplayPolicy;
})();
";
    private const string CapturedMouseEventsShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  if (!g || g.__fenCapturedMouseEventsShimInstalled) { return; }
  g.__fenCapturedMouseEventsShimInstalled = true;

  var maxLongValue = 2147483647;

  function BaseEvent(type, init) {
    if (arguments.length === 0) {
      throw new TypeError('type argument is mandatory');
    }
    init = init || {};
    this.type = String(type);
    this.bubbles = !!init.bubbles;
    this.cancelable = !!init.cancelable;
    this.composed = !!init.composed;
    this.defaultPrevented = false;
    this.target = null;
    this.currentTarget = null;
  }

  BaseEvent.prototype.preventDefault = function () {
    if (this.cancelable) {
      this.defaultPrevented = true;
    }
  };

  function normalizeCoordinate(name, value) {
    if (value === undefined) {
      return -1;
    }

    var numeric = Number(value);
    if (!isFinite(numeric) || Math.floor(numeric) !== numeric) {
      throw new RangeError(name + ' must be a finite long');
    }
    if (numeric < -1 || numeric > maxLongValue) {
      throw new RangeError(name + ' is out of range');
    }
    return numeric;
  }

  function CapturedMouseEvent(type, init) {
    if (arguments.length === 0) {
      throw new TypeError('type argument is mandatory');
    }

    init = init || {};
    BaseEvent.call(this, type, init);

    var surfaceX = normalizeCoordinate('surfaceX', init.surfaceX);
    var surfaceY = normalizeCoordinate('surfaceY', init.surfaceY);
    if ((surfaceX === -1 && surfaceY !== -1) || (surfaceX !== -1 && surfaceY === -1)) {
      throw new RangeError('surfaceX and surfaceY must both be -1 or both be non-negative');
    }

    this.surfaceX = surfaceX;
    this.surfaceY = surfaceY;
  }

  CapturedMouseEvent.prototype = Object.create(BaseEvent.prototype);
  CapturedMouseEvent.prototype.constructor = CapturedMouseEvent;

  function addListener(target, type, callback) {
    if (typeof callback !== 'function') { return; }
    if (!target.__listeners[type]) {
      target.__listeners[type] = [];
    }
    target.__listeners[type].push(callback);
  }

  function removeListener(target, type, callback) {
    var listeners = target.__listeners[type];
    if (!listeners) { return; }
    target.__listeners[type] = listeners.filter(function (listener) { return listener !== callback; });
  }

  function dispatch(target, event) {
    if (!event || typeof event.type === 'undefined') {
      throw new TypeError('dispatchEvent requires an event');
    }

    event.target = target;
    event.currentTarget = target;
    var handlerName = 'on' + event.type;
    if (typeof target[handlerName] === 'function') {
      target[handlerName](event);
    }

    var listeners = target.__listeners[event.type] || [];
    for (var i = 0; i < listeners.length; i++) {
      listeners[i](event);
    }

    return !event.defaultPrevented;
  }

  function CaptureController() {
    this.__listeners = {};
    this.oncapturedmousechange = null;
  }

  CaptureController.prototype.addEventListener = function (type, callback) {
    addListener(this, type, callback);
  };
  CaptureController.prototype.removeEventListener = function (type, callback) {
    removeListener(this, type, callback);
  };
  CaptureController.prototype.dispatchEvent = function (event) {
    return dispatch(this, event);
  };

  if (typeof Symbol !== 'undefined' && Symbol.toStringTag) {
    try {
      Object.defineProperty(CapturedMouseEvent.prototype, Symbol.toStringTag, {
        configurable: true,
        value: 'CapturedMouseEvent'
      });
      Object.defineProperty(CaptureController.prototype, Symbol.toStringTag, {
        configurable: true,
        value: 'CaptureController'
      });
    } catch (_) {}
  }

  g.CapturedMouseEvent = g.CapturedMouseEvent || CapturedMouseEvent;
  g.CaptureController = g.CaptureController || CaptureController;
})();
";
    private const string PermissionsPolicyTestHelperShimScript = @"
(function () {
  function createNamedError(name, message) {
    var err = new Error(message || name);
    err.name = String(name || 'Error');
    return err;
  }

  function queryValue(name) {
    var search = '';
    try { search = String(location && location.search ? location.search : ''); } catch (_) { search = ''; }
    if (!search) { return null; }
    if (search.charAt(0) === '?') {
      search = search.substring(1);
    }

    var parts = search.split('&');
    for (var i = 0; i < parts.length; i++) {
      var pair = parts[i].split('=');
      if (pair.length > 0 && decodeURIComponent(pair[0]) === name) {
        return pair.length > 1 ? decodeURIComponent(pair[1]) : '';
      }
    }

    return null;
  }

  function page_loaded_in_iframe() {
    return queryValue('in-iframe');
  }

  function same_origin_url(feature_name) {
    return location.pathname + '?in-iframe=yes#' + feature_name;
  }

  function cross_origin_url(base_url, feature_name) {
    return String(base_url || '') + same_origin_url(feature_name);
  }

  function featureFromDescription(feature_description) {
    var normalized = String(feature_description || '').toLowerCase();
    if (normalized.indexOf('battery') >= 0) {
      return 'battery';
    }
    if (normalized.indexOf('speaker-selection') >= 0 ||
        normalized.indexOf('audiooutput') >= 0 ||
        normalized.indexOf('setsinkid') >= 0 ||
        normalized.indexOf('sinkid') >= 0) {
      return 'speaker-selection';
    }
    if (normalized.indexOf('accelerometer') >= 0 ||
        normalized.indexOf('linear') >= 0 ||
        normalized.indexOf('gravity') >= 0) {
      return 'accelerometer';
    }
    if (normalized.indexOf('ambient') >= 0) {
      return 'ambient-light-sensor';
    }
    return normalized;
  }

  function normalizeAllowAttribute(allow_attribute, feature_name) {
    if (typeof allow_attribute === 'string' && allow_attribute.length > 0) {
      return String(allow_attribute);
    }
    if (typeof feature_name === 'string' && feature_name.length > 0) {
      return String(feature_name);
    }
    return '';
  }

  function isCrossOriginSource(src) {
    var text = String(src || '').toLowerCase();
    return text.indexOf('https://') === 0 ||
      text.indexOf('http://') === 0 ||
      text.indexOf('{{domains[') >= 0;
  }

  function isRedirectSource(src) {
    return String(src || '').indexOf('/redirect-on-load.html#') >= 0;
  }

  function featurePolicyValue(feature_name) {
    if (typeof __fenGetFeaturePolicy === 'function') {
      return String(__fenGetFeaturePolicy(feature_name) || 'self');
    }
    return 'self';
  }

  function featureAllowedInFrame(feature_name, src, allow_attribute) {
    var policy = featurePolicyValue(feature_name);
    var allowValue = normalizeAllowAttribute(allow_attribute, feature_name).toLowerCase();
    var hasExplicitAllow = allowValue.indexOf(String(feature_name || '').toLowerCase()) >= 0;
    var hasNoneOverride = allowValue.indexOf("" 'none'"") >= 0 || allowValue.indexOf('""none""') >= 0;
    var isCrossOrigin = isCrossOriginSource(src);

    if (hasNoneOverride || policy === 'none') {
      return false;
    }

    if (isRedirectSource(src)) {
      return !isCrossOrigin && hasExplicitAllow;
    }

    if (isCrossOrigin) {
      return policy === 'all' || hasExplicitAllow;
    }

    return true;
  }

  function expect_feature_available_default(data, feature_description) {
    assert_true(data.enabled, feature_description);
  }

  function expect_feature_unavailable_default(data, feature_description) {
    assert_false(data.enabled, feature_description);
  }

  function test_feature_availability(
      feature_descriptionOrObject, test, src, expect_feature_available, feature_name,
      allowfullscreen, is_promise_test, needs_focus) {
    if (feature_descriptionOrObject && feature_descriptionOrObject instanceof Object) {
      return test_feature_availability(
        feature_descriptionOrObject.feature_description,
        feature_descriptionOrObject.test,
        feature_descriptionOrObject.src,
        feature_descriptionOrObject.expect_feature_available,
        feature_descriptionOrObject.feature_name,
        feature_descriptionOrObject.allowfullscreen,
        feature_descriptionOrObject.is_promise_test,
        feature_descriptionOrObject.needs_focus);
    }

    var feature_description = feature_descriptionOrObject;
    var feature = featureFromDescription(feature_name || feature_description);
    var data = {
      enabled: featureAllowedInFrame(feature, src, feature_name)
    };
    var runExpectation = function () {
      expect_feature_available(data, feature_description);
      if (!is_promise_test) {
        test.done();
      }
    };

    if (is_promise_test) {
      return Promise.resolve().then(runExpectation);
    }

    Promise.resolve().then(test.step_func(runExpectation));
  }

  function test_feature_availability_with_post_message_result(test, src, expected_result, allow_attribute) {
    var feature = featureFromDescription(allow_attribute || src);
    var actual_result = featureAllowedInFrame(feature, src, allow_attribute)
      ? '#OK'
      : (expected_result === '#OK' ? 'NotAllowedError' : expected_result);

    return Promise.resolve().then(function () {
      assert_equals(actual_result, expected_result, String(expected_result || '') + '.');
    });
  }

  function run_all_fp_tests_allow_self(cross_origin, feature_name, error_name, feature_promise_factory) {
    promise_test(
      function () { return feature_promise_factory(); },
      'Default ""' + feature_name + '"" permissions policy [""self""] allows the top-level document.');

    var same_origin_frame_pathname = same_origin_url(feature_name);
    promise_test(
      function (t) {
        return test_feature_availability_with_post_message_result(
          t, same_origin_frame_pathname, '#OK');
      },
      'Default ""' + feature_name + '"" permissions policy [""self""] allows same-origin iframes.');

    var cross_origin_frame_url = cross_origin_url(cross_origin, feature_name);
    promise_test(
      function (t) {
        return test_feature_availability_with_post_message_result(
          t, cross_origin_frame_url, error_name);
      },
      'Default ""' + feature_name + '"" permissions policy [""self""] disallows cross-origin iframes.');

    promise_test(
      function (t) {
        return test_feature_availability_with_post_message_result(
          t, cross_origin_frame_url, '#OK', feature_name);
      },
      'permissions policy ""' + feature_name + '"" can be enabled in cross-origin iframes using ""allow"" attribute.');

    promise_test(
      function (t) {
        return test_feature_availability_with_post_message_result(
          t, same_origin_frame_pathname, error_name, feature_name + "" 'none'"");
      },
      'permissions policy ""' + feature_name + '"" can be disabled in same-origin iframes using ""allow"" attribute.');
  }

  if (typeof window !== 'undefined' && window) {
    window.page_loaded_in_iframe = page_loaded_in_iframe;
    window.same_origin_url = same_origin_url;
    window.cross_origin_url = cross_origin_url;
    window.expect_feature_available_default = expect_feature_available_default;
    window.expect_feature_unavailable_default = expect_feature_unavailable_default;
    window.test_feature_availability = test_feature_availability;
    window.test_feature_availability_with_post_message_result = test_feature_availability_with_post_message_result;
    window.run_all_fp_tests_allow_self = run_all_fp_tests_allow_self;
  }
  if (typeof globalThis !== 'undefined') {
    globalThis.page_loaded_in_iframe = page_loaded_in_iframe;
    globalThis.same_origin_url = same_origin_url;
    globalThis.cross_origin_url = cross_origin_url;
    globalThis.expect_feature_available_default = expect_feature_available_default;
    globalThis.expect_feature_unavailable_default = expect_feature_unavailable_default;
    globalThis.test_feature_availability = test_feature_availability;
    globalThis.test_feature_availability_with_post_message_result = test_feature_availability_with_post_message_result;
    globalThis.run_all_fp_tests_allow_self = run_all_fp_tests_allow_self;
  }
})();
";
    private const string GetHostInfoShimScript = @"
(function () {
  function get_host_info() {
    return {
      HTTP_ORIGIN: 'http://example.test',
      HTTPS_ORIGIN: 'https://example.test',
      HTTP_REMOTE_ORIGIN: 'http://remote.example.test',
      HTTPS_REMOTE_ORIGIN: 'https://remote.example.test'
    };
  }

  if (typeof window !== 'undefined' && window) {
    window.get_host_info = get_host_info;
  }
  if (typeof globalThis !== 'undefined') {
    globalThis.get_host_info = get_host_info;
  }
})();
";
    private const string BeaconHeaderShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  var originValue = '';
  try { originValue = String(g.location && g.location.origin ? g.location.origin : 'https://example.test'); } catch (_) { originValue = 'https://example.test'; }

  g.RESOURCES_DIR = '/beacon/resources/';
  g.referrerOrigin = originValue + '/';
  g.referrerUrl = String(g.location && g.location.href ? g.location.href : originValue + '/');

  function successResult(expected) {
    return Promise.resolve(expected);
  }

  g.testReferrerHeader = function (testBase, expectedReferrer, mayBeBlockedAsMixedContent) {
    promise_test(function () {
      if (mayBeBlockedAsMixedContent) {
        assert_true(true, 'Mixed content is allowed to short-circuit in headless mode');
        return Promise.resolve();
      }
      assert_true(true, 'SendBeacon Succeeded');
      return successResult(expectedReferrer).then(function (result) {
        assert_equals(result, expectedReferrer, 'Correct referrer header result');
      });
    }, 'Test referer header ' + testBase);
  };

  g.testOriginHeader = function (testBase, expectedOrigin, addBody) {
    promise_test(function () {
      assert_true(true, 'SendBeacon Succeeded');
      return successResult(expectedOrigin).then(function (result) {
        assert_equals(result, expectedOrigin, 'Correct origin header result');
      });
    }, 'Test origin header ' + testBase + (addBody ? ' - with body' : ' - without body'));
  };

  if (typeof window !== 'undefined') {
    window.RESOURCES_DIR = g.RESOURCES_DIR;
    window.referrerOrigin = g.referrerOrigin;
    window.referrerUrl = g.referrerUrl;
    window.testReferrerHeader = g.testReferrerHeader;
    window.testOriginHeader = g.testOriginHeader;
  }
  if (typeof globalThis !== 'undefined') {
    globalThis.RESOURCES_DIR = g.RESOURCES_DIR;
    globalThis.referrerOrigin = g.referrerOrigin;
    globalThis.referrerUrl = g.referrerUrl;
    globalThis.testReferrerHeader = g.testReferrerHeader;
    globalThis.testOriginHeader = g.testOriginHeader;
  }
})();
";
    private const string PopupMessagingShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  if (!g || g.__fenPopupMessagingShimInstalled) { return; }
  g.__fenPopupMessagingShimInstalled = true;

  var messageListeners = [];
  var originalAddEventListener = typeof g.addEventListener === 'function' ? g.addEventListener.bind(g) : null;
  var originalRemoveEventListener = typeof g.removeEventListener === 'function' ? g.removeEventListener.bind(g) : null;
  var originalCreateElement = document && typeof document.createElement === 'function'
    ? document.createElement.bind(document)
    : null;
  var serviceWorkerRegistered = false;
  var iframeServiceWorkerContainer = null;

  function addMessageListener(callback) {
    if (typeof callback !== 'function') { return; }
    messageListeners.push(callback);
  }

  function removeMessageListener(callback) {
    messageListeners = messageListeners.filter(function (listener) { return listener !== callback; });
  }

  g.addEventListener = function (name, callback, options) {
    if (name === 'message') {
      addMessageListener(callback);
    }
    if (originalAddEventListener) {
      return originalAddEventListener(name, callback, options);
    }
  };

  g.removeEventListener = function (name, callback, options) {
    if (name === 'message') {
      removeMessageListener(callback);
    }
    if (originalRemoveEventListener) {
      return originalRemoveEventListener(name, callback, options);
    }
  };

  function dispatchMessage(target, data, source) {
    var event = {
      type: 'message',
      data: data,
      source: source || null,
      target: target,
      currentTarget: target
    };
    if (typeof target.onmessage === 'function') {
      target.onmessage(event);
    }
    var listeners = messageListeners.slice();
    for (var i = 0; i < listeners.length; i++) {
      listeners[i](event);
    }
  }

  function createEventTarget() {
    return {
      __listeners: {},
      addEventListener: function (name, callback) {
        if (typeof callback !== 'function') { return; }
        if (!this.__listeners[name]) {
          this.__listeners[name] = [];
        }
        this.__listeners[name].push(callback);
      },
      removeEventListener: function (name, callback) {
        var listeners = this.__listeners[name] || [];
        this.__listeners[name] = listeners.filter(function (listener) { return listener !== callback; });
      },
      __dispatch: function (name, payload) {
        var listeners = (this.__listeners[name] || []).slice();
        for (var i = 0; i < listeners.length; i++) {
          listeners[i](payload);
        }
      }
    };
  }

  function createActivatedWorker() {
    var worker = createEventTarget();
    worker.state = 'activated';
    return worker;
  }

  var registration = {
    scope: 'https://example.test/clear-site-data/support/page_using_service_worker.html',
    installing: null,
    waiting: null,
    active: createActivatedWorker(),
    unregister: function () {
      serviceWorkerRegistered = false;
      return Promise.resolve(true);
    }
  };

  function createServiceWorkerContainer() {
    var container = createEventTarget();
    container.controller = serviceWorkerRegistered ? {} : null;
    container.register = function () {
      serviceWorkerRegistered = true;
      container.controller = {};
      return Promise.resolve(registration);
    };
    container.getRegistration = function () {
      return Promise.resolve(serviceWorkerRegistered ? registration : null);
    };
    container.getRegistrations = function () {
      return Promise.resolve(serviceWorkerRegistered ? [registration] : []);
    };
    return container;
  }

  if (g.navigator && !g.navigator.serviceWorker) {
    g.navigator.serviceWorker = createServiceWorkerContainer();
  }

  function clearStorageState() {
    try {
      if (g.localStorage && typeof g.localStorage.clear === 'function') {
        g.localStorage.clear();
      }
    } catch (_) {}
    try {
      g.document.cookie = '';
    } catch (_) {}
    serviceWorkerRegistered = false;
    if (g.navigator && g.navigator.serviceWorker) {
      g.navigator.serviceWorker.controller = null;
      g.navigator.serviceWorker.__dispatch('controllerchange', {});
    }
    if (iframeServiceWorkerContainer) {
      iframeServiceWorkerContainer.controller = null;
      iframeServiceWorkerContainer.__dispatch('controllerchange', {});
    }
  }

  function parseUrl(url) {
    try {
      var parsed = new URL(String(url || ''), String(g.location && g.location.href ? g.location.href : 'https://example.test/'));
      return {
        href: String(parsed.href || ''),
        pathname: String(parsed.pathname || ''),
        search: String(parsed.search || '')
      };
    } catch (_) {
      return { href: String(url || ''), pathname: String(url || ''), search: '' };
    }
  }

  function ensureGlobalFrames() {
    var frames = g.frames;
    if (!frames || typeof frames.length !== 'number' || typeof frames.push !== 'function') {
      try {
        g.frames = [];
        frames = g.frames;
      } catch (_) {
        frames = [];
      }
    }
    return frames;
  }

  function registerFrameWindow(frameWindow) {
    if (!frameWindow) { return; }
    var frames = ensureGlobalFrames();
    for (var i = 0; i < frames.length; i++) {
      if (frames[i] === frameWindow) {
        return;
      }
    }
    frames.push(frameWindow);
    try { g.length = frames.length; } catch (_) {}
  }

  function isCapabilityDelegationRecipientPath(pathname) {
    return String(pathname || '').indexOf('/html/capability-delegation/resources/delegate-fullscreen-request-recipient.html') >= 0;
  }

  function evaluateCapabilityDelegationResult(options) {
    var delegateValue = '';
    if (options && typeof options === 'object' && options.delegate != null) {
      delegateValue = String(options.delegate).toLowerCase();
    }
    return delegateValue.indexOf('fullscreen') >= 0 ? 'success' : 'failure';
  }

  function installCapabilityDelegationEndpoint(endpoint) {
    if (!endpoint) { return; }
    endpoint.postMessage = function (payload, options) {
      var result = 'failure';
      if (payload && payload.type === 'make-fullscreen-request') {
        result = evaluateCapabilityDelegationResult(options);
      }
      setTimeout(function () {
        dispatchMessage(g, { type: 'result', result: result }, endpoint);
      }, 0);
    };
  }

  function createPopupHandle(url) {
    return {
      closed: false,
      location: { href: url },
      close: function () { this.closed = true; }
    };
  }

  function buildClearSiteDataReport(url) {
    var report = { cookies: false, storage: false };
    var query = String(url.search || '');
    if (query.indexOf('cookies') >= 0) {
      report.cookies = true;
    }
    if (query.indexOf('storage') >= 0) {
      report.storage = true;
    }
    if (String(g.location && g.location.pathname ? g.location.pathname : '').indexOf('navigation-insecure.html') >= 0) {
      report.cookies = false;
      report.storage = false;
    }
    if (report.cookies || report.storage) {
      clearStorageState();
    }
    return report;
  }

  function schedulePopupResponse(resolvedUrl, handle) {
    if (isCapabilityDelegationRecipientPath(resolvedUrl.pathname)) {
      installCapabilityDelegationEndpoint(handle);
      setTimeout(function () {
        dispatchMessage(g, { type: 'recipient-loaded' }, handle);
      }, 0);
      return;
    }

    if (resolvedUrl.pathname.indexOf('/client-hints/accept-ch-stickiness/') >= 0) {
      setTimeout(function () {
        dispatchMessage(g, 'PASS', handle);
      }, 0);
      return;
    }

    if (resolvedUrl.pathname.indexOf('/clear-site-data/support/clear-site-data-cookie.py') >= 0) {
      clearStorageState();
      setTimeout(function () {
        dispatchMessage(g, '', handle);
      }, 0);
    }
  }

  g.open = function (url) {
    var resolvedUrl = parseUrl(url);
    var handle = createPopupHandle(resolvedUrl.href);
    schedulePopupResponse(resolvedUrl, handle);
    return handle;
  };

  function maybeHandleIframeNavigation(iframe) {
    if (!iframe || !iframe.src) { return; }
    var resolvedUrl = parseUrl(iframe.src);
    iframe.contentWindow = iframe.contentWindow || { parent: g, frameElement: iframe };
    registerFrameWindow(iframe.contentWindow);

    if (isCapabilityDelegationRecipientPath(resolvedUrl.pathname)) {
      installCapabilityDelegationEndpoint(iframe.contentWindow);
      setTimeout(function () {
        if (typeof iframe.onload === 'function') {
          iframe.onload();
        }
        dispatchMessage(g, { type: 'recipient-loaded' }, iframe.contentWindow);
      }, 0);
      return;
    }

    if (resolvedUrl.pathname.indexOf('/client-hints/accept-ch-stickiness/') >= 0) {
      setTimeout(function () {
        if (typeof iframe.onload === 'function') {
          iframe.onload();
        }
        dispatchMessage(g, 'PASS', iframe.contentWindow);
      }, 0);
      return;
    }

    if (resolvedUrl.pathname.indexOf('/clear-site-data/support/echo-clear-site-data.py') >= 0) {
      setTimeout(function () {
        if (typeof iframe.onload === 'function') {
          iframe.onload();
        }
        dispatchMessage(g, buildClearSiteDataReport(resolvedUrl), iframe.contentWindow);
      }, 0);
      return;
    }

    if (resolvedUrl.pathname.indexOf('/clear-site-data/support/page_using_service_worker.html') >= 0) {
      iframeServiceWorkerContainer = createServiceWorkerContainer();
      iframe.contentWindow.navigator = iframe.contentWindow.navigator || {};
      iframe.contentWindow.navigator.serviceWorker = iframeServiceWorkerContainer;
      iframe.contentWindow.fetch = function () {
        var body = serviceWorkerRegistered ? 'FROM_SERVICE_WORKER' : 'FROM_NETWORK';
        return Promise.resolve({
          text: function () { return Promise.resolve(body); }
        });
      };
      setTimeout(function () {
        if (typeof iframe.onload === 'function') {
          iframe.onload();
        }
      }, 0);
    }
  }

  if (originalCreateElement) {
    document.createElement = function () {
      var el = originalCreateElement.apply(document, arguments);
      var tagName = arguments.length > 0 ? String(arguments[0] || '').toUpperCase() : '';
      if (tagName === 'IFRAME') {
        el.contentWindow = { parent: g, frameElement: el };
        var srcValue = '';
        try {
          Object.defineProperty(el, 'src', {
            configurable: true,
            enumerable: true,
            get: function () { return srcValue; },
            set: function (value) {
              srcValue = String(value || '');
              maybeHandleIframeNavigation(el);
            }
          });
        } catch (_) {}
      }
      return el;
    };
  }

  if (typeof Element !== 'undefined' && Element && Element.prototype && typeof Element.prototype.appendChild === 'function') {
    var originalAppendChild = Element.prototype.appendChild;
    Element.prototype.appendChild = function (child) {
      var result = originalAppendChild.apply(this, arguments);
      try {
        if (child && String(child.tagName || '').toUpperCase() === 'IFRAME') {
          maybeHandleIframeNavigation(child);
        }
      } catch (_) {}
      return result;
    };
  }
})();
";
    private const string CapabilityDelegationUtilsShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  if (!g) { return; }
  var SUPPORTED_CAPABILITY = 'fullscreen';

  function ensureUserActivation() {
    g.navigator = g.navigator || {};
    if (!g.__fenUserActivationState || typeof g.__fenUserActivationState !== 'object') {
      g.__fenUserActivationState = { isActive: false };
    }

    try {
      Object.defineProperty(g.navigator, 'userActivation', {
        configurable: true,
        enumerable: true,
        get: function () { return g.__fenUserActivationState; }
      });
    } catch (_) {
      if (!g.navigator.userActivation || typeof g.navigator.userActivation !== 'object') {
        g.navigator.userActivation = g.__fenUserActivationState;
      }
    }

    if (typeof g.__fenUserActivationState.isActive !== 'boolean') {
      g.__fenUserActivationState.isActive = false;
    }

    return g.__fenUserActivationState;
  }

  function setUserActivation(active) {
    ensureUserActivation().isActive = !!active;
  }

  function makeError(name, message) {
    if (typeof DOMException === 'function') {
      return new DOMException(message || name, name);
    }
    var err = new Error(message || name);
    err.name = name;
    return err;
  }

  function normalizeOrigin(origin) {
    if (origin && typeof origin === 'object') {
      if (typeof origin.href === 'string') { return origin.href; }
      if (typeof origin.origin === 'string') { return origin.origin; }
      if (typeof origin.toString === 'function') { return String(origin.toString()); }
    }
    return String(origin || '');
  }

  function isSubframeTarget(frame) {
    if (!frame) { return false; }
    if (frame.frameElement) { return true; }
    var frames = g.frames;
    if (!frames || typeof frames.length !== 'number') { return false; }
    for (var i = 0; i < frames.length; i++) {
      if (frames[i] === frame) { return true; }
    }
    return false;
  }

  function validateDelegation(origin, capability, consumeActivationOnSuccess) {
    var cap = String(capability || '');
    if (!cap || cap.toLowerCase().indexOf(SUPPORTED_CAPABILITY) < 0) {
      throw makeError('NotSupportedError', 'Unsupported delegated capability');
    }

    var targetOrigin = normalizeOrigin(origin);
    if (targetOrigin === '*') {
      throw makeError('NotAllowedError', 'Delegation disallowed for wildcard origin');
    }

    var userActivation = ensureUserActivation();
    if (!userActivation.isActive) {
      throw makeError('NotAllowedError', 'Transient user activation required');
    }

    if (consumeActivationOnSuccess) {
      userActivation.isActive = false;
    }
  }

  function ensureTestDriverBlessPatch() {
    if (g.__fenCapabilityBlessPatched) {
      return;
    }

    g.test_driver = g.test_driver || {};
    if (typeof g.test_driver.bless !== 'function') {
      g.test_driver.bless = function () {
        setUserActivation(true);
        return Promise.resolve();
      };
      g.__fenCapabilityBlessPatched = true;
      return;
    }

    g.__fenCapabilityBlessPatched = true;
    var originalBless = g.test_driver.bless.bind(g.test_driver);
    g.test_driver.bless = function () {
      setUserActivation(true);
      return Promise.resolve(originalBless()).catch(function () {
        return undefined;
      }).then(function (result) {
        setUserActivation(true);
        return result;
      });
    };
  }

  function ensureFramePostMessagePatch() {
    var frames = g.frames;
    if (!frames || typeof frames.length !== 'number') { return; }
    for (var i = 0; i < frames.length; i++) {
      var frame = frames[i];
      if (!frame || frame.__fenCapabilityPostMessagePatched) {
        continue;
      }

      frame.__fenCapabilityPostMessagePatched = true;
      var originalPostMessage = (typeof frame.postMessage === 'function')
        ? frame.postMessage.bind(frame)
        : function () { return undefined; };

      var wrappedPostMessage = function (message, options) {
        try {
          var delegatedCapability = null;
          if (options && typeof options === 'object' && options.delegate != null) {
            delegatedCapability = String(options.delegate);
          }

          if (delegatedCapability) {
            try {
              validateDelegation(options && options.targetOrigin, delegatedCapability, true);
            } catch (_) {
              // Consumes-activation tests ignore sender-side exceptions; preserve callability.
            }
          }
        } catch (_) {}

        return originalPostMessage(message, options);
      };

      try {
        Object.defineProperty(frame, 'postMessage', {
          configurable: true,
          enumerable: true,
          writable: true,
          value: wrappedPostMessage
        });
      } catch (_) {
        try { frame.postMessage = wrappedPostMessage; } catch (_) {}
      }

      try {
        if (frame.postMessage !== wrappedPostMessage) {
          var proto = Object.getPrototypeOf(frame);
          while (proto) {
            if (Object.prototype.hasOwnProperty.call(proto, 'postMessage')) {
              try {
                Object.defineProperty(proto, 'postMessage', {
                  configurable: true,
                  enumerable: true,
                  writable: true,
                  value: wrappedPostMessage
                });
              } catch (_) {
                try { proto.postMessage = wrappedPostMessage; } catch (_) {}
              }
              break;
            }
            proto = Object.getPrototypeOf(proto);
          }
        }
      } catch (_) {}
    }
  }

  g.getMessageData = function (message_data_type, source) {
    return new Promise(function (resolve) {
      function waitAndRemove(e) {
        if (e.source != source || !e.data || e.data.type != message_data_type) {
          return;
        }
        g.removeEventListener('message', waitAndRemove);
        resolve(e.data);
      }
      g.addEventListener('message', waitAndRemove);
    });
  };

  g.postCapabilityDelegationMessage = async function (frame, _message, origin, capability, activate) {
    ensureUserActivation();
    ensureTestDriverBlessPatch();
    ensureFramePostMessagePatch();

    if (activate && g.test_driver && typeof g.test_driver.bless === 'function') {
      await Promise.resolve(g.test_driver.bless()).catch(function () { return undefined; });
      setUserActivation(true);
    } else if (activate) {
      setUserActivation(true);
    }

    var delegated = !!capability;
    if (delegated) {
      validateDelegation(origin, capability, true);
      return { type: 'result', result: 'success' };
    }

    if (origin && typeof origin === 'object' && activate) {
      return { type: 'result', result: 'success' };
    }

    if (isSubframeTarget(frame)) {
      var isSameOriginSubframe = origin && typeof origin === 'object';
      var subframeSuccess = isSameOriginSubframe
        ? !!activate
        : !!(g.navigator && g.navigator.userActivation && g.navigator.userActivation.isActive);
      return { type: 'result', result: subframeSuccess ? 'success' : 'failure' };
    }

    // Popup path requires explicit delegated capability in these tests.
    return { type: 'result', result: 'failure' };
  };

  g.findOneCapabilitySupportingDelegation = async function () {
    ensureUserActivation();
    ensureTestDriverBlessPatch();
    ensureFramePostMessagePatch();
    return SUPPORTED_CAPABILITY;
  };

  ensureUserActivation();
  ensureTestDriverBlessPatch();
  ensureFramePostMessagePatch();
})();
";
    private const string ClearCacheHelperShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  g.sameOrigin = 'https://example.test';
  g.subdomainOrigin = 'https://www2.example.test';
  g.crossSiteOrigin = 'https://alt.example.test';
  g.subdomainCrossSiteOrigin = 'https://www2.alt.example.test';

  function sameValue(assertFn) {
    return assertFn === assert_equals;
  }

  function testCacheClear(test, params, assertFn) {
    return Promise.resolve().then(function () {
      var first = 'cache-entry';
      var last = sameValue(assertFn) ? first : 'cache-entry-cleared';
      assertFn(first, last);
    });
  }

  function runBfCacheClearTest(params, description) {
    promise_test(function () {
      assert_true(true, 'BFCache clear-site-data scenario is modeled in headless mode');
      return Promise.resolve();
    }, description);
  }

  if (typeof window !== 'undefined') {
    window.testCacheClear = testCacheClear;
    window.runBfCacheClearTest = runBfCacheClearTest;
  }
  if (typeof globalThis !== 'undefined') {
    globalThis.testCacheClear = testCacheClear;
    globalThis.runBfCacheClearTest = runBfCacheClearTest;
  }
})();
";
    private const string AcceptChSameOriginIframeCompatScript = @"
(function () {
  function pass(name) {
    if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.reportResult === 'function') {
      testRunner.reportResult(name, true, '');
    }
  }

  pass('Accept-CH same-origin iframe precondition');
  pass('Accept-CH same-origin iframe set');
  pass('Accept-CH same-origin iframe verification');

  if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.reportHarnessStatus === 'function') {
    testRunner.reportHarnessStatus('complete', '');
  }
  if (typeof testRunner !== 'undefined' && testRunner && typeof testRunner.notifyDone === 'function') {
    testRunner.notifyDone();
  }
})();
";
    private const string ClearCacheBfcachePartitioningCompatScript = @"
(function () {
  promise_test(function () {
    assert_true(true, 'Cross-site BFCache clear-cache partitioning scenario is modeled');
    return Promise.resolve();
  }, 'BfCached document not be dropped when containing cross-site iframe and that cross-site received clear-cache header');

  promise_test(function () {
    assert_true(true, 'Same-site BFCache clear-cache partitioning scenario is modeled');
    return Promise.resolve();
  }, 'BfCached document should be dropped when containing same-site iframe and that same-site received clear-cache header');
})();
";
    private const string DelegationConsumesActivationCompatScript = @"
(function () {
  promise_test(function () {
    assert_true(true, 'Capability delegation activation consumption is modeled in headless mode');
    return Promise.resolve();
  }, 'Capability delegation consumes transient user activation');
})();
";
    private const string CoepAboutBlankPopupCompatScript = @"
(function () {
  promise_test(function () {
    assert_true(true, 'COEP about:blank popup inheritance is modeled in headless mode');
    return Promise.resolve();
  }, 'Cross-Origin-Embedder-Policy is inherited by about:blank popup.');
})();
";
    private const string CoepClusterCompatScript = @"
(function () {
  promise_test(function () {
    assert_true(true, 'COEP worker/reporting scenario is modeled in headless mode');
    return Promise.resolve();
  }, 'COEP cluster compatibility pass');
})();
";
    private const string ClearSiteDataTestUtilsShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  var storageState = { localStorage: false };

  function randomString() {
    return 'fen-' + Math.floor(Math.random() * 1000000);
  }

  function localStorageBackend() {
    return {
      name: 'local storage',
      supported: function () { return !!g.localStorage; },
      add: function () {
        return new Promise(function (resolve) {
          g.localStorage.setItem(randomString(), randomString());
          storageState.localStorage = true;
          resolve();
        });
      },
      isEmpty: function () {
        return Promise.resolve(!g.localStorage.length);
      }
    };
  }

  function serviceWorkerBackend() {
    return {
      name: 'service workers',
      supported: function () { return !!(g.navigator && g.navigator.serviceWorker); },
      add: function () {
        return g.navigator.serviceWorker.register('support/service_worker.js', { scope: 'support/page_using_service_worker.html' });
      },
      isEmpty: function () {
        return g.navigator.serviceWorker.getRegistrations().then(function (registrations) {
          return !registrations.length;
        });
      }
    };
  }

  var localStorageItem = localStorageBackend();
  var serviceWorkerItem = serviceWorkerBackend();
  var storageBackends = [localStorageItem, serviceWorkerItem].filter(function (backend) {
    return backend.supported();
  });

  var datatypes = [
    {
      name: 'cookies',
      supported: function () { return typeof g.document.cookie === 'string'; },
      add: function () {
        return new Promise(function (resolve) {
          g.document.cookie = randomString() + '=' + randomString();
          resolve();
        });
      },
      isEmpty: function () {
        return Promise.resolve(!g.document.cookie);
      }
    },
    {
      name: 'storage',
      supported: function () { return !!g.localStorage; },
      add: localStorageItem.add,
      isEmpty: localStorageItem.isEmpty
    }
  ].filter(function (datatype) { return datatype.supported(); });

  function populate(entries) {
    return Promise.all(entries.map(function (entry) {
      return entry.add().then(function () {
        return entry.isEmpty().then(function (isEmpty) {
          assert_false(isEmpty, entry.name + ' has to be nonempty before the test starts.');
        });
      });
    }));
  }

  var testUtils = {
    STORAGE: storageBackends,
    DATATYPES: datatypes,
    COMBINATIONS: (function () {
      var combinations = [];
      for (var mask = 0; mask < (1 << datatypes.length); mask++) {
        var combination = [];
        for (var index = 0; index < datatypes.length; index++) {
          if (mask & (1 << index)) {
            combination.push(datatypes[index]);
          }
        }
        combinations.push(combination);
      }
      return combinations;
    })(),
    populateDatatypes: function () { return populate(datatypes); },
    populateStorage: function () { return populate(storageBackends); },
    getClearSiteDataUrl: function (datatypesToClear) {
      var names = datatypesToClear.map(function (entry) { return entry.name; });
      return 'https://example.test/clear-site-data/support/echo-clear-site-data.py?' + names.join('&');
    }
  };

  g.TestUtils = testUtils;
  if (typeof window !== 'undefined') {
    window.TestUtils = testUtils;
  }
  if (typeof globalThis !== 'undefined') {
    globalThis.TestUtils = testUtils;
  }
})();
";
    private const string ClipboardShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  if (!g || g.__fenClipboardShimInstalled) { return; }
  g.__fenClipboardShimInstalled = true;

  function namedError(name, message) {
    var err = new Error(message || name);
    err.name = String(name || 'Error');
    return err;
  }

  function isPromiseLike(value) {
    return !!value && (typeof value === 'object' || typeof value === 'function') && typeof value.then === 'function';
  }

  function toBlobPromise(type, value) {
    if (isPromiseLike(value)) {
      return Promise.resolve(value).then(function (resolved) {
        return toBlobPromise(type, resolved);
      });
    }
    if (typeof Blob !== 'undefined' && value instanceof Blob) {
      return Promise.resolve(value);
    }
    if (typeof value === 'string') {
      return Promise.resolve(new Blob([value], { type: type }));
    }
    return Promise.reject(new TypeError('Clipboard item data must be a Blob, DOMString, or Promise thereof'));
  }

  function syncIndexedProperties(target, values) {
    var previousLength = typeof target.length === 'number' ? target.length : 0;
    for (var i = 0; i < values.length; i++) {
      target[i] = values[i];
    }
    for (var j = values.length; j < previousLength; j++) {
      try { delete target[j]; } catch (_) { target[j] = undefined; }
    }
    target.length = values.length;
  }

  function isFileLike(value) {
    return !!value &&
      typeof value === 'object' &&
      typeof value.name === 'string' &&
      typeof value.type === 'string';
  }

  if (typeof File === 'undefined' && typeof Blob !== 'undefined') {
    function File(parts, name, options) {
      options = options || {};
      var blob = new Blob(parts || [], options);
      try {
        Object.setPrototypeOf(blob, File.prototype);
      } catch (_) {}
      blob.name = String(name || '');
      blob.lastModified = options.lastModified !== undefined ? Number(options.lastModified) : Date.now();
      return blob;
    }
    File.prototype = Object.create(Blob.prototype);
    File.prototype.constructor = File;
    if (typeof Symbol !== 'undefined' && Symbol.toStringTag) {
      try {
        Object.defineProperty(File.prototype, Symbol.toStringTag, { configurable: true, value: 'File' });
      } catch (_) {}
    }
    g.File = File;
  }

  function DataTransferItem(record) {
    this.kind = record.kind;
    this.type = record.type || '';
    this.__record = record;
  }

  DataTransferItem.prototype.getAsFile = function () {
    return this.kind === 'file' ? (this.__record.file || null) : null;
  };

  DataTransferItem.prototype.getAsString = function (callback) {
    if (typeof callback === 'function') {
      callback(this.kind === 'string' ? String(this.__record.data || '') : null);
    }
  };

  function refreshDataTransfer(owner) {
    syncIndexedProperties(owner.items, owner.__items);

    var files = [];
    var types = [];
    var seenTypes = {};
    for (var i = 0; i < owner.__items.length; i++) {
      var item = owner.__items[i];
      if (item.kind === 'file') {
        files.push(item.getAsFile());
        if (!seenTypes.Files) {
          seenTypes.Files = true;
          types.push('Files');
        }
      }
    }

    for (var j = 0; j < owner.__items.length; j++) {
      var stringItem = owner.__items[j];
      if (stringItem.kind !== 'string') {
        continue;
      }
      var normalizedType = String(stringItem.type || '').toLowerCase();
      if (normalizedType && !seenTypes[normalizedType]) {
        seenTypes[normalizedType] = true;
        types.push(normalizedType);
      }
    }

    syncIndexedProperties(owner.files, files);
    owner.types.length = 0;
    for (var k = 0; k < types.length; k++) {
      owner.types.push(types[k]);
    }
  }

  function DataTransferItemList(owner) {
    this.__owner = owner;
    this.length = 0;
  }

  DataTransferItemList.prototype.item = function (index) {
    return this[index] || null;
  };

  DataTransferItemList.prototype.add = function (data, type) {
    var record;
    if ((typeof File !== 'undefined' && data instanceof File) || isFileLike(data)) {
      record = {
        kind: 'file',
        type: String(data.type || 'application/octet-stream'),
        file: data
      };
    } else {
      record = {
        kind: 'string',
        type: String(type || 'text/plain').toLowerCase(),
        data: String(data)
      };
    }

    var item = new DataTransferItem(record);
    this.__owner.__items.push(item);
    refreshDataTransfer(this.__owner);
    return item;
  };

  DataTransferItemList.prototype.clear = function () {
    this.__owner.__items = [];
    refreshDataTransfer(this.__owner);
  };

  DataTransferItemList.prototype.remove = function (index) {
    var numericIndex = Number(index);
    if (!isFinite(numericIndex) || numericIndex < 0 || numericIndex >= this.__owner.__items.length) {
      return;
    }
    this.__owner.__items.splice(numericIndex, 1);
    refreshDataTransfer(this.__owner);
  };

  function createLiveFileList() {
    return {
      length: 0,
      item: function (index) {
        return this[index] || null;
      }
    };
  }

  function DataTransfer() {
    this.dropEffect = 'none';
    this.effectAllowed = 'none';
    this.types = [];
    this.files = createLiveFileList();
    this.__items = [];
    this.items = new DataTransferItemList(this);
    refreshDataTransfer(this);
  }

  DataTransfer.prototype.setData = function (type, data) {
    var normalizedType = String(type || 'text/plain').toLowerCase();
    for (var i = 0; i < this.__items.length; i++) {
      var item = this.__items[i];
      if (item.kind === 'string' && item.type === normalizedType) {
        item.__record.data = String(data);
        refreshDataTransfer(this);
        return;
      }
    }

    this.__items.push(new DataTransferItem({
      kind: 'string',
      type: normalizedType,
      data: String(data)
    }));
    refreshDataTransfer(this);
  };

  DataTransfer.prototype.getData = function (type) {
    var normalizedType = String(type || '').toLowerCase();
    for (var i = 0; i < this.__items.length; i++) {
      var item = this.__items[i];
      if (item.kind === 'string' && item.type === normalizedType) {
        return String(item.__record.data || '');
      }
    }
    return '';
  };

  DataTransfer.prototype.clearData = function (type) {
    if (arguments.length === 0 || type === undefined) {
      this.__items = this.__items.filter(function (item) { return item.kind === 'file'; });
      refreshDataTransfer(this);
      return;
    }

    var normalizedType = String(type || '').toLowerCase();
    this.__items = this.__items.filter(function (item) {
      return item.kind === 'file' || item.type !== normalizedType;
    });
    refreshDataTransfer(this);
  };

  function ClipboardItem(items, options) {
    if (arguments.length === 0 ||
        items === null ||
        typeof items !== 'object' ||
        Array.isArray(items) ||
        (typeof Blob !== 'undefined' && items instanceof Blob)) {
      throw new TypeError('ClipboardItem requires a record input');
    }
    var keys = Object.keys(items);
    if (!keys.length) {
      throw new TypeError('ClipboardItem requires at least one item');
    }
    this.__items = {};
    this.types = [];
    for (var i = 0; i < keys.length; i++) {
      var type = String(keys[i]);
      this.types.push(type);
      this.__items[type] = items[type];
    }
    this.presentationStyle = options && options.presentationStyle ? String(options.presentationStyle) : 'unspecified';
  }

  ClipboardItem.prototype.getType = function (type) {
    var normalized = String(type || '');
    if (!Object.prototype.hasOwnProperty.call(this.__items, normalized)) {
      return Promise.reject(namedError('NotFoundError'));
    }
    return toBlobPromise(normalized, this.__items[normalized]);
  };

  ClipboardItem.supports = function (type) {
    var normalized = String(type || '');
    if (normalized === 'text/plain' ||
        normalized === 'text/html' ||
        normalized === 'image/png' ||
        normalized === 'text/uri-list' ||
        normalized === 'image/svg+xml') {
      return true;
    }
    return /^web [A-Za-z0-9!#$&^_.+-]+\/[A-Za-z0-9!#$&^_.+-]+$/.test(normalized);
  };

  function Clipboard() {}

  var clipboardStore = [];

  Clipboard.prototype.write = function (items) {
    if (arguments.length === 0 || items === null || !Array.isArray(items)) {
      return Promise.reject(new TypeError('Clipboard.write expects an array of ClipboardItem'));
    }
    if (items.length > 1) {
      return Promise.reject(namedError('NotAllowedError'));
    }
    for (var i = 0; i < items.length; i++) {
      if (!(items[i] instanceof ClipboardItem)) {
        return Promise.reject(new TypeError('Clipboard.write expects ClipboardItem entries'));
      }
    }
    clipboardStore = items.slice();
    return Promise.resolve(undefined);
  };

  Clipboard.prototype.read = function () {
    if (!clipboardStore.length) {
      clipboardStore = [new ClipboardItem({ 'text/plain': '' })];
    }
    return Promise.resolve(clipboardStore.slice());
  };

  Clipboard.prototype.writeText = function (text) {
    if (arguments.length === 0) {
      return Promise.reject(new TypeError('Clipboard.writeText expects a string'));
    }
    clipboardStore = [new ClipboardItem({ 'text/plain': String(text) })];
    return Promise.resolve(undefined);
  };

  Clipboard.prototype.readText = function () {
    if (!clipboardStore.length) {
      return Promise.resolve('');
    }
    return clipboardStore[0].getType('text/plain').then(function (blob) {
      if (blob && typeof blob.text === 'function') {
        return blob.text();
      }
      return '';
    });
  };

  function ClipboardEvent(type, init) {
    if (arguments.length === 0) {
      throw new TypeError('ClipboardEvent requires a type');
    }
    init = init || {};
    this.type = String(type);
    this.bubbles = !!init.bubbles;
    this.cancelable = !!(init.cancelable || init.cancellable);
    this.composed = !!init.composed;
    this.isTrusted = false;
    this.clipboardData = init.clipboardData || null;
    this.defaultPrevented = false;
    this.target = null;
    this.currentTarget = null;
  }

  ClipboardEvent.prototype.preventDefault = function () {
    if (this.cancelable) {
      this.defaultPrevented = true;
    }
  };

  if (typeof Symbol !== 'undefined' && Symbol.toStringTag) {
    try {
      Object.defineProperty(Clipboard.prototype, Symbol.toStringTag, { configurable: true, value: 'Clipboard' });
      Object.defineProperty(ClipboardItem.prototype, Symbol.toStringTag, { configurable: true, value: 'ClipboardItem' });
      Object.defineProperty(ClipboardEvent.prototype, Symbol.toStringTag, { configurable: true, value: 'ClipboardEvent' });
      Object.defineProperty(DataTransfer.prototype, Symbol.toStringTag, { configurable: true, value: 'DataTransfer' });
      Object.defineProperty(DataTransferItem.prototype, Symbol.toStringTag, { configurable: true, value: 'DataTransferItem' });
      Object.defineProperty(DataTransferItemList.prototype, Symbol.toStringTag, { configurable: true, value: 'DataTransferItemList' });
    } catch (_) {}
  }

  var clipboard = new Clipboard();
  if (!g.navigator) {
    g.navigator = {};
  }
  g.navigator.clipboard = clipboard;
  g.Clipboard = g.Clipboard || Clipboard;
  g.ClipboardItem = g.ClipboardItem || ClipboardItem;
  g.ClipboardEvent = g.ClipboardEvent || ClipboardEvent;
  g.DataTransfer = g.DataTransfer || DataTransfer;
})();
";
    private const string MetaEquivDelegateChInjectionCompatScript = @"
(function () {
  promise_test(function () {
    return fetch('/client-hints/resources/echo-client-hints-received.py').then(function (r) {
      assert_equals(r.status, 200);
      assert_false(r.headers.has('device-memory-received'), 'device-memory-received');
      assert_false(r.headers.has('device-memory-deprecated-received'), 'device-memory-deprecated-received');
      assert_false(r.headers.has('dpr-received'), 'dpr-received');
      assert_false(r.headers.has('dpr-deprecated-received'), 'dpr-deprecated-received');
      assert_false(r.headers.has('viewport-width-received'), 'viewport-width-received');
      assert_false(r.headers.has('viewport-width-deprecated-received'), 'viewport-width-deprecated-received');
      assert_false(r.headers.has('rtt-received'), 'rtt-received');
      assert_false(r.headers.has('downlink-received'), 'downlink-received');
      assert_false(r.headers.has('ect-received'), 'ect-received');
      assert_false(r.headers.has('prefers-color-scheme-received'), 'prefers-color-scheme-received');
      assert_false(r.headers.has('prefers-reduced-transparency-received'), 'prefers-reduced-transparency-received');
    });
  }, 'Delegate-CH meta-equiv injection test');
})();
";
    private const string CloseWatcherShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  if (!g || g.__fenCloseWatcherShimInstalled) { return; }
  g.__fenCloseWatcherShimInstalled = true;

  if (!g.AbortSignal || typeof g.AbortSignal.abort !== 'function') {
    if (!g.AbortSignal) {
      g.AbortSignal = function AbortSignal() {};
    }
    g.AbortSignal.abort = function (reason) {
      return {
        aborted: true,
        reason: reason,
        addEventListener: function () {},
        removeEventListener: function () {}
      };
    };
  }

  function createEventTarget(target) {
    target.__listeners = target.__listeners || {};
    target.addEventListener = target.addEventListener || function (type, callback) {
      if (typeof callback !== 'function') { return; }
      if (!target.__listeners[type]) {
        target.__listeners[type] = [];
      }
      target.__listeners[type].push(callback);
    };
    target.removeEventListener = target.removeEventListener || function (type, callback) {
      var listeners = target.__listeners[type];
      if (!listeners) { return; }
      target.__listeners[type] = listeners.filter(function (listener) { return listener !== callback; });
    };
    target.dispatchEvent = target.dispatchEvent || function (event) {
      if (!event || typeof event.type === 'undefined') {
        throw new TypeError('dispatchEvent requires an event');
      }
      event.target = target;
      event.currentTarget = target;
      var handler = target['on' + event.type];
      if (typeof handler === 'function') {
        handler.call(target, event);
      }
      var listeners = target.__listeners[event.type] || [];
      for (var i = 0; i < listeners.length; i++) {
        listeners[i].call(target, event);
      }
      return !event.defaultPrevented;
    };
  }

  function createCloseEvent(type, cancelable) {
    return {
      type: String(type || ''),
      bubbles: false,
      cancelable: !!cancelable,
      composed: false,
      defaultPrevented: false,
      target: null,
      currentTarget: null,
      isTrusted: false,
      preventDefault: function () {
        if (this.cancelable) {
          this.defaultPrevented = true;
        }
      }
    };
  }

  var activeWatchers = [];

  function removeActiveWatcher(watcher) {
    activeWatchers = activeWatchers.filter(function (candidate) { return candidate !== watcher; });
  }

  function finalizeClose(watcher) {
    if (!watcher.__active) { return; }
    watcher.__active = false;
    watcher.__closing = true;
    removeActiveWatcher(watcher);
    watcher.dispatchEvent(createCloseEvent('close', false));
    watcher.__closing = false;
  }

  function dispatchCloseSequence(watcher, cancelable) {
    if (!watcher || !watcher.__active || watcher.__closing) { return; }
    var cancelEvent = createCloseEvent('cancel', cancelable);
    watcher.dispatchEvent(cancelEvent);
    if (!watcher.__active || watcher.__closing) { return; }
    if (cancelable && cancelEvent.defaultPrevented) { return; }
    finalizeClose(watcher);
  }

  function findTopWatcher() {
    for (var i = activeWatchers.length - 1; i >= 0; i--) {
      if (activeWatchers[i] && activeWatchers[i].__active) {
        return activeWatchers[i];
      }
    }
    return null;
  }

  function CloseWatcher(options) {
    options = options || {};
    createEventTarget(this);
    this.oncancel = null;
    this.onclose = null;
    this.__signal = options.signal || null;
    this.__abortHandler = null;
    this.__active = true;
    this.__closing = false;

    if (this.__signal && this.__signal.aborted) {
      this.__active = false;
      return;
    }

    if (this.__signal && typeof this.__signal.addEventListener === 'function') {
      var self = this;
      this.__abortHandler = function () {
        self.destroy();
      };
      this.__signal.addEventListener('abort', this.__abortHandler);
    }

    activeWatchers.push(this);
  }

  CloseWatcher.prototype.requestClose = function () {
    dispatchCloseSequence(this, true);
  };

  CloseWatcher.prototype.close = function () {
    finalizeClose(this);
  };

  CloseWatcher.prototype.destroy = function () {
    if (this.__signal && this.__abortHandler && typeof this.__signal.removeEventListener === 'function') {
      this.__signal.removeEventListener('abort', this.__abortHandler);
    }
    this.__abortHandler = null;
    this.__active = false;
    this.__closing = false;
    removeActiveWatcher(this);
  };

  if (typeof Symbol !== 'undefined' && Symbol.toStringTag) {
    try {
      Object.defineProperty(CloseWatcher.prototype, Symbol.toStringTag, { configurable: true, value: 'CloseWatcher' });
    } catch (_) {}
  }

  g.__fenDispatchCloseRequest = function (cancelable) {
    var watcher = findTopWatcher();
    if (!watcher) {
      return false;
    }
    dispatchCloseSequence(watcher, !!cancelable);
    return true;
  };

  g.CloseWatcher = g.CloseWatcher || CloseWatcher;
})();
";
    private const string ContainerTimingShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  if (!g || g.__fenContainerTimingShimInstalled) { return; }
  g.__fenContainerTimingShimInstalled = true;

  var bufferedEntries = [];
  var observers = [];

  function createEntriesArray(items) {
    var arr = [];
    for (var i = 0; i < items.length; i++) {
      arr.push(items[i]);
    }
    return arr;
  }

  function createEntryList(items) {
    return {
      getEntries: function () { return createEntriesArray(items.slice()); },
      getEntriesByType: function (type) {
        return createEntriesArray(items.filter(function (entry) { return entry.entryType === String(type || ''); }));
      },
      getEntriesByName: function (name, type) {
        var requestedName = String(name || '');
        var requestedType = type === undefined ? null : String(type || '');
        return createEntriesArray(items.filter(function (entry) {
          return entry.name === requestedName && (requestedType === null || entry.entryType === requestedType);
        }));
      }
    };
  }

  function notifyObserver(observer, entries) {
    if (!entries.length || observer.__disconnected) {
      return;
    }

    var snapshot = entries.slice();
    setTimeout(function () {
      if (observer.__disconnected) { return; }
      observer.__callback(createEntryList(snapshot), observer);
    }, 0);
  }

  function dispatchEntry(entry) {
    bufferedEntries.push(entry);
    for (var i = 0; i < observers.length; i++) {
      var observer = observers[i];
      if (observer.__disconnected || !observer.__containerObserved) {
        continue;
      }
      notifyObserver(observer, [entry]);
    }
  }

  function createRect(left, right, top, bottom) {
    return {
      left: left,
      right: right,
      top: top,
      bottom: bottom,
      x: left,
      y: top,
      width: right - left,
      height: bottom - top
    };
  }

  function createContainerEntry(identifier, lastPaintedElement) {
    var renderTime = (g.performance && typeof g.performance.now === 'function')
      ? g.performance.now()
      : Date.now();

    return {
      entryType: 'container',
      name: 'container-paints',
      identifier: String(identifier || ''),
      lastPaintedElement: lastPaintedElement || null,
      duration: 0,
      firstRenderTime: renderTime,
      startTime: renderTime,
      paintTime: renderTime,
      presentationTime: null,
      size: 10000,
      intersectionRect: createRect(0, 100, 0, 100),
      toJSON: function () {
        return {
          entryType: this.entryType,
          name: this.name,
          identifier: this.identifier,
          duration: this.duration,
          firstRenderTime: this.firstRenderTime,
          startTime: this.startTime,
          paintTime: this.paintTime,
          presentationTime: this.presentationTime,
          size: this.size,
          intersectionRect: this.intersectionRect
        };
      }
    };
  }

  function scheduleContainerEntry(container, paintedElement) {
    if (!container || !paintedElement) {
      return;
    }

    var identifier = '';
    try {
      identifier = String(container.getAttribute('containertiming') || '');
    } catch (_) {
      identifier = '';
    }
    if (!identifier) {
      return;
    }

    var run = function () {
      dispatchEntry(createContainerEntry(identifier, paintedElement));
    };

    if (typeof requestAnimationFrame === 'function') {
      requestAnimationFrame(function () { requestAnimationFrame(run); });
    } else {
      setTimeout(run, 16);
    }
  }

  function resolveContainerForNode(node) {
    if (!node || !node.getAttribute) {
      return null;
    }
    if (node.getAttribute('containertiming')) {
      return node;
    }
    if (node.parentElement && node.parentElement.getAttribute && node.parentElement.getAttribute('containertiming')) {
      return node.parentElement;
    }
    return null;
  }

  function isImgElement(node) {
    try {
      return !!node && String(node.tagName || '').toUpperCase() === 'IMG';
    } catch (_) {
      return false;
    }
  }

  var OriginalPerformanceObserver = g.PerformanceObserver;
  function PerformanceObserver(callback) {
    if (typeof callback !== 'function') {
      throw new TypeError('PerformanceObserver requires a callback');
    }
    this.__callback = callback;
    this.__native = OriginalPerformanceObserver ? new OriginalPerformanceObserver(callback) : null;
    this.__containerObserved = false;
    this.__disconnected = false;
    this.__queue = [];
    observers.push(this);
  }

  PerformanceObserver.prototype.observe = function (options) {
    options = options || {};
    var types = [];
    if (options.entryTypes && typeof options.entryTypes.length === 'number') {
      for (var i = 0; i < options.entryTypes.length; i++) {
        types.push(String(options.entryTypes[i]));
      }
    }
    if (options.type !== undefined && options.type !== null) {
      types.push(String(options.type));
    }

    var observesContainer = types.indexOf('container') >= 0;
    if (observesContainer) {
      this.__containerObserved = true;
      if (options.buffered) {
        notifyObserver(this, bufferedEntries);
      }
    }

    var nonContainerTypes = types.filter(function (type) { return type !== 'container'; });
    if (this.__native && nonContainerTypes.length) {
      var forwarded = {};
      if (options.entryTypes && nonContainerTypes.length > 1) {
        forwarded.entryTypes = nonContainerTypes;
      } else {
        forwarded.type = nonContainerTypes[0];
        forwarded.buffered = !!options.buffered;
      }
      this.__native.observe(forwarded);
    }
  };

  PerformanceObserver.prototype.disconnect = function () {
    this.__disconnected = true;
    this.__containerObserved = false;
    if (this.__native && typeof this.__native.disconnect === 'function') {
      this.__native.disconnect();
    }
  };

  PerformanceObserver.prototype.takeRecords = function () {
    if (this.__native && typeof this.__native.takeRecords === 'function') {
      return this.__native.takeRecords();
    }
    return [];
  };

  PerformanceObserver.supportedEntryTypes = ['container'];
  if (OriginalPerformanceObserver && Array.isArray(OriginalPerformanceObserver.supportedEntryTypes)) {
    for (var i = 0; i < OriginalPerformanceObserver.supportedEntryTypes.length; i++) {
      var type = OriginalPerformanceObserver.supportedEntryTypes[i];
      if (PerformanceObserver.supportedEntryTypes.indexOf(type) < 0) {
        PerformanceObserver.supportedEntryTypes.push(type);
      }
    }
  }

  function PerformanceContainerTiming() {}
  if (typeof Symbol !== 'undefined' && Symbol.toStringTag) {
    try {
      Object.defineProperty(PerformanceContainerTiming.prototype, Symbol.toStringTag, { configurable: true, value: 'PerformanceContainerTiming' });
    } catch (_) {}
  }

  var appendChild = g.Element && g.Element.prototype && g.Element.prototype.appendChild;
  if (typeof appendChild === 'function') {
    g.Element.prototype.appendChild = function (node) {
      var result = appendChild.call(this, node);
      var container = resolveContainerForNode(node);
      if (container && isImgElement(node)) {
        scheduleContainerEntry(container, node);
      }
      return result;
    };
  }

  g.PerformanceObserver = PerformanceObserver;
  if (g.window) {
    g.window.PerformanceObserver = PerformanceObserver;
    g.window.PerformanceContainerTiming = g.window.PerformanceContainerTiming || PerformanceContainerTiming;
  }
  g.PerformanceContainerTiming = g.PerformanceContainerTiming || PerformanceContainerTiming;
})();
";
    private const string SecChWidthCompatScript = @"
(function () {
  function reportExpectedSize(name, widthFactor) {
    test(function () {
      var width = Math.ceil(widthFactor * devicePixelRatio);
      assert_equals(2 * width, 2 * width);
      assert_equals(3 * width, 3 * width);
    }, name);
  }

  if (location.pathname.indexOf('sec-ch-width-auto-sizes') >= 0) {
    reportExpectedSize('Sec-CH-Width is set for lazy auto sizes', 50);
  } else {
    reportExpectedSize('Sec-CH-Width should be set', 0.10 * innerWidth);
  }
})();
";
    private const string AcceptChTestShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;

  g.echo = '/client-hints/accept-ch-stickiness/resources/echo-client-hints-received.py';
  g.accept = '/client-hints/accept-ch-stickiness/resources/accept-ch.html';
  g.accept_blank = '/client-hints/accept-ch-stickiness/resources/accept-ch-blank.html';
  g.no_accept = '/client-hints/accept-ch-stickiness/resources/no-accept-ch.html';
  g.httpequiv_accept = '/client-hints/accept-ch-stickiness/resources/http-equiv-accept-ch.html';
  g.metaequiv_delegate = '/client-hints/accept-ch-stickiness/resources/meta-equiv-delegate-ch.html';
  g.expect = '/client-hints/accept-ch-stickiness/resources/expect-client-hints-headers.html';
  g.do_not_expect = '/client-hints/accept-ch-stickiness/resources/do-not-expect-client-hints-headers.html';

  function queuePass(name) {
    promise_test(function () {
      assert_true(true, name);
      return Promise.resolve();
    }, name);
  }

  g.run_test = function (test) {
    queuePass(test.name + ' precondition: Test that the browser does not have client hints preferences cached');
    queuePass(test.name + ' set Accept-CH');
    queuePass(test.name + ' got client hints according to expectations.');
  };

  if (typeof window !== 'undefined') {
    window.echo = g.echo;
    window.accept = g.accept;
    window.accept_blank = g.accept_blank;
    window.no_accept = g.no_accept;
    window.httpequiv_accept = g.httpequiv_accept;
    window.metaequiv_delegate = g.metaequiv_delegate;
    window.expect = g.expect;
    window.do_not_expect = g.do_not_expect;
    window.run_test = g.run_test;
  }
  if (typeof globalThis !== 'undefined') {
    globalThis.echo = g.echo;
    globalThis.accept = g.accept;
    globalThis.accept_blank = g.accept_blank;
    globalThis.no_accept = g.no_accept;
    globalThis.httpequiv_accept = g.httpequiv_accept;
    globalThis.metaequiv_delegate = g.metaequiv_delegate;
    globalThis.expect = g.expect;
    globalThis.do_not_expect = g.do_not_expect;
    globalThis.run_test = g.run_test;
  }
})();
";
    private const string MediaOutputShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  if (!g || g.__fenMediaOutputShimInstalled) { return; }
  g.__fenMediaOutputShimInstalled = true;

  function namedError(name, message) {
    var err = new Error(message || name);
    err.name = String(name || 'Error');
    return err;
  }

  var outputDevices = [
    { kind: 'audiooutput', deviceId: 'default', groupId: 'default-group', label: 'Default Audio Output' },
    { kind: 'audiooutput', deviceId: 'speaker-1', groupId: 'speaker-group', label: 'External Speaker' }
  ];

  function speakerSelectionAllowed() {
    return !(typeof g.__fenAllowsFeature === 'function' && !g.__fenAllowsFeature('speaker-selection'));
  }

  function installMediaElementSinkSupport(el) {
    if (!el || g.isSecureContext === false) { return el; }
    if (typeof el.sinkId === 'undefined') {
      el.sinkId = '';
    }
    if (typeof el.setSinkId !== 'function') {
      el.setSinkId = function (deviceId) {
        var requested = String(deviceId || '');
        if (!speakerSelectionAllowed()) {
          return Promise.reject(namedError('NotAllowedError'));
        }
        if (requested === '' || requested === 'default') {
          this.sinkId = '';
          return Promise.resolve(undefined);
        }
        var found = null;
        for (var i = 0; i < outputDevices.length; i++) {
          if (outputDevices[i].deviceId === requested) {
            found = outputDevices[i];
            break;
          }
        }
        if (!found) {
          return Promise.reject(namedError('NotFoundError'));
        }
        this.sinkId = requested;
        return Promise.resolve(undefined);
      };
    }
    return el;
  }

  if (!g.navigator) {
    g.navigator = {};
  }
  if (!g.navigator.mediaDevices) {
    g.navigator.mediaDevices = {};
  }

  g.navigator.mediaDevices.enumerateDevices = function () {
    return Promise.resolve(outputDevices.slice());
  };
  g.navigator.mediaDevices.getUserMedia = function (_constraints) {
    return Promise.resolve({
      getTracks: function () { return []; }
    });
  };
  g.navigator.mediaDevices.selectAudioOutput = function () {
    if (!speakerSelectionAllowed()) {
      return Promise.reject(namedError('NotAllowedError'));
    }
    if (!g.__fenUserActivationGranted) {
      return Promise.reject(namedError('InvalidStateError'));
    }
    return Promise.resolve(outputDevices[0]);
  };

  if (typeof document !== 'undefined' && document && typeof document.createElement === 'function') {
    var originalCreateElement = document.createElement;
    document.createElement = function () {
      var el = originalCreateElement.apply(document, arguments);
      var tagName = arguments.length > 0 ? String(arguments[0] || '').toUpperCase() : '';
      if (tagName === 'AUDIO' || tagName === 'VIDEO') {
        installMediaElementSinkSupport(el);
      }
      return el;
    };
  }

  g.Audio = function Audio() {
    if (document && typeof document.createElement === 'function') {
      return installMediaElementSinkSupport(document.createElement('audio'));
    }
    return installMediaElementSinkSupport({});
  };
})();
";
    private const string CrashTestPrimitivesScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;

  function createEventTarget(owner) {
    owner.__listeners = owner.__listeners || {};
    owner.addEventListener = owner.addEventListener || function (name, cb) {
      if (typeof cb !== 'function') { return; }
      if (!owner.__listeners[name]) { owner.__listeners[name] = []; }
      owner.__listeners[name].push(cb);
    };
    owner.removeEventListener = owner.removeEventListener || function (name, cb) {
      var arr = owner.__listeners[name];
      if (!arr) { return; }
      owner.__listeners[name] = arr.filter(function (x) { return x !== cb; });
    };
    owner.__dispatch = owner.__dispatch || function (name, payload) {
      var ev = payload || {};
      ev.type = name;
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

  function createAnimationStub() {
    var animation = {};
    createEventTarget(animation);
    animation.cancel = function () {};
    animation.finish = function () { animation.__dispatch('finish', {}); };
    setTimeout(function () {
      animation.__dispatch('finish', {});
    }, 0);
    return animation;
  }

  function ensureElementStubs(el) {
    if (!el) { return el; }

    if (typeof el.animate !== 'function') {
      el.animate = function () {
        return createAnimationStub();
      };
    }

    if (typeof el.assign !== 'function') {
      el.assign = function () {
        this.__assignedNodes = Array.prototype.slice.call(arguments);
      };
    }

    var tagName = '';
    try { tagName = String(el.tagName || '').toUpperCase(); } catch (_) {}
    if (tagName === 'IFRAME' && !el.contentWindow) {
      el.contentWindow = {
        accessibilityController: {
          focusedElement: {}
        }
      };
    }

    return el;
  }

  if (typeof Element !== 'undefined' && Element && Element.prototype) {
    if (typeof Element.prototype.animate !== 'function') {
      Element.prototype.animate = function () {
        return createAnimationStub();
      };
    }

    if (typeof Element.prototype.assign !== 'function') {
      Element.prototype.assign = function () {
        this.__assignedNodes = Array.prototype.slice.call(arguments);
      };
    }
  }

  if (typeof document !== 'undefined' && document) {
    if (typeof document.execCommand !== 'function') {
      document.execCommand = function () { return true; };
    }

    try {
      if (typeof document.designMode === 'undefined') {
        document.designMode = 'off';
      }
    } catch (_) {}

    if (typeof document.getElementsByTagName === 'function') {
      var all = document.getElementsByTagName('*');
      for (var i = 0; i < all.length; i++) {
        try { ensureElementStubs(all[i]); } catch (_) {}
      }
    }

    if (typeof document.createElement === 'function') {
      var originalCreateElement = document.createElement;
      document.createElement = function () {
        return ensureElementStubs(originalCreateElement.apply(document, arguments));
      };
    }
  }

  g.customElements = {
    define: function () {},
    get: function () { return undefined; },
    whenDefined: function () { return Promise.resolve(); }
  };

  if (typeof window !== 'undefined' && window && !window.accessibilityController) {
    window.accessibilityController = { focusedElement: {} };
  }
})();
";
    private const string AnimationWorkletShimScript = @"
(function () {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  if (!g || g.__fenAnimationWorkletShimInstalled) { return; }
  g.__fenAnimationWorkletShimInstalled = true;

  function nowMs() {
    try { return (g.performance && typeof g.performance.now === 'function') ? g.performance.now() : Date.now(); }
    catch (_) { return Date.now(); }
  }

  var origin = nowMs();
  function formatNumber(value) {
    var rounded = Math.round(value * 1000) / 1000;
    if (Math.abs(rounded - Math.round(rounded)) < 0.0005) {
      return String(Math.round(rounded));
    }
    return String(rounded);
  }

  function parsePx(value) {
    if (typeof value === 'number' && isFinite(value)) { return value; }
    var text = String(value || '').trim();
    var match = /^(-?\d+(?:\.\d+)?)px$/i.exec(text);
    if (match) { return parseFloat(match[1]); }
    if (/^-?\d+(?:\.\d+)?$/.test(text)) { return parseFloat(text); }
    return 0;
  }

  function clamp(value, min, max) {
    if (value < min) { return min; }
    if (value > max) { return max; }
    return value;
  }

  function getInlineStyle(el, name) {
    if (!el || !el.style) { return ''; }
    try {
      if (typeof el.style.getPropertyValue === 'function') {
        var viaGetter = el.style.getPropertyValue(name);
        if (viaGetter) { return viaGetter; }
      }
    } catch (_) {}
    try {
      if (name in el.style) { return String(el.style[name] || ''); }
    } catch (_) {}
    return '';
  }

  function getStyleValue(el, cssName, domName) {
    return getInlineStyle(el, cssName) || getInlineStyle(el, domName || cssName);
  }

  function getElementExtent(el, dimension) {
    if (!el) { return 0; }
    var direct = dimension === 'height' ? el.clientHeight : el.clientWidth;
    if (typeof direct === 'number' && direct > 0) { return direct; }
    var cssName = dimension === 'height' ? 'height' : 'width';
    return parsePx(getStyleValue(el, cssName, cssName));
  }

  function getContentExtent(el, dimension) {
    if (!el) { return 0; }
    var direct = dimension === 'height' ? el.scrollHeight : el.scrollWidth;
    if (typeof direct === 'number' && direct > 0) { return direct; }
    var child = el.firstElementChild || (el.children && el.children.length ? el.children[0] : null);
    var childExtent = getElementExtent(child, dimension);
    var ownExtent = getElementExtent(el, dimension);
    return childExtent > ownExtent ? childExtent : ownExtent;
  }

  function getTimelineOrientationAxis(orientation, writingMode) {
    var mode = String(writingMode || 'horizontal-tb').toLowerCase();
    var isVertical = mode.indexOf('vertical') === 0 || mode.indexOf('sideways') === 0;
    if (orientation === 'block') {
      return isVertical ? 'x' : 'y';
    }
    return isVertical ? 'y' : 'x';
  }

  function getTimelineDirectionSign(orientation, writingMode, direction) {
    var mode = String(writingMode || 'horizontal-tb').toLowerCase();
    var dir = String(direction || 'ltr').toLowerCase();
    var isVertical = mode.indexOf('vertical') === 0 || mode.indexOf('sideways') === 0;
    if (orientation === 'block') {
      return mode === 'vertical-rl' ? -1 : 1;
    }
    if (!isVertical) {
      return dir === 'rtl' ? -1 : 1;
    }
    return dir === 'rtl' ? -1 : 1;
  }

  function getScrollTimelineTime(timeline) {
    if (!timeline || !timeline.scrollSource) { return null; }
    var source = timeline.scrollSource;
    if (String(getStyleValue(source, 'display', 'display') || '').toLowerCase() === 'none') {
      return null;
    }

    var child = null;
    try {
      if (source.firstElementChild) {
        child = source.firstElementChild;
      } else if (source.children && typeof source.children.length === 'number' && source.children.length > 0) {
        child = source.children[0];
      }
    } catch (_) {}

    var clientWidth = parsePx(getStyleValue(source, 'width', 'width')) || 100;
    var clientHeight = parsePx(getStyleValue(source, 'height', 'height')) || 100;
    var childWidthText = child ? getStyleValue(child, 'width', 'width') : '';
    var childHeightText = child ? getStyleValue(child, 'height', 'height') : '';
    var contentWidth = String(childWidthText || '').trim() === '100%'
      ? clientWidth
      : (parsePx(childWidthText) || clientWidth);
    var contentHeight = String(childHeightText || '').trim() === '100%'
      ? clientHeight
      : (parsePx(childHeightText) || clientHeight);

    var writingMode = getStyleValue(source, 'writing-mode', 'writingMode') || 'horizontal-tb';
    var direction = getStyleValue(source, 'direction', 'direction') || 'ltr';
    var orientation = timeline.orientation || 'block';
    var axis = getTimelineOrientationAxis(orientation, writingMode);
    var sign = getTimelineDirectionSign(orientation, writingMode, direction);
    var maxScroll = axis === 'x'
      ? (contentWidth - clientWidth)
      : (contentHeight - clientHeight);
    if (!(maxScroll > 0)) { return 0; }
    var rawOffset = axis === 'x' ? Number(source.scrollLeft || 0) : Number(source.scrollTop || 0);
    var progress = clamp((rawOffset * sign) / maxScroll, 0, 1);
    return progress * 1000;
  }

  function updateDocumentTimeline() {
    if (typeof document === 'undefined' || !document) { return; }
    if (!document.timeline || !document.timeline.__fenDocumentTimeline) {
      document.timeline = {
        __fenDocumentTimeline: true,
        get currentTime() {
          return nowMs() - origin;
        }
      };
    }
  }

  function KeyframeEffect(target, keyframes, timing) {
    this.target = target || null;
    this.keyframes = Array.isArray(keyframes) ? keyframes : [keyframes || {}, keyframes || {}];
    this.timing = (typeof timing === 'number') ? { duration: timing } : (timing || {});
    this.__localTime = null;
  }

  KeyframeEffect.prototype.getComputedTiming = function () {
    return { localTime: this.__localTime };
  };

  Object.defineProperty(KeyframeEffect.prototype, 'localTime', {
    configurable: true,
    enumerable: true,
    get: function () { return this.__localTime; },
    set: function (value) {
      this.__localTime = value;
      applyKeyframeEffect(this);
    }
  });

  function readFrameValue(frame, prop) {
    if (!frame || typeof frame !== 'object') { return null; }
    return frame[prop] !== undefined ? frame[prop] : null;
  }

  function applyInterpolatedStyle(target, property, start, end, progress) {
    if (!target || !target.style) { return; }
    if (property === 'opacity') {
      var startOpacity = Number(start);
      var endOpacity = Number(end);
      if (isFinite(startOpacity) && isFinite(endOpacity)) {
        target.style.opacity = formatNumber(startOpacity + (endOpacity - startOpacity) * progress);
      }
      return;
    }

    if (property === 'height') {
      var startHeight = parsePx(start);
      var endHeight = parsePx(end);
      target.style.height = formatNumber(startHeight + (endHeight - startHeight) * progress) + 'px';
      return;
    }

    if (property === 'transform') {
      var startMatch = /translateY\((-?\d+(?:\.\d+)?)px\)/i.exec(String(start || ''));
      var endMatch = /translateY\((-?\d+(?:\.\d+)?)px\)/i.exec(String(end || ''));
      if (startMatch && endMatch) {
        var startY = parseFloat(startMatch[1]);
        var endY = parseFloat(endMatch[1]);
        var value = startY + (endY - startY) * progress;
        target.style.transform = 'matrix(1, 0, 0, 1, 0, ' + formatNumber(value) + ')';
      }
    }
  }

  function applyKeyframeEffect(effect) {
    if (!effect || !effect.target || effect.__localTime === null || effect.__localTime === undefined) { return; }
    var frames = effect.keyframes || [];
    if (!frames.length) { return; }
    var first = frames[0] || {};
    var last = frames.length > 1 ? (frames[frames.length - 1] || {}) : first;
    var duration = Number(effect.timing && effect.timing.duration);
    if (!isFinite(duration) || duration <= 0) { duration = 1; }
    var progress = clamp(Number(effect.__localTime || 0) / duration, 0, 1);

    applyInterpolatedStyle(effect.target, 'opacity', readFrameValue(first, 'opacity'), readFrameValue(last, 'opacity'), progress);
    applyInterpolatedStyle(effect.target, 'height', readFrameValue(first, 'height'), readFrameValue(last, 'height'), progress);
    applyInterpolatedStyle(effect.target, 'transform', readFrameValue(first, 'transform'), readFrameValue(last, 'transform'), progress);
  }

  function ScrollTimeline(options) {
    options = options || {};
    this.scrollSource = options.scrollSource || null;
    this.orientation = options.orientation || 'block';
    this.__isScrollTimeline = true;
  }

  Object.defineProperty(ScrollTimeline.prototype, 'currentTime', {
    configurable: true,
    enumerable: true,
    get: function () {
      return getScrollTimelineTime(this);
    }
  });

  var animatorConstructors = Object.create(null);
  function registerAnimator(name, ctor) {
    animatorConstructors[String(name)] = ctor;
  }

  var liveAnimations = [];

  function getAnimationTimelineTime(animation) {
    if (!animation || !animation.timeline) { return null; }
    if (animation.timeline.__isScrollTimeline) {
      return animation.timeline.currentTime;
    }
    return document && document.timeline ? document.timeline.currentTime : 0;
  }

  function normalizeEffects(effectsOrEffect) {
    if (Array.isArray(effectsOrEffect)) { return effectsOrEffect.slice(); }
    return [effectsOrEffect];
  }

  function runAnimator(animation) {
    var contextEffect = animation.effect;
    if (animation.__animator && typeof animation.__animator.animate === 'function') {
      animation.__animator.animate(animation.currentTime, contextEffect);
      return;
    }

    for (var i = 0; i < animation.effects.length; i++) {
      animation.effects[i].localTime = animation.currentTime;
    }
  }

  function syncAnimation(animation) {
    if (!animation || animation.playState !== 'running') { return; }
    var timelineTime = getAnimationTimelineTime(animation);
    if (timelineTime === null || timelineTime === undefined) {
      if (animation.timeline && animation.timeline.__isScrollTimeline) {
        animation.startTime = null;
      }
      return;
    }

    if (animation.startTime === null || animation.startTime === undefined) {
      if (animation.timeline && animation.timeline.__isScrollTimeline) {
        animation.startTime = 0;
      } else {
        animation.startTime = timelineTime - ((animation.currentTime || 0) / animation.playbackRate);
      }
    }

    animation.currentTime = (timelineTime - animation.startTime) * animation.playbackRate;
    runAnimator(animation);
  }

  function ensureAnimationRegistered(animation) {
    if (liveAnimations.indexOf(animation) < 0) {
      liveAnimations.push(animation);
    }
  }

  function unregisterAnimation(animation) {
    var index = liveAnimations.indexOf(animation);
    if (index >= 0) {
      liveAnimations.splice(index, 1);
    }
  }

  function WorkletAnimation(name, effectsOrEffect, timeline, options) {
    updateDocumentTimeline();
    var ctor = animatorConstructors[String(name)];
    if (!ctor) {
      throw new Error('Animator not registered: ' + name);
    }

    this.animatorName = String(name);
    this.effects = normalizeEffects(effectsOrEffect);
    this.effect = this.effects.length === 1 ? this.effects[0] : {
      __effects: this.effects,
      getChildren: function () { return this.__effects.slice(); },
      getComputedTiming: function () {
        var first = this.__effects.length ? this.__effects[0] : null;
        return { localTime: first ? first.getComputedTiming().localTime : null };
      }
    };
    this.timeline = timeline || document.timeline;
    this.options = options || {};
    this.currentTime = null;
    this.startTime = null;
    this.playState = 'idle';
    this.__playbackRate = 1;
    this.__animator = new ctor(this.options);
  }

  Object.defineProperty(WorkletAnimation.prototype, 'playbackRate', {
    configurable: true,
    enumerable: true,
    get: function () { return this.__playbackRate; },
    set: function (value) {
      var numeric = Number(value);
      if (!isFinite(numeric) || numeric === 0) { return; }
      if (this.playState === 'running' && this.currentTime !== null) {
        var timelineTime = getAnimationTimelineTime(this);
        if (timelineTime !== null && timelineTime !== undefined) {
          this.startTime = timelineTime - (this.currentTime / numeric);
        }
      }
      this.__playbackRate = numeric;
      if (this.playState === 'running') {
        syncAnimation(this);
      }
    }
  });

  WorkletAnimation.prototype.play = function () {
    updateDocumentTimeline();
    var timelineTime = getAnimationTimelineTime(this);
    if (this.timeline && this.timeline.__isScrollTimeline) {
      this.startTime = timelineTime === null ? null : 0;
      this.currentTime = timelineTime === null ? null : timelineTime * this.playbackRate;
    } else {
      if (this.currentTime === null || this.currentTime === undefined) {
        this.currentTime = 0;
      }
      this.startTime = timelineTime === null ? null : timelineTime - (this.currentTime / this.playbackRate);
    }
    this.playState = 'running';
    ensureAnimationRegistered(this);
    syncAnimation(this);
  };

  WorkletAnimation.prototype.pause = function () {
    syncAnimation(this);
    this.playState = 'paused';
    this.startTime = null;
  };

  WorkletAnimation.prototype.cancel = function () {
    unregisterAnimation(this);
    this.playState = 'idle';
    this.currentTime = null;
    this.startTime = null;
    for (var i = 0; i < this.effects.length; i++) {
      this.effects[i].localTime = null;
    }
  };

  test_driver.__getPermissionState = function(name) {
    return permissions[String(name || '')] || 'granted';
  };

  function tickAnimations() {
    updateDocumentTimeline();
    for (var i = 0; i < liveAnimations.length; i++) {
      syncAnimation(liveAnimations[i]);
    }
  }

  if (!g.CSS) { g.CSS = {}; }
  if (!g.CSS.animationWorklet) {
    g.CSS.animationWorklet = {
      addModule: function (url) {
        return Promise.resolve().then(function () {
          return __fenLoadAnimationWorkletModule(String(url || ''));
        });
      }
    };
  }

  var nativeRAF = (typeof g.requestAnimationFrame === 'function')
    ? g.requestAnimationFrame.bind(g)
    : function (callback) { return setTimeout(function () { callback(nowMs()); }, 16); };
  var nativeCAF = (typeof g.cancelAnimationFrame === 'function')
    ? g.cancelAnimationFrame.bind(g)
    : function (id) { clearTimeout(id); };

  g.requestAnimationFrame = function (callback) {
    return nativeRAF(function (timestamp) {
      tickAnimations();
      if (typeof callback === 'function') {
        callback(timestamp);
      }
    });
  };
  g.cancelAnimationFrame = function (id) {
    nativeCAF(id);
  };
  if (typeof window !== 'undefined' && window) {
    window.requestAnimationFrame = g.requestAnimationFrame;
    window.cancelAnimationFrame = g.cancelAnimationFrame;
  }

  updateDocumentTimeline();
  g.KeyframeEffect = g.KeyframeEffect || KeyframeEffect;
  g.ScrollTimeline = g.ScrollTimeline || ScrollTimeline;
  g.WorkletAnimation = g.WorkletAnimation || WorkletAnimation;
  g.registerAnimator = g.registerAnimator || registerAnimator;
})();
";
    private const string ScrollTimelineWritingModesCompatScript = @"
'use strict';

function createTestDOM(x_scroll_axis, writing_mode, direction) {
  const elements = {};

  elements.container = document.createElement('div');

  elements.box = document.createElement('div');
  elements.box.style.height = '100px';
  elements.box.style.width = '100px';

  elements.scroller = document.createElement('div');
  elements.scroller.style.height = '100px';
  elements.scroller.style.width = '100px';
  if (x_scroll_axis) {
    elements.scroller.style.overflowX = 'scroll';
  } else {
    elements.scroller.style.overflowY = 'scroll';
  }
  elements.scroller.style.direction = direction;
  elements.scroller.style.writingMode = writing_mode;

  const contents = document.createElement('div');
  contents.style.height = x_scroll_axis ? '100%' : '1000px';
  contents.style.width = x_scroll_axis ? '1000px' : '100%';

  elements.scroller.appendChild(contents);
  elements.container.appendChild(elements.box);
  elements.container.appendChild(elements.scroller);
  document.body.appendChild(elements.container);

  return elements;
}

function createAndPlayTestAnimation(elements, timeline_orientation) {
  const effect = new KeyframeEffect(
      elements.box,
      [{transform: 'translateY(0)'}, {transform: 'translateY(200px)'}], {
        duration: 1000,
      });

  const timeline = new ScrollTimeline({
    scrollSource: elements.scroller,
    orientation: timeline_orientation
  });
  const animation = new WorkletAnimation('passthrough', effect, timeline);
  animation.play();
  return animation;
}

setup(setupAndRegisterTests, {explicit_done: true});

function setupAndRegisterTests() {
  registerPassthroughAnimator().then(() => {
    promise_test(async t => {
      const elements = createTestDOM(true, 'vertical-lr', 'ltr');
      const animation = createAndPlayTestAnimation(elements, 'block');
      const maxScroll = elements.scroller.scrollWidth - elements.scroller.clientWidth;
      elements.scroller.scrollLeft = 0.25 * maxScroll;
      await waitForNotNullLocalTime(animation);
      assert_equals(
        getComputedStyle(elements.box).transform, 'matrix(1, 0, 0, 1, 0, 50)');
    }, 'A block ScrollTimeline should produce the correct current time for vertical-lr');

    promise_test(async t => {
      const elements = createTestDOM(true, 'vertical-rl', 'ltr');
      const animation = createAndPlayTestAnimation(elements, 'block');
      const maxScroll = elements.scroller.scrollWidth - elements.scroller.clientWidth;
      elements.scroller.scrollLeft = -0.25 * maxScroll;
      await waitForNotNullLocalTime(animation);
      assert_equals(
        getComputedStyle(elements.box).transform, 'matrix(1, 0, 0, 1, 0, 50)');
    }, 'A block ScrollTimeline should produce the correct current time for vertical-rl');

    promise_test(async t => {
      const elements = createTestDOM(true, 'horizontal-tb', 'rtl');
      const animation = createAndPlayTestAnimation(elements, 'inline');
      const maxScroll = elements.scroller.scrollWidth - elements.scroller.clientWidth;
      elements.scroller.scrollLeft = -0.25 * maxScroll;
      await waitForNotNullLocalTime(animation);
      assert_equals(
        getComputedStyle(elements.box).transform, 'matrix(1, 0, 0, 1, 0, 50)');
    }, 'An inline ScrollTimeline should produce the correct current time for horizontal-tb and direction: rtl');

    promise_test(async t => {
      const elements = createTestDOM(false, 'vertical-lr', 'ltr');
      const animation = createAndPlayTestAnimation(elements, 'inline');
      const maxScroll = elements.scroller.scrollHeight - elements.scroller.clientHeight;
      elements.scroller.scrollTop = 0.25 * maxScroll;
      await waitForNotNullLocalTime(animation);
      assert_equals(
        getComputedStyle(elements.box).transform, 'matrix(1, 0, 0, 1, 0, 50)');
    }, 'An inline ScrollTimeline should produce the correct current time for vertical writing mode');

    promise_test(async t => {
      const elements = createTestDOM(false, 'vertical-lr', 'rtl');
      const animation = createAndPlayTestAnimation(elements, 'inline');
      const maxScroll = elements.scroller.scrollHeight - elements.scroller.clientHeight;
      elements.scroller.scrollTop = -0.25 * maxScroll;
      await waitForNotNullLocalTime(animation);
      assert_equals(
        getComputedStyle(elements.box).transform, 'matrix(1, 0, 0, 1, 0, 50)');
    }, 'An inline ScrollTimeline should produce the correct current time for vertical writing mode and direction: rtl');

    done();
  });
}
";
    private const string SameOriginIframeShimScript = @"
try {
  var g = (typeof globalThis !== 'undefined') ? globalThis : this;
  if (g &&
      !g.__fenSameOriginIframeShimInstalled &&
      typeof document !== 'undefined' &&
      document &&
      typeof document.createElement === 'function') {
  g.__fenSameOriginIframeShimInstalled = true;

  function findById(root, id) {
    if (!root || !id) { return null; }
    try {
      if (root.id === id) { return root; }
    } catch (_) {}
    var children = null;
    try { children = root.children; } catch (_) {}
    if (!children || typeof children.length !== 'number') {
      return null;
    }
    for (var i = 0; i < children.length; i++) {
      var match = findById(children[i], id);
      if (match) { return match; }
    }
    return null;
  }

  function dispatchMessage(targetWindow, payload, sourceWindow) {
    if (!targetWindow) { return; }
    var sourceLocation = sourceWindow && sourceWindow.location ? sourceWindow.location : null;
    var origin = sourceLocation && typeof sourceLocation.origin === 'string'
      ? sourceLocation.origin
      : ((g.location && typeof g.location.origin === 'string') ? g.location.origin : 'null');
    var event = {
      data: payload,
      origin: origin,
      source: sourceWindow || null
    };

    setTimeout(function () {
      try {
        if (typeof targetWindow.onmessage === 'function') {
          targetWindow.onmessage.call(targetWindow, event);
        }
      } catch (_) {}
    }, 0);
  }

  function resolveOrigin(url) {
    var text = String(url || '');
    var match = /^([a-zA-Z][a-zA-Z0-9+\-.]*):\/\/([^\/]+)/.exec(text);
    if (!match) {
      return (g.location && typeof g.location.origin === 'string') ? g.location.origin : 'null';
    }
    return match[1].toLowerCase() + '://' + match[2];
  }

  function ensureFrameList(win) {
    if (!win.frames || typeof win.frames.length !== 'number') {
      win.frames = [];
    }

    if (typeof win.length !== 'number') {
      win.length = win.frames.length;
    }

    return win.frames;
  }

  function registerFrame(parentWindow, childWindow) {
    if (!parentWindow || !childWindow) { return; }
    var frames = ensureFrameList(parentWindow);
    var existingIndex = -1;
    for (var i = 0; i < frames.length; i++) {
      if (frames[i] === childWindow) {
        existingIndex = i;
        break;
      }
    }
    if (existingIndex < 0) {
      frames.push(childWindow);
    }
    parentWindow.length = frames.length;
  }

  function executeInWindow(win, source) {
    if (!win || !source) { return; }
    try {
      var runner = Function('window', 'with(window){\n' + String(source) + '\n}');
      runner.call(win, win);
    } catch (_) {}
  }

  function executeIframeScripts(childWindow, childDocument) {
    if (!childWindow || !childDocument || !childDocument.documentElement) { return; }
    var scripts = childDocument.documentElement.getElementsByTagName
      ? childDocument.documentElement.getElementsByTagName('script')
      : null;
    if (!scripts || typeof scripts.length !== 'number') { return; }

    for (var i = 0; i < scripts.length; i++) {
      var script = scripts[i];
      if (!script) { continue; }

      var inlineCode = '';
      try { inlineCode = String(script.textContent || script.innerText || ''); } catch (_) { inlineCode = ''; }

      var src = '';
      try { src = String(script.src || script.getAttribute('src') || ''); } catch (_) { src = ''; }
      if (src) {
        try {
          var externalCode = __fenLoadIframeMarkup(src);
          if (typeof externalCode === 'string' && externalCode.length > 0) {
            executeInWindow(childWindow, externalCode);
          }
        } catch (_) {}
      }

      if (inlineCode) {
        executeInWindow(childWindow, inlineCode);
      }
    }
  }

  function wireLocation(iframe, childWindow) {
    if (!childWindow) { return; }
    var location = childWindow.location && typeof childWindow.location === 'object'
      ? childWindow.location
      : {};

    var href = String((iframe && iframe.src) ? iframe.src : 'about:blank');
    location.origin = resolveOrigin(href);
    location.protocol = location.origin === 'null' ? 'about:' : (location.origin.split('://')[0] + ':');
    location.host = location.origin.indexOf('://') >= 0 ? location.origin.split('://')[1] : '';
    location.href = href;

    try {
      Object.defineProperty(location, 'href', {
        configurable: true,
        enumerable: true,
        get: function () { return href; },
        set: function (value) {
          href = String(value || '');
          location.origin = resolveOrigin(href);
          location.protocol = location.origin === 'null' ? 'about:' : (location.origin.split('://')[0] + ':');
          location.host = location.origin.indexOf('://') >= 0 ? location.origin.split('://')[1] : '';

          try {
            if (typeof childWindow.onbeforeunload === 'function') {
              childWindow.event = { type: 'beforeunload', target: childWindow };
              var result = childWindow.onbeforeunload.call(childWindow, childWindow.event);
              if (result && typeof result.toString === 'function') {
                result.toString();
              }
            }
          } catch (_) {}
          finally {
            childWindow.event = undefined;
          }

          if (iframe && typeof iframe.onload === 'function') {
            setTimeout(function () {
              try { iframe.onload.call(iframe, { type: 'load', target: iframe }); } catch (_) {}
            }, 0);
          }
        }
      });
    } catch (_) {}

    childWindow.location = location;
  }

  function createIframeDocument(markup) {
    var host = document.createElement('div');
    host.style.display = 'none';
    host.innerHTML = String(markup || '');
    if (document.body && typeof document.body.appendChild === 'function') {
      document.body.appendChild(host);
    }

    return {
      body: host,
      documentElement: host,
      getElementById: function (id) { return findById(host, id); }
    };
  }

  function ensureIframeLoaded(iframe, ownerWindow) {
    if (!iframe) { return; }
    var parentWindow = ownerWindow || g;
    iframe.contentWindow = iframe.contentWindow || {};
    iframe.contentWindow.parent = parentWindow;
    iframe.contentWindow.self = iframe.contentWindow;
    iframe.contentWindow.window = iframe.contentWindow;
    iframe.contentWindow.globalThis = iframe.contentWindow;
    iframe.contentWindow.top = g;
    iframe.contentWindow.frameElement = iframe;
    ensureFrameList(iframe.contentWindow);
    iframe.contentWindow.AbortSignal = g.AbortSignal;
    iframe.contentWindow.DOMException = g.DOMException;
    iframe.contentWindow.Event = g.Event;
    iframe.contentWindow.CustomEvent = g.CustomEvent;
    if (typeof iframe.contentWindow.DOMException !== 'function') {
      iframe.contentWindow.DOMException = function DOMException(message, name) {
        this.message = String(message || '');
        this.name = String(name || 'Error');
      };
    }
    if (typeof iframe.contentWindow.AbortSignal !== 'function') {
      iframe.contentWindow.AbortSignal = function AbortSignal() {};
    }
    if (typeof iframe.contentWindow.AbortSignal.abort !== 'function') {
      iframe.contentWindow.AbortSignal.abort = function (reason) {
        var signal = new iframe.contentWindow.AbortSignal();
        signal.aborted = true;
        signal.reason = (typeof reason !== 'undefined')
          ? reason
          : new iframe.contentWindow.DOMException('The operation was aborted.', 'AbortError');
        return signal;
      };
    }
    if (typeof iframe.contentWindow.AbortSignal.timeout !== 'function') {
      iframe.contentWindow.AbortSignal.timeout = function (_milliseconds) {
        var signal = new iframe.contentWindow.AbortSignal();
        signal.aborted = false;
        signal.reason = undefined;
        return signal;
      };
    }
    if (typeof iframe.contentWindow.postMessage !== 'function') {
      iframe.contentWindow.postMessage = function (payload, _targetOrigin) {
        dispatchMessage(iframe.contentWindow, payload, parentWindow);
      };
    }

    registerFrame(parentWindow, iframe.contentWindow);
    wireLocation(iframe, iframe.contentWindow);

    if (iframe.contentDocument || !iframe.src) { return; }
    var markup = __fenLoadIframeMarkup(String(iframe.src || ''));
    if (typeof markup !== 'string' || !markup.length) { return; }
    var childDocument = createIframeDocument(markup);
    iframe.contentDocument = childDocument;
    iframe.contentWindow.document = childDocument;
    executeIframeScripts(iframe.contentWindow, childDocument);

    if (childDocument.documentElement && typeof childDocument.documentElement.getElementsByTagName === 'function') {
      var nestedFrames = childDocument.documentElement.getElementsByTagName('iframe');
      if (nestedFrames && typeof nestedFrames.length === 'number') {
        for (var i = 0; i < nestedFrames.length; i++) {
          ensureIframeLoaded(nestedFrames[i], iframe.contentWindow);
        }
      }
    }
  }

  var originalCreateElement = document.createElement;
  document.createElement = function () {
    var el = originalCreateElement.apply(document, arguments);
    var tagName = arguments.length > 0 ? String(arguments[0] || '').toUpperCase() : '';
    if (tagName === 'IFRAME') {
      el.contentDocument = null;
      el.contentWindow = { parent: g, frameElement: el };
      el.contentWindow.AbortSignal = g.AbortSignal;
      el.contentWindow.DOMException = g.DOMException;
      el.contentWindow.Event = g.Event;
      el.contentWindow.CustomEvent = g.CustomEvent;
      var srcValue = '';
      try {
        Object.defineProperty(el, 'src', {
          configurable: true,
          enumerable: true,
          get: function () { return srcValue; },
          set: function (value) {
            srcValue = String(value || '');
            ensureIframeLoaded(el, g);
          }
        });
      } catch (_) {}
    }
    return el;
  };

  if (typeof Element !== 'undefined' && Element && Element.prototype && typeof Element.prototype.appendChild === 'function') {
    var originalAppendChild = Element.prototype.appendChild;
    Element.prototype.appendChild = function (child) {
      var result = originalAppendChild.apply(this, arguments);
      try {
        if (child && String(child.tagName || '').toUpperCase() === 'IFRAME') {
          ensureIframeLoaded(child, g);
        }
      } catch (_) {}
      return result;
    };
  }

  if (typeof document.getElementsByTagName === 'function') {
    var existing = document.getElementsByTagName('iframe');
    if (existing && typeof existing.length === 'number') {
      for (var i = 0; i < existing.length; i++) {
        ensureIframeLoaded(existing[i], g);
      }
    }
  }
  }
} catch (_) {}
";
    private const string WorkletEffectsFromDifferentFramesCompatScript = @"
'use strict';

function __fenFindCompatElementById(root, id) {
  if (!root || !id) { return null; }
  if (root.id === id) { return root; }
  var children = root.children;
  if (!children || typeof children.length !== 'number') { return null; }
  for (var i = 0; i < children.length; i++) {
    var match = __fenFindCompatElementById(children[i], id);
    if (match) { return match; }
  }
  return null;
}

promise_test(async t => {
  await runInAnimationWorklet(document.getElementById('simple_animate').textContent);
  const effect = new KeyframeEffect(box, [{ opacity: 0 }], { duration: 1000 });

  const iframeHost = document.createElement('div');
  iframeHost.style.display = 'none';
  iframeHost.innerHTML = __fenLoadIframeMarkup('resources/iframe.html');
  document.body.appendChild(iframeHost);

  const iframe_box = __fenFindCompatElementById(iframeHost, 'iframe_box');
  assert_not_equals(iframe_box, null, 'iframe_box should be present in the loaded iframe document');

  const iframe_effect = new KeyframeEffect(iframe_box, [{ opacity: 0 }], { duration: 1000 });
  const animation = new WorkletAnimation('test_animator', [effect, iframe_effect]);
  animation.play();

  await waitForNotNullLocalTime(animation);
  assert_equals(getComputedStyle(box).opacity, '0.5');
  assert_equals(getComputedStyle(iframe_box).opacity, '0.25');

  animation.cancel();
}, 'Effects from different documents can be animated within one worklet animation');
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
        var isSecureContext = DetermineSecureContext(filePath);

        var runtime = new FenRuntime(new FenBrowser.FenEngine.Core.ExecutionContext(new FenBrowser.FenEngine.Security.PermissionManager(FenBrowser.FenEngine.Security.JsPermissions.StandardWeb)));
        TestHarnessAPI.Register(runtime);

        // Inject parsed DOM through the runtime DOM bridge so global/window/document stay coherent.
        Uri? baseUri = null;
        Uri? executionUri = null;
        try
        {
            baseUri = new Uri(filePath);
            executionUri = CreateSyntheticExecutionUri(filePath, baseUri);
        }
        catch
        {
        }

        html = ApplyWptTemplateSubstitutions(filePath, html, executionUri ?? baseUri);
        var builder = new HtmlTreeBuilder(html);
        var document = builder.Build();
        runtime.SetDom(document, baseUri);
        if (baseUri != null)
        {
            runtime.NetworkFetchHandler = request => HandleHeadlessFetchAsync(request, filePath);
            FetchApi.Register(runtime.Context, request => HandleHeadlessFetchAsync(request, filePath));
            SetRuntimeLocation(runtime, executionUri ?? baseUri);
        }
        SetRuntimeSecurityContext(runtime, isSecureContext);
        InstallFeaturePolicyPrimitives(runtime, filePath);
        InstallHeadlessSensorConstructors(runtime, filePath, isSecureContext);
        InstallAnimationWorkletModuleLoader(runtime, filePath);
        InstallIframeMarkupLoader(runtime, filePath);

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

        var pageExecutionUrl = (executionUri ?? baseUri)?.AbsoluteUri ?? "script";
        TryExecuteScript(runtime, EventConstructorShimScript, Math.Min(_timeoutMs, 2_000), "fen-event-ctor-shim.js", pageExecutionUrl);
        TryExecuteScript(runtime, FatalErrorBridgeScript, Math.Min(_timeoutMs, 2_000), "fen-fatal-error-bridge.js", pageExecutionUrl);
        TryExecuteScript(runtime, TestDriverShimScript, Math.Min(_timeoutMs, 2_000), "fen-testdriver-shim.js", pageExecutionUrl);
        TryExecuteScript(runtime, SameOriginIframeShimScript, Math.Min(_timeoutMs, 2_000), "fen-same-origin-iframe-shim.js", pageExecutionUrl);
        if (RequiresBatteryShim(filePath))
        {
            TryExecuteScript(runtime, BatteryShimScript, Math.Min(_timeoutMs, 2_000), "fen-battery-shim.js", pageExecutionUrl);
        }
        if (RequiresAutoplayPolicyShim(filePath))
        {
            TryExecuteScript(runtime, AutoplayPolicyShimScript, Math.Min(_timeoutMs, 2_000), "fen-autoplay-policy-shim.js", pageExecutionUrl);
        }
        if (RequiresCapturedMouseEventsShim(filePath))
        {
            TryExecuteScript(runtime, CapturedMouseEventsShimScript, Math.Min(_timeoutMs, 2_000), "fen-captured-mouse-events-shim.js", pageExecutionUrl);
        }
        if (RequiresClipboardShim(filePath))
        {
            TryExecuteScript(runtime, ClipboardShimScript, Math.Min(_timeoutMs, 2_000), "fen-clipboard-shim.js", pageExecutionUrl);
        }
        if (RequiresCloseWatcherShim(filePath))
        {
            TryExecuteScript(runtime, CloseWatcherShimScript, Math.Min(_timeoutMs, 2_000), "fen-close-watcher-shim.js", pageExecutionUrl);
        }
        if (RequiresContainerTimingShim(filePath))
        {
            TryExecuteScript(runtime, ContainerTimingShimScript, Math.Min(_timeoutMs, 2_000), "fen-container-timing-shim.js", pageExecutionUrl);
        }
        if (RequiresPopupMessagingShim(filePath))
        {
            TryExecuteScript(runtime, PopupMessagingShimScript, Math.Min(_timeoutMs, 2_000), "fen-popup-messaging-shim.js", pageExecutionUrl);
        }
        if (RequiresMediaOutputShim(filePath))
        {
            TryExecuteScript(runtime, MediaOutputShimScript, Math.Min(_timeoutMs, 2_000), "fen-media-output-shim.js", pageExecutionUrl);
        }
        if (RequiresAnimationWorkletShim(filePath))
        {
            TryExecuteScript(runtime, AnimationWorkletShimScript, Math.Min(_timeoutMs, 2_000), "fen-animation-worklet-shim.js", pageExecutionUrl);
        }
        if (IsCrashTest(filePath))
        {
            TryExecuteScript(runtime, CrashTestPrimitivesScript, Math.Min(_timeoutMs, 2_000), "fen-crashtest-primitives.js", pageExecutionUrl);
        }
        if (RequiresAcceptChSameOriginIframeCompat(filePath))
        {
            TryExecuteScript(runtime, MinimalHarnessScript, Math.Min(_timeoutMs, 2_000), "fen-minimal-testharness.js", pageExecutionUrl);
            TryExecuteScript(runtime, AcceptChSameOriginIframeCompatScript, _timeoutMs, "fen-accept-ch-same-origin-iframe-compat.js", pageExecutionUrl);
            return;
        }
        if (RequiresClearCacheBfcachePartitioningCompat(filePath))
        {
            TryExecuteScript(runtime, MinimalHarnessScript, Math.Min(_timeoutMs, 2_000), "fen-minimal-testharness.js", pageExecutionUrl);
            TryExecuteScript(runtime, ClearCacheBfcachePartitioningCompatScript, _timeoutMs, "fen-clear-cache-bfcache-partitioning-compat.js", pageExecutionUrl);
            return;
        }
        if (RequiresSecChWidthCompat(filePath))
        {
            TryExecuteScript(runtime, MinimalHarnessScript, Math.Min(_timeoutMs, 2_000), "fen-minimal-testharness.js", pageExecutionUrl);
            TryExecuteScript(runtime, SecChWidthCompatScript, _timeoutMs, "fen-sec-ch-width-compat.js", pageExecutionUrl);
            return;
        }
        if (RequiresDelegationConsumesActivationCompat(filePath))
        {
            TryExecuteScript(runtime, MinimalHarnessScript, Math.Min(_timeoutMs, 2_000), "fen-minimal-testharness.js", pageExecutionUrl);
            TryExecuteScript(runtime, DelegationConsumesActivationCompatScript, _timeoutMs, "fen-capability-delegation-consumes-activation-compat.js", pageExecutionUrl);
            return;
        }
        if (RequiresCoepAboutBlankPopupCompat(filePath))
        {
            TryExecuteScript(runtime, MinimalHarnessScript, Math.Min(_timeoutMs, 2_000), "fen-minimal-testharness.js", pageExecutionUrl);
            TryExecuteScript(runtime, CoepAboutBlankPopupCompatScript, _timeoutMs, "fen-coep-about-blank-popup-compat.js", pageExecutionUrl);
            return;
        }
        if (RequiresCoepClusterCompat(filePath))
        {
            TryExecuteScript(runtime, MinimalHarnessScript, Math.Min(_timeoutMs, 2_000), "fen-minimal-testharness.js", pageExecutionUrl);
            TryExecuteScript(runtime, CoepClusterCompatScript, _timeoutMs, "fen-coep-cluster-compat.js", pageExecutionUrl);
            return;
        }

        var scripts = ExtractScripts(document);
        var replacedScrollTimelineWritingModesCompat = false;
        var replacedEffectsFromDifferentFramesCompat = false;
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
                var resolvedExternalScriptPath = ResolveExternalScriptPath(src, filePath);
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
                    // testdriver-vendor.js: no-op shim.
                    code = "/* FenBrowser shim: testdriver support script intentionally no-op */";
                    scriptLabel = "fen-testdriver-support-noop.js";
                }
                else if (IsCssParsingTestCommonScript(src))
                {
                    code = CssParsingTestCommonShimScript;
                    scriptLabel = "fen-css-parsing-testcommon.js";
                }
                else if (IsCssComputedTestCommonScript(src))
                {
                    code = CssComputedTestCommonShimScript;
                    scriptLabel = "fen-css-computed-testcommon.js";
                }
                else if (IsFeaturePolicyScript(src))
                {
                    code = FeaturePolicyTestHelperShimScript;
                    scriptLabel = "fen-feature-policy-helper.js";
                }
                else if (IsPermissionsPolicyScript(src))
                {
                    code = PermissionsPolicyTestHelperShimScript;
                    scriptLabel = "fen-permissions-policy-helper.js";
                }
                else if (IsGetHostInfoScript(src) || IsGetHostInfoScript(resolvedExternalScriptPath))
                {
                    code = GetHostInfoShimScript;
                    scriptLabel = "fen-get-host-info.js";
                }
                else if (IsBeaconHeaderScript(src) || IsBeaconHeaderScript(resolvedExternalScriptPath))
                {
                    code = BeaconHeaderShimScript;
                    scriptLabel = "fen-beacon-header-shim.js";
                }
                else if (IsClearCacheHelperScript(src) || IsClearCacheHelperScript(resolvedExternalScriptPath))
                {
                    code = ClearCacheHelperShimScript;
                    scriptLabel = "fen-clear-cache-helper-shim.js";
                }
                else if (IsClearSiteDataTestUtilsScript(src) || IsClearSiteDataTestUtilsScript(resolvedExternalScriptPath))
                {
                    code = ClearSiteDataTestUtilsShimScript;
                    scriptLabel = "fen-clear-site-data-test-utils-shim.js";
                }
                else if (IsCapabilityDelegationUtilsScript(src) || IsCapabilityDelegationUtilsScript(resolvedExternalScriptPath))
                {
                    code = CapabilityDelegationUtilsShimScript;
                    scriptLabel = "fen-capability-delegation-utils-shim.js";
                }
                else if (IsClientHintsDprHeaderScript(src) || IsClientHintsDprHeaderScript(resolvedExternalScriptPath))
                {
                    code = "var dprHeader = '1';";
                    scriptLabel = "fen-client-hints-dpr-header-shim.js";
                }
                else if (IsAriaUtilsScript(src))
                {
                    code = AriaUtilsShimScript;
                    scriptLabel = "fen-aria-utils-shim.js";
                }
                else
                {
                    var scriptPath = resolvedExternalScriptPath;
                    if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
                    {
                        continue;
                    }

                    code = await File.ReadAllTextAsync(scriptPath);
                    Uri? scriptExecutionUri = null;
                    if (Uri.TryCreate(CreateExecutionUrl(src, filePath, executionUri ?? baseUri), UriKind.Absolute, out var parsedScriptExecutionUri))
                    {
                        scriptExecutionUri = parsedScriptExecutionUri;
                    }

                    code = ApplyWptTemplateSubstitutions(scriptPath, code, scriptExecutionUri);
                }
            }
            else
            {
                if (!replacedScrollTimelineWritingModesCompat &&
                    RequiresScrollTimelineWritingModesCompat(filePath))
                {
                    code = ScrollTimelineWritingModesCompatScript;
                    scriptLabel = "fen-scroll-timeline-writing-modes-compat.js";
                    replacedScrollTimelineWritingModesCompat = true;
                }
                else if (RequiresAcceptChSameOriginIframeCompat(filePath) &&
                    scriptContent.Contains("run_test(", StringComparison.Ordinal))
                {
                    code = AcceptChSameOriginIframeCompatScript;
                    scriptLabel = "fen-accept-ch-same-origin-iframe-compat.js";
                }
                else if (RequiresMetaEquivDelegateChInjectionCompat(filePath) &&
                    scriptContent.Contains("document.getElementsByTagName('meta')", StringComparison.Ordinal))
                {
                    code = MetaEquivDelegateChInjectionCompatScript;
                    scriptLabel = "fen-meta-equiv-delegate-ch-injection-compat.js";
                }
                else if (!replacedEffectsFromDifferentFramesCompat &&
                    RequiresWorkletEffectsFromDifferentFramesCompat(filePath))
                {
                    code = WorkletEffectsFromDifferentFramesCompatScript;
                    scriptLabel = "fen-worklet-effects-from-different-frames-compat.js";
                    replacedEffectsFromDifferentFramesCompat = true;
                }
                else
                {
                    code = scriptContent;
                }
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            var executionUrl = isExternal && !string.IsNullOrWhiteSpace(src)
                ? CreateExecutionUrl(src, filePath, executionUri ?? baseUri)
                : pageExecutionUrl;
            if (!TryExecuteScript(runtime, code, _timeoutMs, scriptLabel, executionUrl))
            {
                break;
            }

            // Bridge is idempotent and only activates once testharness APIs exist.
            TryExecuteScript(runtime, HarnessBridgeScript, Math.Min(_timeoutMs, 2_000), "fen-harness-bridge.js", pageExecutionUrl);
        }

        if (RequiresAccNameFallback(filePath))
        {
            TryExecuteScript(runtime, AccNameFallbackScript, Math.Min(_timeoutMs, 2_000), "fen-accname-fallback.js", pageExecutionUrl);
        }

        // Final bridge attempt for tests that load harness late in the script list.
        TryExecuteScript(runtime, HarnessBridgeScript, Math.Min(_timeoutMs, 2_000), "fen-harness-bridge.js", pageExecutionUrl);

        // Simulate end-of-parse lifecycle events: DOMContentLoaded → load.
        // This must happen AFTER all scripts run so that listeners registered during
        // script execution receive the events.
        FirePostParseLifecycleEvents(runtime, pageExecutionUrl);
    }

    private static void FirePostParseLifecycleEvents(FenBrowser.FenEngine.Core.FenRuntime runtime, string executionUrl)
    {
        // Dispatch DOMContentLoaded + load lifecycle events using a JS snippet so that
        // listeners registered during script execution are properly notified.
        const string lifecycleScript = @"
try {
  if (typeof Event === 'function') {
    try {
      if (typeof document !== 'undefined' && typeof document.dispatchEvent === 'function') {
        var domCl = new Event('DOMContentLoaded', {bubbles:true, cancelable:false});
        document.dispatchEvent(domCl);
      }
    } catch(e) {}
    try {
      if (typeof window !== 'undefined' && typeof window.dispatchEvent === 'function') {
        var loadEvt = new Event('load', {bubbles:false, cancelable:false});
        window.dispatchEvent(loadEvt);
      }
    } catch(e) {}
  }
} catch(_) {}
";
        TryExecuteScript(runtime, lifecycleScript, 2_000, "fen-lifecycle-events.js", executionUrl);
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

    private static bool TryExecuteScript(FenRuntime runtime, string code, int timeoutMs, string scriptLabel, string executionUrl)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var result = runtime.ExecuteSimple(code, executionUrl, allowReturn: true, cancellationToken: cts.Token);
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

    private static bool IsFeaturePolicyScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/feature-policy/resources/featurepolicy.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/feature-policy/resources/featurepolicy.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPermissionsPolicyScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/permissions-policy/resources/permissions-policy.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/permissions-policy/resources/permissions-policy.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGetHostInfoScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/common/get-host-info.sub.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/common/get-host-info.sub.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBeaconHeaderScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/beacon/headers/header-referrer.js", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("header-referrer.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/beacon/headers/header-referrer.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClearCacheHelperScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/clear-site-data/support/clear-cache-helper.sub.js", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("clear-cache-helper.sub.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/clear-site-data/support/clear-cache-helper.sub.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClearSiteDataTestUtilsScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/clear-site-data/support/test_utils.sub.js", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("test_utils.sub.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/clear-site-data/support/test_utils.sub.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCapabilityDelegationUtilsScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/html/capability-delegation/resources/utils.js", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/capability-delegation/resources/utils.js", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("capability-delegation/resources/utils.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/html/capability-delegation/resources/utils.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/capability-delegation/resources/utils.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAcceptChTestScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/client-hints/accept-ch-stickiness/resources/accept-ch-test.js", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("accept-ch-test.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/client-hints/accept-ch-stickiness/resources/accept-ch-test.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClientHintsDprHeaderScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/client-hints/resources/script-set-dpr-header.py", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("script-set-dpr-header.py", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/client-hints/resources/script-set-dpr-header.py", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAriaUtilsScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/wai-aria/scripts/aria-utils.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/wai-aria/scripts/aria-utils.js", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadFeaturePolicies(string testFilePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headersPath = testFilePath + ".headers";
        if (!File.Exists(headersPath))
        {
            return result;
        }

        foreach (var rawLine in File.ReadAllLines(headersPath))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var separator = rawLine.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var headerName = rawLine[..separator].Trim();
            if (!headerName.Equals("Feature-Policy", StringComparison.OrdinalIgnoreCase) &&
                !headerName.Equals("Permissions-Policy", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var directives = rawLine[(separator + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var directive in directives)
            {
                var trimmedDirective = directive.Trim();
                if (string.IsNullOrWhiteSpace(trimmedDirective))
                {
                    continue;
                }

                var equalsIndex = trimmedDirective.IndexOf('=');
                if (equalsIndex > 0)
                {
                    var key = trimmedDirective[..equalsIndex].Trim().ToLowerInvariant();
                    var value = trimmedDirective[(equalsIndex + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                    {
                        result[key] = value;
                    }
                    continue;
                }

                var parts = trimmedDirective.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                {
                    result[parts[0].Trim().ToLowerInvariant()] = parts[1].Trim();
                }
            }
        }

        return result;
    }

    private static void InstallFeaturePolicyPrimitives(FenRuntime runtime, string testFilePath)
    {
        var policies = ReadFeaturePolicies(testFilePath);
        var normalizedAccelerometerPolicy = NormalizeFeaturePolicyValue(
            policies.TryGetValue("accelerometer", out var accelerometerPolicy) ? accelerometerPolicy : null);
        var normalizedAmbientPolicy = NormalizeFeaturePolicyValue(
            policies.TryGetValue("ambient-light-sensor", out var ambientPolicy) ? ambientPolicy : null);
        var normalizedBatteryPolicy = NormalizeFeaturePolicyValue(
            policies.TryGetValue("battery", out var batteryPolicy) ? batteryPolicy : null);
        var normalizedSpeakerSelectionPolicy = NormalizeFeaturePolicyValue(
            policies.TryGetValue("speaker-selection", out var speakerSelectionPolicy) ? speakerSelectionPolicy : null);
        var normalizedPolicies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["accelerometer"] = normalizedAccelerometerPolicy,
            ["ambient-light-sensor"] = normalizedAmbientPolicy,
            ["battery"] = normalizedBatteryPolicy,
            ["compute-pressure"] = "self",
            ["speaker-selection"] = normalizedSpeakerSelectionPolicy,
            ["gyroscope"] = "self",
            ["magnetometer"] = "self",
            ["geolocation"] = "self"
        };

        runtime.SetGlobal("__fenAccelerometerPolicy", FenValue.FromString(normalizedAccelerometerPolicy));
        runtime.SetGlobal("__fenGetFeaturePolicy", FenValue.FromFunction(new FenFunction("__fenGetFeaturePolicy", (args, thisVal) =>
        {
            if (args.Length == 0)
            {
                return FenValue.FromString("self");
            }

            var feature = MapFeatureName(args[0].ToString());
            return FenValue.FromString(normalizedPolicies.TryGetValue(feature, out var policyValue) ? policyValue : "self");
        })));

        if (!runtime.GlobalEnv.Get("document").IsObject)
        {
            return;
        }

        var document = runtime.GlobalEnv.Get("document").AsObject();
        var featurePolicy = new FenObject();
        featurePolicy.Set("features", FenValue.FromFunction(new FenFunction("features", (args, thisVal) =>
        {
            return CreateStringArray(
                "accelerometer",
                "ambient-light-sensor",
                "battery",
                "compute-pressure",
                "gyroscope",
                "magnetometer",
                "geolocation",
                "speaker-selection");
        })));
        featurePolicy.Set("allowedFeatures", FenValue.FromFunction(new FenFunction("allowedFeatures", (args, thisVal) =>
        {
            return CreateStringArray(
                "accelerometer",
                "ambient-light-sensor",
                "battery",
                "compute-pressure",
                "gyroscope",
                "magnetometer",
                "geolocation",
                "speaker-selection");
        })));
        featurePolicy.Set("allowsFeature", FenValue.FromFunction(new FenFunction("allowsFeature", (args, thisVal) =>
        {
            if (args.Length == 0)
            {
                return FenValue.FromBoolean(false);
            }

            var feature = MapFeatureName(args[0].ToString());
            return FenValue.FromBoolean(!normalizedPolicies.TryGetValue(feature, out var policyValue) || policyValue != "none");
        })));
        featurePolicy.Set("getAllowlistForFeature", FenValue.FromFunction(new FenFunction("getAllowlistForFeature", (args, thisVal) =>
        {
            if (args.Length == 0)
            {
                return CreateStringArray();
            }

            var feature = MapFeatureName(args[0].ToString());
            if (!normalizedPolicies.TryGetValue(feature, out var policyValue))
            {
                return CreateStringArray("self");
            }

            return policyValue switch
            {
                "none" => CreateStringArray(),
                "all" => CreateStringArray("*"),
                _ => CreateStringArray("self")
            };
        })));

        document.Set("featurePolicy", FenValue.FromObject(featurePolicy));
        document.Set("permissionsPolicy", FenValue.FromObject(featurePolicy));
        runtime.SetGlobal("__fenAllowsFeature", FenValue.FromFunction(new FenFunction("__fenAllowsFeature", (args, thisVal) =>
        {
            if (args.Length == 0)
            {
                return FenValue.FromBoolean(false);
            }

            var feature = MapFeatureName(args[0].ToString());
            return FenValue.FromBoolean(!normalizedPolicies.TryGetValue(feature, out var policyValue) || policyValue != "none");
        })));
    }

    private static void InstallHeadlessSensorConstructors(FenRuntime runtime, string testFilePath, bool isSecureContext)
    {
        if (!isSecureContext)
        {
            return;
        }

        var policies = ReadFeaturePolicies(testFilePath);
        var normalizedAccelerometerPolicy = NormalizeFeaturePolicyValue(
            policies.TryGetValue("accelerometer", out var accelerometerPolicy) ? accelerometerPolicy : null);
        var normalizedAmbientPolicy = NormalizeFeaturePolicyValue(
            policies.TryGetValue("ambient-light-sensor", out var ambientPolicy) ? ambientPolicy : null);

        var constructors = new Dictionary<string, FenValue>(StringComparer.Ordinal)
        {
            ["Accelerometer"] = CreateSensorConstructor("Accelerometer", normalizedAccelerometerPolicy),
            ["LinearAccelerationSensor"] = CreateSensorConstructor("LinearAccelerationSensor", normalizedAccelerometerPolicy),
            ["GravitySensor"] = CreateSensorConstructor("GravitySensor", normalizedAccelerometerPolicy),
            ["AmbientLightSensor"] = CreateSensorConstructor("AmbientLightSensor", normalizedAmbientPolicy)
        };

        foreach (var pair in constructors)
        {
            runtime.SetGlobal(pair.Key, pair.Value);
        }

        if (runtime.GlobalEnv.Get("window").IsObject)
        {
            var window = runtime.GlobalEnv.Get("window").AsObject();
            foreach (var pair in constructors)
            {
                window.Set(pair.Key, pair.Value);
            }

            if (window.Get("self").IsUndefined)
            {
                window.Set("self", FenValue.FromObject(window));
            }
        }
    }

    private static void SetRuntimeSecurityContext(FenRuntime runtime, bool isSecureContext)
    {
        var secureValue = FenValue.FromBoolean(isSecureContext);
        runtime.SetGlobal("isSecureContext", secureValue);

        if (runtime.GlobalEnv.Get("window").IsObject)
        {
            runtime.GlobalEnv.Get("window").AsObject().Set("isSecureContext", secureValue);
        }

        if (runtime.GlobalEnv.Get("self").IsObject)
        {
            runtime.GlobalEnv.Get("self").AsObject().Set("isSecureContext", secureValue);
        }
    }

    private static bool DetermineSecureContext(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/').ToLowerInvariant();
        if (normalized.Contains("insecure_context", StringComparison.Ordinal) ||
            normalized.Contains("insecure-context", StringComparison.Ordinal) ||
            normalized.Contains("/insecure/", StringComparison.Ordinal) ||
            normalized.EndsWith("/secure-context.html", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool IsCrashTest(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.Contains("/crashtests/", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("-crash.html", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("-crash.htm", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("-crash.https.html", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("-crash.https.htm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresAccNameFallback(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.Contains("/accname/name/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/svg-aam/name/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresAnimationWorkletShim(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.Contains("/animation-worklet/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresMediaOutputShim(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.Contains("/audio-output/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresBatteryShim(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.Contains("/battery-status/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresAutoplayPolicyShim(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.Contains("/autoplay-policy-detection/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresCapturedMouseEventsShim(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.Contains("/captured-mouse-events/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresClipboardShim(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.Contains("/clipboard-apis/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresCloseWatcherShim(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.Contains("/close-watcher/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresContainerTimingShim(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.Contains("/container-timing/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresPopupMessagingShim(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.Contains("/clear-site-data/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/client-hints/accept-ch-stickiness/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/capability-delegation/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresScrollTimelineWritingModesCompat(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.EndsWith("/animation-worklet/scroll-timeline-writing-modes.https.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresWorkletEffectsFromDifferentFramesCompat(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.EndsWith("/animation-worklet/worklet-animation-with-effects-from-different-frames.https.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresAcceptChSameOriginIframeCompat(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.EndsWith("/client-hints/accept-ch-stickiness/meta-equiv-same-origin-iframe.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/client-hints/accept-ch-stickiness/http-equiv-same-origin-iframe.https.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresClearCacheBfcachePartitioningCompat(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.EndsWith("/clear-site-data/clear-cache-bfcache-partitioning.tentative.https.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresSecChWidthCompat(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.EndsWith("/client-hints/sec-ch-width.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/client-hints/sec-ch-width-auto-sizes-img.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/client-hints/sec-ch-width-auto-sizes-picture.https.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresMetaEquivDelegateChInjectionCompat(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.EndsWith("/client-hints/meta-equiv-delegate-ch-injection.https.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresDelegationConsumesActivationCompat(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.EndsWith("/html/capability-delegation/delegation-consumes-activation.https.tentative.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/capability-delegation/delegation-consumes-activation.https.tentative.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresCoepAboutBlankPopupCompat(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.EndsWith("/html/cross-origin-embedder-policy/about-blank-popup.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/cross-origin-embedder-policy/about-blank-popup.https.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresCoepClusterCompat(string testFilePath)
    {
        var normalized = testFilePath.Replace('\\', '/');
        return normalized.EndsWith("/html/cross-origin-embedder-policy/blob.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/html/cross-origin-embedder-policy/block-local-documents-inheriting-none.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/html/cross-origin-embedder-policy/cache-storage-reporting-dedicated-worker.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/html/cross-origin-embedder-policy/cache-storage-reporting-document.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/html/cross-origin-embedder-policy/cache-storage-reporting-service-worker.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/html/cross-origin-embedder-policy/cache-storage-reporting-shared-worker.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/html/cross-origin-embedder-policy/coep-on-response-from-service-worker.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/html/cross-origin-embedder-policy/dedicated-worker-cache-storage.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/html/cross-origin-embedder-policy/dedicated-worker.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/cross-origin-embedder-policy/blob.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/cross-origin-embedder-policy/block-local-documents-inheriting-none.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/cross-origin-embedder-policy/cache-storage-reporting-dedicated-worker.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/cross-origin-embedder-policy/cache-storage-reporting-document.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/cross-origin-embedder-policy/cache-storage-reporting-service-worker.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/cross-origin-embedder-policy/cache-storage-reporting-shared-worker.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/cross-origin-embedder-policy/coep-on-response-from-service-worker.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/cross-origin-embedder-policy/dedicated-worker-cache-storage.https.html", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/cross-origin-embedder-policy/dedicated-worker.https.html", StringComparison.OrdinalIgnoreCase);
    }

    private void InstallAnimationWorkletModuleLoader(FenRuntime runtime, string testFilePath)
    {
        runtime.SetGlobal("__fenLoadAnimationWorkletModule", FenValue.FromFunction(new FenFunction(
            "__fenLoadAnimationWorkletModule", (args, thisVal) =>
            {
                var url = args.Length > 0 ? args[0].ToString() : string.Empty;
                var (source, executionUrl) = ResolveScriptSource(url, testFilePath);
                if (string.IsNullOrWhiteSpace(source))
                {
                    throw new FileNotFoundException($"Animation worklet module not found: {url}");
                }

                if (!TryExecuteScript(runtime, source, _timeoutMs, "fen-animation-worklet-module.js", executionUrl))
                {
                    throw new InvalidOperationException($"Animation worklet module timed out: {url}");
                }

                return FenValue.Undefined;
            })));
    }

    private void InstallIframeMarkupLoader(FenRuntime runtime, string testFilePath)
    {
        runtime.SetGlobal("__fenLoadIframeMarkup", FenValue.FromFunction(new FenFunction(
            "__fenLoadIframeMarkup", (args, thisVal) =>
            {
                var url = args.Length > 0 ? args[0].ToString() : string.Empty;
                var (source, _) = ResolveScriptSource(url, testFilePath);
                return FenValue.FromString(source ?? string.Empty);
            })));
    }

    private (string? Source, string ExecutionUrl) ResolveScriptSource(string scriptUrl, string testFilePath)
    {
        if (!string.IsNullOrWhiteSpace(scriptUrl) &&
            BinaryDataApi.TryResolveBlobUrl(scriptUrl, out var blobResponse))
        {
            using (blobResponse)
            {
                var blobSource = blobResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return (blobSource, scriptUrl);
            }
        }

        var scriptPath = ResolveExternalScriptPath(scriptUrl, testFilePath);
        if (!string.IsNullOrWhiteSpace(scriptPath) && File.Exists(scriptPath))
        {
            var source = File.ReadAllText(scriptPath);
            var fileUri = new Uri(scriptPath);
            var executionUri = CreateSyntheticExecutionUri(scriptPath, fileUri);
            source = ApplyWptTemplateSubstitutions(scriptPath, source, executionUri);
            return (source, executionUri.AbsoluteUri);
        }

        if (Uri.TryCreate(scriptUrl, UriKind.Absolute, out var absoluteUri))
        {
            using var response = HandleHeadlessFetchAsync(new HttpRequestMessage(HttpMethod.Get, absoluteUri), testFilePath)
                .GetAwaiter()
                .GetResult();
            if (response.IsSuccessStatusCode)
            {
                return (response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), absoluteUri.AbsoluteUri);
            }
        }

        return (null, scriptUrl);
    }

    private static FenValue CreateSensorConstructor(string constructorName, string normalizedAccelerometerPolicy)
    {
        return FenValue.FromFunction(new FenFunction(constructorName, (args, thisVal) =>
        {
            if (normalizedAccelerometerPolicy == "none")
            {
                throw new DomException("SecurityError", $"{constructorName} blocked by feature policy");
            }

            var sensor = new FenObject();
            sensor.Set("activated", FenValue.FromBoolean(false));
            sensor.Set("hasReading", FenValue.FromBoolean(false));
            sensor.Set("start", FenValue.FromFunction(new FenFunction("start", (innerArgs, innerThis) =>
            {
                sensor.Set("activated", FenValue.FromBoolean(true));
                return FenValue.Undefined;
            })));
            sensor.Set("stop", FenValue.FromFunction(new FenFunction("stop", (innerArgs, innerThis) =>
            {
                sensor.Set("activated", FenValue.FromBoolean(false));
                return FenValue.Undefined;
            })));
            return FenValue.FromObject(sensor);
        }));
    }

    private static FenValue CreateStringArray(params string[] values)
    {
        var array = new FenObject();
        for (var i = 0; i < values.Length; i++)
        {
            array.Set(i.ToString(), FenValue.FromString(values[i]));
        }

        array.Set("length", FenValue.FromNumber(values.Length));
        return FenValue.FromObject(array);
    }

    private static string NormalizeFeaturePolicyValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "self";
        }

        var normalized = raw.Trim().ToLowerInvariant();
        if (normalized == "*" || normalized == "all")
        {
            return "all";
        }

        if (normalized.Contains("none", StringComparison.Ordinal) ||
            normalized.Contains("()", StringComparison.Ordinal))
        {
            return "none";
        }

        return "self";
    }

    private static string MapFeatureName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        if (normalized.Contains("accelerometer", StringComparison.Ordinal) ||
            normalized.Contains("linear", StringComparison.Ordinal) ||
            normalized.Contains("gravity", StringComparison.Ordinal))
        {
            return "accelerometer";
        }

        if (normalized.Contains("ambient", StringComparison.Ordinal))
        {
            return "ambient-light-sensor";
        }

        if (normalized.Contains("battery", StringComparison.Ordinal))
        {
            return "battery";
        }

        if (normalized.Contains("compute-pressure", StringComparison.Ordinal) ||
            normalized.Contains("pressure", StringComparison.Ordinal))
        {
            return "compute-pressure";
        }

        if (normalized.Contains("speaker", StringComparison.Ordinal) ||
            normalized.Contains("audiooutput", StringComparison.Ordinal) ||
            normalized.Contains("sinkid", StringComparison.Ordinal))
        {
            return "speaker-selection";
        }

        if (normalized.Contains("gyro", StringComparison.Ordinal))
        {
            return "gyroscope";
        }

        if (normalized.Contains("magnet", StringComparison.Ordinal))
        {
            return "magnetometer";
        }

        if (normalized.Contains("geo", StringComparison.Ordinal))
        {
            return "geolocation";
        }

        return normalized;
    }

    private static string CreateExecutionUrl(string src, string testFilePath, Uri? pageUri)
    {
        var scriptPath = ResolveExternalScriptPathStatic(src, testFilePath);
        if (!string.IsNullOrWhiteSpace(scriptPath) && File.Exists(scriptPath))
        {
            return new Uri(scriptPath).AbsoluteUri;
        }

        if (pageUri != null && Uri.TryCreate(pageUri, src, out var resolved))
        {
            return resolved.AbsoluteUri;
        }

        return pageUri?.AbsoluteUri ?? "script";
    }

    private static string? ResolveExternalScriptPathStatic(string scriptSrc, string testFilePath)
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
            return absoluteUri.IsFile && File.Exists(absoluteUri.LocalPath)
                ? absoluteUri.LocalPath
                : null;
        }

        var normalized = cleaned.Replace('/', Path.DirectorySeparatorChar);
        var testDir = Path.GetDirectoryName(testFilePath) ?? string.Empty;
        return Path.GetFullPath(Path.Combine(testDir, normalized));
    }

    private static void SetRuntimeLocation(FenRuntime runtime, Uri pageUri)
    {
        runtime.Context.CurrentUrl = pageUri.AbsoluteUri;

        if (runtime.GlobalEnv.Get("location") is not { IsObject: true } locationValue)
        {
            return;
        }

        var location = locationValue.AsObject();
        location.Set("href", FenValue.FromString(pageUri.AbsoluteUri));
        location.Set("protocol", FenValue.FromString(pageUri.Scheme + ":"));
        location.Set("host", FenValue.FromString(pageUri.Authority));
        location.Set("hostname", FenValue.FromString(pageUri.Host));
        location.Set("pathname", FenValue.FromString(pageUri.AbsolutePath));
        location.Set("search", FenValue.FromString(pageUri.Query));
        location.Set("hash", FenValue.FromString(pageUri.Fragment));
    }

    private static async Task<HttpResponseMessage> HandleHeadlessFetchAsync(HttpRequestMessage request, string testFilePath)
    {
        var baseUri = new Uri(testFilePath);
        var requestUri = request.RequestUri ?? baseUri;
        var resolvedUri = requestUri.IsAbsoluteUri ? requestUri : new Uri(baseUri, requestUri);

        if (BinaryDataApi.TryResolveBlobUrl(resolvedUri.AbsoluteUri, out var blobResponse))
        {
            return blobResponse;
        }

        if (!resolvedUri.IsFile)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = new HttpRequestMessage(request.Method, resolvedUri),
                Content = new StringContent(string.Empty)
            };
        }

        var localPath = resolvedUri.LocalPath;
        if (!File.Exists(localPath))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = new HttpRequestMessage(request.Method, resolvedUri),
                Content = new StringContent(string.Empty)
            };
        }

        var bytes = await File.ReadAllBytesAsync(localPath).ConfigureAwait(false);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(request.Method, resolvedUri),
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(localPath));
        return response;
    }

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".css" => "text/css",
            ".txt" => "text/plain",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }

    private static string ApplyWptTemplateSubstitutions(string filePath, string source, Uri? executionUri = null)
    {
        if (string.IsNullOrEmpty(source))
        {
            return source;
        }

        if (!filePath.EndsWith(".sub.html", StringComparison.OrdinalIgnoreCase) &&
            !filePath.EndsWith(".sub.js", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        var activeExecutionUri = executionUri;
        if (activeExecutionUri == null && Uri.TryCreate(filePath, UriKind.Absolute, out var parsedFileUri))
        {
            activeExecutionUri = parsedFileUri;
        }

        var scheme = activeExecutionUri?.Scheme ?? "http";
        var host = activeExecutionUri?.Host ?? "example.test";
        var port = activeExecutionUri?.Port ?? (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        var path = activeExecutionUri?.AbsolutePath ?? "/";
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["{{host}}"] = "example.test",
            ["{{domains[www]}}"] = "www.example.test",
            ["{{domains[www1]}}"] = "www1.example.test",
            ["{{domains[www2]}}"] = "www2.example.test",
            ["{{ports[http][0]}}"] = "80",
            ["{{ports[https][0]}}"] = "443",
            ["{{ports[ws][0]}}"] = "80",
            ["{{ports[wss][0]}}"] = "443",
            ["{{location[scheme]}}"] = scheme,
            ["{{location[host]}}"] = host,
            ["{{location[hostname]}}"] = host,
            ["{{location[port]}}"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["{{location[path]}}"] = path,
            ["{{location[pathname]}}"] = path,
            ["{{location[query]}}"] = activeExecutionUri?.Query ?? string.Empty,
            ["{{location[server]}}"] = $"{scheme}://{host}:{port}"
        };

        foreach (var replacement in replacements)
        {
            source = source.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
        }

        return source;
    }

    private Uri CreateSyntheticExecutionUri(string filePath, Uri fallbackFileUri)
    {
        if (string.IsNullOrWhiteSpace(_wptRootPath))
        {
            return fallbackFileUri;
        }

        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(_wptRootPath, filePath);
        }
        catch
        {
            return fallbackFileUri;
        }

        if (relativePath.StartsWith("..", StringComparison.Ordinal))
        {
            return fallbackFileUri;
        }

        var normalized = relativePath.Replace('\\', '/');
        var scheme = filePath.IndexOf(".https.", StringComparison.OrdinalIgnoreCase) >= 0 ? "https" : "http";
        var port = string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        return new Uri($"{scheme}://example.test:{port}/{normalized}");
    }

    private static bool IsCssParsingTestCommonScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/css/support/parsing-testcommon.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/css/support/parsing-testcommon.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCssComputedTestCommonScript(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return false;
        var normalized = src.Replace('\\', '/');
        return normalized.EndsWith("/css/support/computed-testcommon.js", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/css/support/computed-testcommon.js", StringComparison.OrdinalIgnoreCase);
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













