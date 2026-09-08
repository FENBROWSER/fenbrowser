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
            if (window.__fenPrologueInstalled) { return; }

            // In a worker there is no document, and everything below that
            // touches one has to be skipped - but bailing out entirely left the
            // worker realm with no error capture at all, which is exactly where
            // the trail goes cold: the anchor's message reaches the worker, its
            // handler runs, and no reply is ever produced. Install the error
            // hooks there and report them back over the worker's own channel.
            if (typeof document === 'undefined' || !document) { return; }

            window.__fenPrologueInstalled = 1;
            if (!window.__fenListenerLog) { window.__fenListenerLog = []; }

            // The bundle wraps its own start-up in try/catch and reports failures
            // by calling console methods, so a fatal error looks like silence from
            // the outside: no listeners, no requests, a widget stuck loading.
            // Record what actually threw, in the realm where it threw.
            window.__fenErrorLog = [];

            // Chrome removes recaptcha-checkbox-disabled and
            // recaptcha-checkbox-loading 98ms after the anchor receives its own
            // setup message, before any of the heavy work runs. Ours never
            // removes them. Sampling the class from outside cannot say when it
            // should have changed, because the engine lock keeps outside probes
            // from running at all for seconds at a time - so record the
            // transitions from inside the frame, on the frame's own clock.
            window.__fenClassLog = [];
            (function () {
                var started = Date.now();
                var last = null;
                var tick = function () {
                    try {
                        var el = document.getElementById('recaptcha-anchor');
                        if (el && el.className !== last) {
                            last = el.className;
                            if (window.__fenClassLog.length < 40) {
                                window.__fenClassLog.push((Date.now() - started) + 'ms ' + last);
                            }
                        }
                    } catch (e) { }
                };
                try {
                    setInterval(tick, 50);
                    tick();
                } catch (e) { }
            })();

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
            // Whether a frame that goes quiet asked to speak and was not
            // heard, or never asked, is the whole question -- and from outside
            // the two look the same. Record the calls as the bundle makes them.
            window.__fenPostLog = [];
            var logPost = function (kind, data, origin, transfer) {
                try {
                    if (window.__fenPostLog.length >= 60) { return; }
                    window.__fenPostLog.push(stamp() + ' ' + kind +
                        ' data=' + (typeof data === 'string'
                            ? JSON.stringify(data.slice(0, 40))
                            : (data && typeof data === 'object' ? 'object' : typeof data)) +
                        ' origin=' + String(origin) +
                        ' transfer=' + ((transfer && transfer.length) || 0));
                } catch (e) { }
            };
            try {
                var selfPost = window.postMessage;
                if (typeof selfPost === 'function') {
                    window.postMessage = function (d, o, t) {
                        logPost('self', d, o, t);
                        return selfPost.apply(this, arguments);
                    };
                }
            } catch (e) { note('selfpost-hook-threw:' + e); }
            try {
                var up = window.parent;
                if (up && up !== window && typeof up.postMessage === 'function') {
                    var upPost = up.postMessage;
                    up.postMessage = function (d, o, t) {
                        logPost('parent', d, o, t);
                        return upPost.apply(this, arguments);
                    };
                }
            } catch (e) { note('parentpost-hook-threw:' + e); }
            // Posts into a child go through iframe.contentWindow, which the hooks
            // above never see: a parent that posted setup into a frame and a
            // parent that never posted at all left an identical record. Frames
            // appear after this prologue runs, so hook each one the first time a
            // sweep finds it, and keep sweeping.
            try {
                // A hook that silently fails to install would make "the parent
                // never posted" and "we could not watch" look the same, so say
                // which frames could not be hooked and why.
                var hookReported = {};
                var reportOnce = function (label, why) {
                    if (hookReported[label + why]) { return; }
                    hookReported[label + why] = 1;
                    note('childpost-hook ' + label + ': ' + why);
                };
                var hookChildPost = function (frame, label) {
                    var win = null;
                    try { win = frame.contentWindow; } catch (e) { reportOnce(label, 'contentWindow threw ' + e); return; }
                    if (!win) { reportOnce(label, 'no contentWindow'); return; }
                    if (win.__fenChildPostHooked) { return; }
                    if (typeof win.postMessage !== 'function') { reportOnce(label, 'no postMessage'); return; }
                    try {
                        var childPost = win.postMessage;
                        win.postMessage = function (d, o, t) {
                            logPost('child:' + label, d, o, t);
                            return childPost.apply(this, arguments);
                        };
                        if (win.postMessage === childPost) { reportOnce(label, 'postMessage not writable'); return; }
                        win.__fenChildPostHooked = 1;
                        if (!win.__fenChildPostHooked) { reportOnce(label, 'hooked, but the facade drops expandos'); }
                        reportOnce(label, 'hooked');
                    } catch (e) { reportOnce(label, 'hook threw ' + e); }
                };
                var sweepFrames = function () {
                    try {
                        var fs = document.getElementsByTagName('iframe');
                        for (var i = 0; i < fs.length; i++) {
                            hookChildPost(fs[i], fs[i].name || fs[i].id || ('iframe#' + i));
                        }
                    } catch (e) { }
                };
                sweepFrames();
                setInterval(sweepFrames, 250);
            } catch (e) { note('childsweep-threw:' + e); }
            try {
                if (typeof MessagePort === 'function' && MessagePort.prototype) {
                    // "a port was written to and never answered" is only
                    // actionable once you know which port. Number them, and
                    // carry the number on every event.
                    window.__fenPortSeq = 0;
                    var portId = function (p) {
                        try {
                            if (p.__fenPortId === undefined) {
                                p.__fenPortId = ++window.__fenPortSeq;
                            }
                            return p.__fenPortId;
                        } catch (e) { return '?'; }
                    };
                    window.__fenPortId = portId;

                    var portPost = MessagePort.prototype.postMessage;
                    MessagePort.prototype.postMessage = function (d, t) {
                        logPost('port#' + portId(this), d, '-', t);
                        return portPost.apply(this, arguments);
                    };
                    var portStart = MessagePort.prototype.start;
                    MessagePort.prototype.start = function () {
                        logPost('port-start#' + portId(this), '', '-', null);
                        return portStart.apply(this, arguments);
                    };

                    // Outgoing port traffic was recorded; incoming was not, so
                    // a channel that is written to and never answered looked
                    // exactly like one that is working. The widget goes into
                    // its loading state while it waits for a reply, so whether
                    // one ever arrives is the whole question.
                    var portAdd = MessagePort.prototype.addEventListener;
                    if (typeof portAdd === 'function') {
                        MessagePort.prototype.addEventListener = function (type, fn, opts) {
                            if (type === 'message' && typeof fn === 'function') {
                                var self = this;
                                var wrapped = function (ev) {
                                    logPost('port-recv#' + portId(self), ev && ev.data, '-', null);
                                    return fn.apply(this, arguments);
                                };
                                return portAdd.call(this, type, wrapped, opts);
                            }
                            return portAdd.apply(this, arguments);
                        };
                    }

                    try {
                        Object.defineProperty(MessagePort.prototype, 'onmessage', {
                            configurable: true,
                            set: function (fn) {
                                var self = this;
                                if (typeof fn === 'function') {
                                    portAdd.call(self, 'message', function (ev) {
                                        logPost('port-recv#' + portId(self), ev && ev.data, '-', null);
                                        return fn.apply(self, arguments);
                                    });
                                    try { self.start(); } catch (e) { }
                                }
                            },
                            get: function () { return undefined; }
                        });
                    } catch (e) { note('portonmessage-hook-threw:' + e); }
                }
            } catch (e) { note('portpost-hook-threw:' + e); }
            try {
                if (typeof MessageChannel === 'function') {
                    var realChannel = MessageChannel;
                    window.MessageChannel = function () {
                        logPost('new-MessageChannel', '', '-', null);
                        return new realChannel();
                    };
                }
            } catch (e) { note('channel-hook-threw:' + e); }

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
            // The handshake only accepts a message whose source is the very
            // contentWindow of the frame it expects, so identity of that object
            // matters as much as delivery. Record what each message actually
            // carries.
            try {
                window.addEventListener('message', function (ev) {
                    var match = 'no-iframe-match';
                    try {
                        var fs = document.getElementsByTagName('iframe');
                        for (var i = 0; i < fs.length; i++) {
                            if (fs[i].contentWindow === ev.source) {
                                match = (fs[i].id || ('iframe#' + i));
                                break;
                            }
                        }
                        if (match === 'no-iframe-match' && parent && parent !== window && parent.document) {
                            var pfs = parent.document.getElementsByTagName('iframe');
                            for (var j = 0; j < pfs.length; j++) {
                                if (pfs[j].contentWindow === ev.source) {
                                    match = 'parent:' + (pfs[j].id || pfs[j].name || ('iframe#' + j));
                                    break;
                                }
                            }
                        }
                    } catch (e) { match = 'compare-threw'; }
                    var portIds = '';
                    try {
                        if (ev.ports && ev.ports.length && window.__fenPortId) {
                            for (var pi = 0; pi < ev.ports.length; pi++) {
                                portIds += (pi ? ',' : '') + '#' + window.__fenPortId(ev.ports[pi]);
                            }
                        }
                    } catch (e) { portIds = 'id-threw'; }
                    note('msg data=' + String(ev.data).slice(0, 20) +
                        ' ports=' + (ev.ports ? ev.ports.length : 'none') + portIds +
                        ' source=' + (ev.source === window ? 'self' :
                            (ev.source === null ? 'null' :
                                (ev.source === undefined ? 'undefined' : match))) +
                        ' origin=' + ev.origin);
                });
            } catch (e) { note('message-probe-threw:' + e); }
            try {
                if (typeof Worker === 'function' && Worker.prototype) {
                    var workerPost = Worker.prototype.postMessage;
                    Worker.prototype.postMessage = function (d, t) {
                        var ids = '';
                        try {
                            var list = (t && t.length) ? t : (d && d.length !== undefined && d.constructor === Array ? [] : []);
                            for (var wi = 0; wi < list.length; wi++) {
                                if (window.__fenPortId && list[wi] instanceof MessagePort) {
                                    ids += (wi ? ',' : '') + '#' + window.__fenPortId(list[wi]);
                                }
                            }
                        } catch (e) { ids = '?'; }
                        logPost('worker-post' + (ids ? ' ports=' + ids : ''), d, '-', t);
                        return workerPost.apply(this, arguments);
                    };

                    var workerAdd = Worker.prototype.addEventListener;
                    Worker.prototype.addEventListener = function (type, fn, opts) {
                        if (type === 'message' && typeof fn === 'function') {
                            var wrapped = function (ev) {
                                var ids = '';
                                try {
                                    if (ev.ports && ev.ports.length && window.__fenPortId) {
                                        for (var wj = 0; wj < ev.ports.length; wj++) {
                                            ids += (wj ? ',' : '') + '#' + window.__fenPortId(ev.ports[wj]);
                                        }
                                    }
                                } catch (e) { ids = '?'; }
                                logPost('worker-recv' + (ids ? ' ports=' + ids : ''), ev && ev.data, '-', null);
                                return fn.apply(this, arguments);
                            };
                            return workerAdd.call(this, type, wrapped, opts);
                        }
                        return workerAdd.apply(this, arguments);
                    };
                }
            } catch (e) { note('worker-probe-threw:' + e); }

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
