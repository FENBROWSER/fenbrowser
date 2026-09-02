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
            if (window.__fenListenerLog) { return; }
            window.__fenListenerLog = [];
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
