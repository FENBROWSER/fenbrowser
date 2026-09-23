using System;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Window and navigator members that every mainstream browser exposes and
/// that feature-detection and fingerprinting scripts read first. Each is
/// either the real value (the origin, the screen position) or the answer a
/// browser gives when a capability is present but withheld (permission
/// prompts, empty device lists), never a claim of a capability the engine
/// does not have.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private void InstallFenJsBrowserSurfaceFillers()
    {
        try
        {
            // The navigator members are built in script and installed from
            // here as stored host properties: a script-side assignment to a
            // host object is recorded as page evidence by the missing-API
            // tracker, and the engine's own setup is not page evidence.
            var fillers = EvaluateWithFenJsRaw(
                """
                (function () {
                    if (typeof document === 'undefined') {
                        return undefined;
                    }

                    var nav = globalThis.navigator;
                    if (!nav) {
                        return undefined;
                    }

                    var navigatorFillers = {};

                    function notAllowed(message) {
                        var error = new Error(message || 'Permission denied');
                        error.name = 'NotAllowedError';
                        return Promise.reject(error);
                    }

                    // HTML dom-navigator-plugins: the PDF viewer set that Chromium
                    // reports; the same five names in the same order.
                    function makeMime(type, suffixes) {
                        return { type: type, suffixes: suffixes, description: 'Portable Document Format', enabledPlugin: null };
                    }
                    function makePlugin(name) {
                        var mimes = [makeMime('application/pdf', 'pdf'), makeMime('text/pdf', 'pdf')];
                        var plugin = { name: name, description: 'Portable Document Format', filename: 'internal-pdf-viewer', length: mimes.length };
                        for (var i = 0; i < mimes.length; i++) { mimes[i].enabledPlugin = plugin; plugin[i] = mimes[i]; }
                        plugin.item = function (index) { return this[index] || null; };
                        plugin.namedItem = function (type) {
                            for (var j = 0; j < this.length; j++) { if (this[j].type === type) return this[j]; }
                            return null;
                        };
                        plugin[Symbol.iterator] = function () { return mimes[Symbol.iterator](); };
                        return plugin;
                    }
                    var pluginNames = ['PDF Viewer', 'Chrome PDF Viewer', 'Chromium PDF Viewer', 'Microsoft Edge PDF Viewer', 'WebKit built-in PDF'];
                    var plugins = { length: pluginNames.length };
                    for (var p = 0; p < pluginNames.length; p++) {
                        plugins[p] = makePlugin(pluginNames[p]);
                        plugins[pluginNames[p]] = plugins[p];
                    }
                    plugins.item = function (index) { return this[index] || null; };
                    plugins.namedItem = function (name) { return this[name] || null; };
                    plugins.refresh = function () {};
                    plugins[Symbol.iterator] = function () {
                        var list = [];
                        for (var k = 0; k < this.length; k++) list.push(this[k]);
                        return list[Symbol.iterator]();
                    };
                    var mimeTypes = { length: 2 };
                    mimeTypes[0] = plugins[0][0];
                    mimeTypes[1] = plugins[0][1];
                    mimeTypes['application/pdf'] = mimeTypes[0];
                    mimeTypes['text/pdf'] = mimeTypes[1];
                    mimeTypes.item = function (index) { return this[index] || null; };
                    mimeTypes.namedItem = function (type) { return this[type] || null; };
                    mimeTypes[Symbol.iterator] = function () { return [this[0], this[1]][Symbol.iterator](); };
                    navigatorFillers.plugins = plugins;
                    navigatorFillers.mimeTypes = mimeTypes;
                    navigatorFillers.pdfViewerEnabled = true;

                    navigatorFillers.productSub = '20030107';
                    navigatorFillers.vendorSub = '';
                    navigatorFillers.javaEnabled = function () { return false; };

                    // Permissions: every query answers "prompt", which is the
                    // state before a user has been asked.
                    {
                        navigatorFillers.permissions = {
                            query: function (descriptor) {
                                var name = descriptor && descriptor.name;
                                if (typeof name !== 'string') {
                                    return Promise.reject(new TypeError("Failed to execute 'query' on 'Permissions': required member name is undefined."));
                                }
                                return Promise.resolve({ name: name, state: 'prompt', onchange: null, addEventListener: function () {}, removeEventListener: function () {} });
                            }
                        };
                    }

                    // Geolocation: present, and the position request is denied
                    // the way a declined prompt denies it.
                    {
                        var denied = function (error) {
                            if (typeof error === 'function') {
                                setTimeout(function () {
                                    error({ code: 1, message: 'User denied Geolocation', PERMISSION_DENIED: 1, POSITION_UNAVAILABLE: 2, TIMEOUT: 3 });
                                }, 0);
                            }
                        };
                        navigatorFillers.geolocation = {
                            getCurrentPosition: function (success, error) { denied(error); },
                            watchPosition: function (success, error) { denied(error); return 1; },
                            clearWatch: function () {}
                        };
                    }

                    {
                        navigatorFillers.clipboard = {
                            readText: function () { return notAllowed('Read permission denied.'); },
                            read: function () { return notAllowed('Read permission denied.'); },
                            writeText: function () { return Promise.resolve(); },
                            write: function () { return Promise.resolve(); },
                            addEventListener: function () {},
                            removeEventListener: function () {}
                        };
                    }

                    {
                        navigatorFillers.credentials = {
                            get: function () { return Promise.resolve(null); },
                            store: function (credential) { return Promise.resolve(credential); },
                            create: function () { return Promise.resolve(null); },
                            preventSilentAccess: function () { return Promise.resolve(); }
                        };
                    }

                    {
                        navigatorFillers.locks = {
                            request: function (name, options, callback) {
                                if (typeof options === 'function') { callback = options; options = undefined; }
                                var mode = options && options.mode === 'shared' ? 'shared' : 'exclusive';
                                return Promise.resolve().then(function () { return callback({ name: String(name), mode: mode }); });
                            },
                            query: function () { return Promise.resolve({ held: [], pending: [] }); }
                        };
                    }

                    {
                        var battery = { charging: true, chargingTime: 0, dischargingTime: Infinity, level: 1,
                            onchargingchange: null, onchargingtimechange: null, ondischargingtimechange: null, onlevelchange: null,
                            addEventListener: function () {}, removeEventListener: function () {} };
                        navigatorFillers.getBattery = function () { return Promise.resolve(battery); };
                    }

                    {
                        navigatorFillers.userActivation = { isActive: false, hasBeenActive: false };
                    }

                    {
                        navigatorFillers.scheduling = { isInputPending: function () { return false; } };
                    }

                    // Prioritized Task Scheduling: postTask runs the callback as a
                    // task and yields its result; priorities collapse to ordering.
                    if (typeof globalThis.scheduler === 'undefined') {
                        globalThis.scheduler = {
                            postTask: function (callback, options) {
                                var delay = options && typeof options.delay === 'number' ? Math.max(0, options.delay) : 0;
                                return new Promise(function (resolve, reject) {
                                    setTimeout(function () {
                                        try { resolve(callback()); } catch (e) { reject(e); }
                                    }, delay);
                                });
                            },
                            yield: function () {
                                return new Promise(function (resolve) { setTimeout(resolve, 0); });
                            }
                        };
                    }

                    // HTML inert: reflected, and honoured by hit testing only when
                    // the attribute is present, which is all this exposes.
                    if (typeof globalThis.HTMLElement === 'function' &&
                        !Object.getOwnPropertyDescriptor(globalThis.HTMLElement.prototype, 'inert')) {
                        Object.defineProperty(globalThis.HTMLElement.prototype, 'inert', {
                            get: function () {
                                try { return typeof this.hasAttribute === 'function' && this.hasAttribute('inert'); }
                                catch (e) { return false; }
                            },
                            set: function (value) {
                                try {
                                    if (typeof this.setAttribute !== 'function') return;
                                    if (value) this.setAttribute('inert', ''); else this.removeAttribute('inert');
                                } catch (e) {}
                            },
                            configurable: true,
                            enumerable: true
                        });
                    }

                    if (typeof globalThis.requestIdleCallback !== 'function') {
                        globalThis.requestIdleCallback = function (callback, options) {
                            var timeout = options && typeof options.timeout === 'number' ? Math.max(0, options.timeout) : 0;
                            var start = Date.now();
                            return setTimeout(function () {
                                callback({
                                    didTimeout: timeout > 0 && Date.now() - start >= timeout,
                                    timeRemaining: function () { return Math.max(0, 50 - (Date.now() - start)); }
                                });
                            }, 1);
                        };
                        globalThis.cancelIdleCallback = function (handle) { clearTimeout(handle); };
                    }

                    if (typeof globalThis.onerror === 'undefined') globalThis.onerror = null;
                    if (typeof globalThis.origin !== 'string' && globalThis.location) globalThis.origin = String(globalThis.location.origin || '');
                    ['screenX', 'screenY', 'screenLeft', 'screenTop'].forEach(function (name) {
                        if (typeof globalThis[name] !== 'number') globalThis[name] = 0;
                    });

                    // performance.memory is Chromium's; its three numbers are
                    // read as a liveness check far more often than as sizes.
                    if (globalThis.performance && typeof globalThis.performance.memory === 'undefined') {
                        var heapUsed = 28 * 1024 * 1024;
                        Object.defineProperty(globalThis.performance, 'memory', {
                            get: function () {
                                heapUsed += 65536;
                                return { jsHeapSizeLimit: 4294705152, totalJSHeapSize: heapUsed + 18 * 1024 * 1024, usedJSHeapSize: heapUsed };
                            },
                            configurable: true
                        });
                    }

                    return navigatorFillers;
                })();
                """);

            if (fillers.Tag == JsValueTag.Object &&
                _interpreter.TryReadGlobalValue("navigator", out var navigatorValue) &&
                TryResolveHostObject(navigatorValue, out var navigatorHost) &&
                navigatorHost != null)
            {
                var fillerObject = _interpreter.Heap.GetObject(fillers.AsObjectHandle());
                foreach (var property in fillerObject.EnumerateOwnProperties())
                {
                    SetStoredHostProperty(navigatorHost, property.Key, property.Value.Value);
                }
            }
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Debug(
                $"[FenJsBridge] Browser surface fillers failed: {ex.Message}",
                FenBrowser.Core.Logging.LogCategory.JavaScript);
        }
    }
}
