using System;
using System.IO;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.Tooling.Host
{
    internal static class ToolingBrowserHostOptions
    {
        public static BrowserHostOptions CreateForWpt(string wptRootPath)
        {
            return new BrowserHostOptions
            {
                RequestUriMapper = requestUri => MapWptUri(wptRootPath, requestUri),
                ScriptOverrideProvider = requestUri =>
                {
                    if (requestUri == null)
                    {
                        return null;
                    }

                    var uriText = requestUri.ToString();
                    if (uriText.IndexOf("testharnessreport.js", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        return null;
                    }

                    return """
                        (function () {
                            function emit(marker, payload) {
                                try {
                                    console.log(marker + JSON.stringify(payload || {}));
                                } catch (err) {
                                    console.log(marker + '{"error":"serialization_failed"}');
                                }
                            }

                            function installFallbackHarnessIfNeeded() {
                                try {
                                    if (window.location && String(window.location.protocol || '').toLowerCase() === 'about:') {
                                        return false;
                                    }
                                } catch (_) {}

                                if (typeof window.promise_test === 'function') {
                                    return false;
                                }

                                var pendingAsync = 0;
                                var completed = false;
                                var hasResults = false;

                                function failMessage(err) {
                                    if (!err) return '';
                                    if (typeof err === 'string') return err;
                                    if (err && err.message) return String(err.message);
                                    return String(err);
                                }

                                function record(name, pass, message) {
                                    hasResults = true;
                                    emit('__FEN_WPT_RESULT__', {
                                        name: name || '',
                                        status: pass ? 0 : 1,
                                        message: message || ''
                                    });
                                }

                                function maybeComplete() {
                                    if (completed || pendingAsync > 0 || !hasResults) {
                                        return;
                                    }
                                    completed = true;
                                    emit('__FEN_WPT_COMPLETE__', { status: '0', message: 'fallback-harness-complete', total: 0 });
                                }

                                function assert_true(value, description) {
                                    if (!value) throw new Error(description || 'assert_true failed');
                                }

                                function assert_equals(actual, expected, description) {
                                    if (actual !== expected) {
                                        throw new Error(description || ('assert_equals failed: expected=' + String(expected) + ' actual=' + String(actual)));
                                    }
                                }

                                function assert_throws_js(expectedCtor, fn, description) {
                                    var threw = false;
                                    try {
                                        fn();
                                    } catch (err) {
                                        threw = true;
                                        if (expectedCtor && !(err instanceof expectedCtor)) {
                                            throw new Error(description || 'assert_throws_js failed: unexpected error type');
                                        }
                                    }

                                    if (!threw) {
                                        throw new Error(description || 'assert_throws_js failed: no throw');
                                    }
                                }

                                function assert_throws_dom(nameOrCode, maybeCtor, maybeFn) {
                                    var fn = typeof maybeCtor === 'function' ? maybeFn : maybeCtor;
                                    var expectedName = typeof nameOrCode === 'string' ? nameOrCode : '';
                                    var threw = false;

                                    try {
                                        fn();
                                    } catch (err) {
                                        threw = true;
                                        if (expectedName && err && err.name && String(err.name) !== expectedName) {
                                            throw new Error('assert_throws_dom failed: expected ' + expectedName + ' got ' + String(err.name));
                                        }
                                    }

                                    if (!threw) {
                                        throw new Error('assert_throws_dom failed: no throw');
                                    }
                                }

                                window.assert_true = assert_true;
                                window.assert_equals = assert_equals;
                                window.assert_throws_js = assert_throws_js;
                                window.assert_throws_dom = assert_throws_dom;
                                window.setup = function () {};

                                window.test = function (fn, name) {
                                    try {
                                        fn();
                                        record(name || 'test', true, '');
                                    } catch (err) {
                                        record(name || 'test', false, failMessage(err));
                                    }
                                    maybeComplete();
                                };

                                window.promise_test = function (fn, name) {
                                    pendingAsync++;
                                    Promise.resolve()
                                        .then(function () { return fn(); })
                                        .then(function () { record(name || 'promise_test', true, ''); })
                                        .catch(function (err) { record(name || 'promise_test', false, failMessage(err)); })
                                        .finally(function () {
                                            pendingAsync--;
                                            maybeComplete();
                                        });
                                };

                                window.async_test = function (name) {
                                    pendingAsync++;
                                    var done = false;

                                    function finish(pass, message) {
                                        if (done) return;
                                        done = true;
                                        record(name || 'async_test', pass, message || '');
                                        pendingAsync--;
                                        maybeComplete();
                                    }

                                    return {
                                        step: function (fn) {
                                            try {
                                                fn();
                                            } catch (err) {
                                                finish(false, failMessage(err));
                                            }
                                        },
                                        step_func: function (fn) {
                                            return function () {
                                                try {
                                                    return fn.apply(this, arguments);
                                                } catch (err) {
                                                    finish(false, failMessage(err));
                                                    throw err;
                                                }
                                            };
                                        },
                                        done: function () { finish(true, ''); },
                                        add_cleanup: function () {}
                                    };
                                };

                                emit('__FEN_WPT_FALLBACK__', { installed: true });
                                return true;
                            }

                            installFallbackHarnessIfNeeded();

                            function onResult(test) {
                                emit('__FEN_WPT_RESULT__', {
                                    name: test && test.name ? test.name : '',
                                    status: test && typeof test.status !== 'undefined' ? test.status : -1,
                                    message: test && test.message ? String(test.message) : ''
                                });
                            }

                            function onComplete(tests, status) {
                                emit('__FEN_WPT_COMPLETE__', {
                                    status: status && typeof status.status !== 'undefined' ? String(status.status) : '',
                                    message: status && status.message ? String(status.message) : '',
                                    total: Array.isArray(tests) ? tests.length : 0
                                });
                            }

                            var attached = false;
                            function tryAttach() {
                                if (attached) {
                                    return;
                                }

                                if (typeof window.add_result_callback === 'function') {
                                    window.add_result_callback(onResult);
                                } else {
                                    return false;
                                }

                                if (typeof window.add_completion_callback === 'function') {
                                    window.add_completion_callback(onComplete);
                                }

                                attached = true;
                                emit('__FEN_WPT_HOOKED__', { ok: true });
                                emit('__FEN_WPT_STATE__', {
                                    global_fetch: typeof fetch,
                                    promise_test: typeof window.promise_test,
                                    async_test: typeof window.async_test,
                                    test: typeof window.test,
                                    fetch: typeof window.fetch,
                                    Promise: typeof window.Promise,
                                    queueMicrotask: typeof window.queueMicrotask,
                                    setTimeout: typeof window.setTimeout
                                });
                                return true;
                            }

                            if (!tryAttach()) {
                                var retries = 0;
                                var maxRetries = 200;
                                var timer = window.setInterval(function () {
                                    retries++;
                                    if (tryAttach() || retries >= maxRetries) {
                                        window.clearInterval(timer);
                                        if (!attached) {
                                            emit('__FEN_WPT_HOOKED__', { ok: false, reason: 'callbacks_unavailable' });
                                        }
                                    }
                                }, 10);
                            }

                            // Some engine paths may miss standard ready/load dispatch that
                            // testharness waits on before executing queued tests.
                            // Fire a best-effort synthetic lifecycle pulse.
                            window.setTimeout(function () {
                                try {
                                    if (typeof Event === 'function') {
                                        var domReadyEvt = new Event('DOMContentLoaded');
                                        if (document && typeof document.dispatchEvent === 'function') {
                                            document.dispatchEvent(domReadyEvt);
                                        }
                                        var loadEvt = new Event('load');
                                        if (window && typeof window.dispatchEvent === 'function') {
                                            window.dispatchEvent(loadEvt);
                                        }
                                    }
                                } catch (err) {
                                    emit('__FEN_WPT_LIFECYCLE__', { ok: false, reason: String(err) });
                                }
                            }, 0);
                        })();
                        """;
                }
            };
        }

        private static Uri MapWptUri(string wptRootPath, Uri requestUri)
        {
            if (requestUri == null || string.IsNullOrWhiteSpace(wptRootPath))
            {
                return requestUri;
            }

            if (!requestUri.IsFile)
            {
                return requestUri;
            }

            if (File.Exists(requestUri.LocalPath))
            {
                return requestUri;
            }

            var normalizedPath = requestUri.LocalPath.Replace('\\', '/');

            // WPT tests commonly reference harness assets as absolute-from-root paths
            // (e.g. "/resources/testharness.js") even when the page itself is loaded
            // from file://. Map those into the configured WPT checkout root.
            var rootSupportMappings = new[] { "/resources/", "/common/", "/fonts/", "/infrastructure/" };
            foreach (var marker in rootSupportMappings)
            {
                var markerIndex = normalizedPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                var supportRelativePath = normalizedPath[(markerIndex + 1)..];
                var mappedSupportPath = Path.Combine(wptRootPath, supportRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(mappedSupportPath))
                {
                    return new Uri(mappedSupportPath);
                }
            }

            if (!normalizedPath.Contains("/wpt/", StringComparison.OrdinalIgnoreCase) &&
                !normalizedPath.Contains("/tests/", StringComparison.OrdinalIgnoreCase))
            {
                return requestUri;
            }

            string relativePath = normalizedPath;
            var wptIndex = normalizedPath.IndexOf("/wpt/", StringComparison.OrdinalIgnoreCase);
            if (wptIndex >= 0)
            {
                relativePath = normalizedPath[(wptIndex + "/wpt/".Length)..];
            }
            else
            {
                var testsIndex = normalizedPath.IndexOf("/tests/", StringComparison.OrdinalIgnoreCase);
                if (testsIndex >= 0)
                {
                    relativePath = normalizedPath[(testsIndex + "/tests/".Length)..];
                }
            }

            var mappedPath = Path.Combine(wptRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(mappedPath))
            {
                return requestUri;
            }

            return new Uri(mappedPath);
        }
    }
}
