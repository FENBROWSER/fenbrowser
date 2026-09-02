using System;
using System.Collections.Generic;
using System.Net.Http;

namespace FenBrowser.Tooling.Diagnostics;

/// <summary>
/// Serves reCAPTCHA's bundle with a prologue in front of it, so we can see what
/// the bundle does rather than only what it leaves behind.
///
/// The widget renders and then ignores a click that we can prove was delivered
/// correctly, which means it never wired a handler up. Whether it asked for one
/// and we lost it, or never asked, cannot be told apart from the outside:
/// listener maps live in expandos that are not own properties on a host object,
/// so they cannot be enumerated. Recording every addEventListener call as the
/// bundle makes it answers the question directly.
/// </summary>
internal static class CaptchaScriptInstrumentation
{
    private const string InstrumentedMarker = "recaptcha__en.js";

    private static readonly HttpClient Http = new();
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static int _serves;

    // Runs immediately before the bundle, in whichever realm loads it.
    private const string Prologue = """
        (function () {
            // The same bundle is imported into the worker, where there is no
            // window at all. Anything that reaches for one throws before the
            // bundle gets to run, so address the global directly.
            var window = globalThis;
            if (typeof document === 'undefined' || !document) { return; }
            if (window.__fenPrologueInstalled) { return; }
            window.__fenPrologueInstalled = 1;
            if (!window.__fenListenerLog) { window.__fenListenerLog = []; }

            // The bundle wraps its own start-up in try/catch and reports failures
            // by calling console methods, so a fatal error looks like silence from
            // the outside: no listeners, no requests, a widget stuck loading.
            // Record what actually threw, in the realm where it threw.
            window.__fenErrorLog = [];
            var note = function (what) {
                try {
                    if (window.__fenErrorLog.length < 40) { window.__fenErrorLog.push(String(what)); }
                } catch (e) { }
            };
            try {
                window.addEventListener('error', function (ev) {
                    note('error: ' + ((ev && (ev.message || ev.error)) || 'unknown'));
                });
                window.addEventListener('unhandledrejection', function (ev) {
                    note('rejection: ' + ((ev && ev.reason) || 'unknown'));
                });
            } catch (e) { }
            try {
                ['error', 'warn', 'log'].forEach(function (level) {
                    if (!window.console || typeof window.console[level] !== 'function') { return; }
                    var original = window.console[level];
                    window.console[level] = function () {
                        note(level + ': ' + Array.prototype.join.call(arguments, ' '));
                        return original.apply(this, arguments);
                    };
                });
            } catch (e) { }
            // The runner's own recorder is installed after load, so every request
            // and element the bundle makes during setup is already invisible by
            // then. This runs before the bundle in the same realm, which is the
            // only place those are observable.
            window.__fenNetLog = [];
            var stamp = function () {
                try { return Math.round(performance.now()) + 'ms'; } catch (e) { return '?'; }
            };
            try {
                var open = XMLHttpRequest.prototype.open;
                var send = XMLHttpRequest.prototype.send;
                XMLHttpRequest.prototype.open = function (method, url) {
                    try { this.__fenUrl = String(method) + ' ' + String(url); } catch (e) { }
                    return open.apply(this, arguments);
                };
                XMLHttpRequest.prototype.send = function () {
                    var self = this;
                    try {
                        window.__fenNetLog.push(stamp() + ' xhr-send ' + (self.__fenUrl || '?'));
                        var already = self.onreadystatechange;
                        self.onreadystatechange = function () {
                            try {
                                if (self.readyState === 4) {
                                    window.__fenNetLog.push(stamp() + ' xhr-done ' + self.status +
                                        ' len=' + ((self.responseText || '').length) + ' ' + (self.__fenUrl || '?'));
                                }
                            } catch (e) { }
                            if (already) { return already.apply(this, arguments); }
                        };
                    } catch (e) { }
                    return send.apply(this, arguments);
                };
            } catch (e) { note('xhr-hook-threw:' + e); }
            try {
                if (typeof window.fetch === 'function') {
                    var realFetch = window.fetch;
                    window.fetch = function (input) {
                        try {
                            window.__fenNetLog.push(stamp() + ' fetch ' +
                                String((input && input.url) || input));
                        } catch (e) { }
                        return realFetch.apply(this, arguments);
                    };
                }
            } catch (e) { note('fetch-hook-threw:' + e); }
            // Chrome fetches /recaptcha/api2/webworker during set-up and we never
            // do, because our Worker is a stub that swallows everything. Record
            // whether the bundle builds one and what it expects back from it.
            try {
                if (typeof window.Worker === 'function') {
                    var RealWorker = window.Worker;
                    window.Worker = function (url, opts) {
                        note('Worker constructed: ' + String(url));
                        var w = new RealWorker(url, opts);
                        try {
                            var post = w.postMessage;
                            w.postMessage = function (d) {
                                note('worker.postMessage len=' +
                                    (function () { try { return JSON.stringify(d).length; } catch (e) { return '?'; } })());
                                return post.apply(this, arguments);
                            };
                            var add = w.addEventListener;
                            if (typeof add === 'function') {
                                w.addEventListener = function (t) {
                                    note('worker.addEventListener ' + t);
                                    return add.apply(this, arguments);
                                };
                            }
                        } catch (e) { }
                        return w;
                    };
                    window.Worker.prototype = RealWorker.prototype;
                } else {
                    note('window.Worker is ' + (typeof window.Worker));
                }
            } catch (e) { note('worker-hook-threw:' + e); }

            try {
                var create = document.createElement;
                document.createElement = function (tag) {
                    var el = create.apply(this, arguments);
                    try {
                        if (String(tag).toLowerCase() === 'iframe') {
                            window.__fenNetLog.push(stamp() + ' iframe-created');
                        }
                    } catch (e) { }
                    return el;
                };
            } catch (e) { note('createElement-hook-threw:' + e); }

            try {
                if (window.trustedTypes && typeof window.trustedTypes.createPolicy === 'function') {
                    var createPolicy = window.trustedTypes.createPolicy;
                    window.trustedTypes.createPolicy = function (name) {
                        var made;
                        try {
                            made = createPolicy.apply(this, arguments);
                        } catch (err) {
                            note('createPolicy(' + name + ') threw: ' + err);
                            throw err;
                        }
                        note('createPolicy(' + name + ') ok=' + !!made);
                        return made;
                    };
                }
            } catch (e) { }
            try {
                var proto = (typeof EventTarget === 'function' && EventTarget.prototype)
                    ? EventTarget.prototype
                    : null;
                if (!proto || typeof proto.addEventListener !== 'function') {
                    window.__fenListenerLog.push('no-EventTarget-prototype');
                    return;
                }

                var original = proto.addEventListener;
                proto.addEventListener = function (type, handler, options) {
                    try {
                        var who = 'unknown';
                        if (this === window) {
                            who = 'window';
                        } else if (this === document) {
                            who = 'document';
                        } else if (this && this.id) {
                            who = '#' + this.id;
                        } else if (this && this.tagName) {
                            who = String(this.tagName).toLowerCase();
                        }
                        window.__fenListenerLog.push(String(type) + '@' + who);
                    } catch (err) { }
                    return original.apply(this, arguments);
                };
            } catch (err) {
                try { window.__fenListenerLog.push('hook-threw:' + err); } catch (e) { }
            }
        })();

        """;

    /// <summary>
    /// Returns the instrumented bundle for reCAPTCHA's script, or null to leave a
    /// request alone. Downloading it here keeps the page on the real bundle
    /// rather than a stale copy pinned to some older release.
    /// </summary>
    public static string TryInstrument(Uri requestUri)
    {
        if (requestUri == null)
        {
            return null;
        }

        var url = requestUri.AbsoluteUri;
        if (url.IndexOf(InstrumentedMarker, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(url, out var cached))
            {
                Console.WriteLine($"[captcha] script override served from cache ({++_serves} total)");
                return cached;
            }
        }

        string bundle;
        try
        {
            bundle = Http.GetStringAsync(requestUri).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[captcha] could not fetch bundle to instrument: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        var instrumented = Prologue + bundle;
        lock (Gate)
        {
            Cache[url] = instrumented;
        }

        Console.WriteLine($"[captcha] instrumented bundle ({bundle.Length} chars) for {requestUri.AbsolutePath}");
        return instrumented;
    }
}
