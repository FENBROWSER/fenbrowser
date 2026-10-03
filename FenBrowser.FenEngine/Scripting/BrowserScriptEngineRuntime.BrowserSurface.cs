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

                    // Web Authentication Level 3. When this process has an authenticator
                    // (Windows Hello, directly or through the broker) a publicKey get() or
                    // create() runs a real ceremony through __fenWebAuthn: BufferSources go
                    // over as base64url and the credential comes back as ArrayBuffers on a
                    // PublicKeyCredential. Without one this is a client whose ceremonies have
                    // nothing to talk to: the platform authenticator and conditional
                    // mediation report unavailable (5.1.7, 5.1.8) and a ceremony ends in
                    // NotAllowedError (5.1.3 / 5.1.4.1). A conditional get() waits for an
                    // autofill pick FenBrowser does not offer, until its signal aborts. Sites
                    // gate their passkey and federated sign-in block on PublicKeyCredential
                    // existing (github.com's login).
                    {
                        function webAuthnError(name, message) {
                            if (typeof DOMException === 'function') return new DOMException(message, name);
                            var error = new Error(message);
                            error.name = name;
                            return error;
                        }
                        function hasAuthenticator() {
                            return typeof __fenWebAuthnAvailable === 'function' && !!__fenWebAuthnAvailable();
                        }
                        function bytesOf(source) {
                            if (source instanceof ArrayBuffer) return new Uint8Array(source);
                            if (source && ArrayBuffer.isView(source)) return new Uint8Array(source.buffer, source.byteOffset, source.byteLength);
                            throw new TypeError("Failed to execute 'credentials': a BufferSource is required.");
                        }
                        function toB64u(source) {
                            var bytes = bytesOf(source), binary = '';
                            for (var i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
                            return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
                        }
                        function fromB64u(text) {
                            if (text === null || text === undefined) return null;
                            var s = String(text).replace(/-/g, '+').replace(/_/g, '/');
                            while (s.length % 4) s += '=';
                            var binary = atob(s), bytes = new Uint8Array(binary.length);
                            for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
                            return bytes.buffer;
                        }
                        function descriptors(list) {
                            var out = [];
                            if (!list) return out;
                            for (var i = 0; i < list.length; i++) {
                                out.push({ id: toB64u(list[i].id), transports: Array.isArray(list[i].transports) ? list[i].transports.slice() : [] });
                            }
                            return out;
                        }
                        function AuthenticatorResponse() { throw new TypeError('Illegal constructor'); }
                        function AuthenticatorAssertionResponse() { throw new TypeError('Illegal constructor'); }
                        function AuthenticatorAttestationResponse() { throw new TypeError('Illegal constructor'); }
                        AuthenticatorAssertionResponse.prototype = Object.create(AuthenticatorResponse.prototype);
                        AuthenticatorAttestationResponse.prototype = Object.create(AuthenticatorResponse.prototype);
                        var PublicKeyCredential = typeof globalThis.PublicKeyCredential === 'function'
                            ? globalThis.PublicKeyCredential
                            : function PublicKeyCredential() { throw new TypeError('Illegal constructor'); };

                        function makeCredential(kind, r) {
                            var response = Object.create(kind === 'create'
                                ? AuthenticatorAttestationResponse.prototype
                                : AuthenticatorAssertionResponse.prototype);
                            response.clientDataJSON = fromB64u(r.clientDataJson);
                            if (kind === 'create') {
                                response.attestationObject = fromB64u(r.attestationObject);
                                var transports = (r.transports || []).slice();
                                var authData = r.authenticatorData;
                                response.getTransports = function () { return transports.slice(); };
                                response.getAuthenticatorData = function () { return fromB64u(authData); };
                                response.getPublicKey = function () { return null; };
                                response.getPublicKeyAlgorithm = function () { return -7; };
                            } else {
                                response.authenticatorData = fromB64u(r.authenticatorData);
                                response.signature = fromB64u(r.signature);
                                response.userHandle = r.userHandle ? fromB64u(r.userHandle) : null;
                            }
                            var credential = Object.create(PublicKeyCredential.prototype);
                            credential.id = r.credentialId;
                            credential.rawId = fromB64u(r.credentialId);
                            credential.type = 'public-key';
                            credential.authenticatorAttachment = r.authenticatorAttachment || null;
                            credential.response = response;
                            credential.getClientExtensionResults = function () { return {}; };
                            credential.toJSON = function () {
                                var json = { id: r.credentialId, rawId: r.credentialId, type: 'public-key',
                                    authenticatorAttachment: credential.authenticatorAttachment, clientExtensionResults: {},
                                    response: { clientDataJSON: r.clientDataJson } };
                                if (kind === 'create') {
                                    json.response.attestationObject = r.attestationObject;
                                    json.response.authenticatorData = r.authenticatorData;
                                    json.response.transports = (r.transports || []).slice();
                                } else {
                                    json.response.authenticatorData = r.authenticatorData;
                                    json.response.signature = r.signature;
                                    json.response.userHandle = r.userHandle || null;
                                }
                                return json;
                            };
                            return credential;
                        }

                        function requestFor(kind, pk) {
                            if (kind === 'create') {
                                var rp = pk.rp || {}, user = pk.user || {}, selection = pk.authenticatorSelection || {};
                                var algorithms = [];
                                (pk.pubKeyCredParams || []).forEach(function (p) {
                                    if (p && p.type === 'public-key' && typeof p.alg === 'number') algorithms.push(p.alg);
                                });
                                return {
                                    rpId: rp.id, rpName: rp.name, challenge: toB64u(pk.challenge),
                                    userId: toB64u(user.id), userName: user.name, userDisplayName: user.displayName,
                                    algorithms: algorithms, excludeCredentials: descriptors(pk.excludeCredentials),
                                    authenticatorAttachment: selection.authenticatorAttachment,
                                    residentKey: selection.residentKey || (selection.requireResidentKey ? 'required' : undefined),
                                    userVerification: selection.userVerification || 'preferred',
                                    attestation: pk.attestation || 'none', timeoutMs: pk.timeout | 0
                                };
                            }
                            return {
                                rpId: pk.rpId, challenge: toB64u(pk.challenge), timeoutMs: pk.timeout | 0,
                                userVerification: pk.userVerification || 'preferred',
                                allowCredentials: descriptors(pk.allowCredentials)
                            };
                        }

                        function publicKeyCeremony(options, kind) {
                            var signal = options && options.signal;
                            function abortReason() {
                                return signal.reason !== undefined ? signal.reason : webAuthnError('AbortError', 'The operation was aborted.');
                            }
                            if (signal && signal.aborted) return Promise.reject(abortReason());
                            if (!globalThis.isSecureContext) {
                                return Promise.reject(webAuthnError('SecurityError', 'The operation is insecure.'));
                            }
                            if (kind === 'get' && options.mediation === 'conditional') {
                                return new Promise(function (resolve, reject) {
                                    if (signal && typeof signal.addEventListener === 'function') {
                                        signal.addEventListener('abort', function () { reject(abortReason()); });
                                    }
                                });
                            }
                            if (!hasAuthenticator()) {
                                return Promise.reject(webAuthnError('NotAllowedError',
                                    'The operation either timed out or was not allowed. See: https://www.w3.org/TR/webauthn-2/#sctn-privacy-considerations-client.'));
                            }
                            var request;
                            try { request = requestFor(kind, options.publicKey); }
                            catch (e) { return Promise.reject(e); }
                            var ceremony = __fenWebAuthn(kind, JSON.stringify(request)).then(function (json) {
                                var r = JSON.parse(json);
                                if (r.errorName) {
                                    if (r.errorName === 'TypeError') throw new TypeError(r.errorMessage || 'Invalid options.');
                                    throw webAuthnError(r.errorName, r.errorMessage || r.errorName);
                                }
                                return makeCredential(kind, r);
                            });
                            if (!signal || typeof signal.addEventListener !== 'function') return ceremony;
                            return new Promise(function (resolve, reject) {
                                signal.addEventListener('abort', function () { reject(abortReason()); });
                                ceremony.then(resolve, reject);
                            });
                        }
                        navigatorFillers.credentials = {
                            get: function (options) {
                                if (options && options.publicKey) return publicKeyCeremony(options, 'get');
                                return Promise.resolve(null);
                            },
                            store: function (credential) { return Promise.resolve(credential); },
                            create: function (options) {
                                if (options && options.publicKey) return publicKeyCeremony(options, 'create');
                                return Promise.resolve(null);
                            },
                            preventSilentAccess: function () { return Promise.resolve(); }
                        };

                        PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable = function () {
                            return hasAuthenticator() && typeof __fenWebAuthnUvpaa === 'function'
                                ? __fenWebAuthnUvpaa()
                                : Promise.resolve(false);
                        };
                        PublicKeyCredential.isConditionalMediationAvailable = function () { return Promise.resolve(false); };
                        var webAuthnGlobals = {
                            PublicKeyCredential: PublicKeyCredential,
                            AuthenticatorResponse: AuthenticatorResponse,
                            AuthenticatorAssertionResponse: AuthenticatorAssertionResponse,
                            AuthenticatorAttestationResponse: AuthenticatorAttestationResponse
                        };
                        Object.keys(webAuthnGlobals).forEach(function (name) {
                            if (typeof globalThis[name] === 'undefined' || name === 'PublicKeyCredential') {
                                Object.defineProperty(globalThis, name, {
                                    value: webAuthnGlobals[name], writable: true, configurable: true, enumerable: false
                                });
                            }
                        });
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
                        // HTML "start an idle period": the deadline runs from when the
                        // idle period starts - when the callback is invoked - and is at
                        // most 50 ms away. Measuring from the request instead handed every
                        // callback that waited behind a busy page timeRemaining() == 0, so
                        // idle-driven schedulers ran nothing and re-queued forever.
                        globalThis.requestIdleCallback = function (callback, options) {
                            var timeout = options && typeof options.timeout === 'number' ? Math.max(0, options.timeout) : 0;
                            var requested = Date.now();
                            return setTimeout(function () {
                                var deadline = performance.now() + 50;
                                callback({
                                    didTimeout: timeout > 0 && Date.now() - requested >= timeout,
                                    timeRemaining: function () { return Math.max(0, deadline - performance.now()); }
                                });
                            }, 0);
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
