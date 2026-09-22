namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The realm side of Encrypted Media Extensions: the interfaces, the promises, the event
/// queue and the key status map. Everything that touches a key or a licence is a
/// <c>__fenEme*</c> native (see <see cref="BrowserScriptEngineRuntime"/>'s EME partial).
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private const string EmePrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            if (typeof g.EventTarget !== 'function' || typeof g.MediaKeys === 'function') return;
            if (typeof g.__fenEmeRequestAccess !== 'function') return;

            function defineInterface(name, ctor, base) {
                if (base !== null) ctor.prototype = Object.create((base || g.EventTarget).prototype);
                Object.defineProperty(ctor.prototype, 'constructor', { value: ctor, writable: true, configurable: true });
                Object.defineProperty(ctor.prototype, Symbol.toStringTag, { value: name, configurable: true });
                Object.defineProperty(ctor, 'name', { value: name, configurable: true });
                g[name] = ctor;
                return ctor;
            }
            function accessor(proto, name, get, set) {
                Object.defineProperty(proto, name, { get: get, set: set, enumerable: true, configurable: true });
            }
            function method(proto, name, fn, length) {
                if (length !== undefined) Object.defineProperty(fn, 'length', { value: length, configurable: true });
                Object.defineProperty(proto, name, { value: fn, writable: true, enumerable: true, configurable: true });
            }
            function handlerAttribute(proto, name) {
                var key = '_fenHandler_' + name;
                var type = name.slice(2);
                accessor(proto, name,
                    function () { return this[key] || null; },
                    function (v) {
                        this[key] = (typeof v === 'function' || (v !== null && typeof v === 'object')) ? v : null;
                        if (!this[key + '_installed'] && typeof this.addEventListener === 'function') {
                            this[key + '_installed'] = true;
                            var target = this;
                            this.addEventListener(type, function (ev) {
                                var h = target[key];
                                if (typeof h === 'function') h.call(target, ev);
                                else if (h && typeof h.handleEvent === 'function') h.handleEvent(ev);
                            });
                        }
                    });
            }
            // The media element event task source, so a session event queued while a
            // resource is being appended still runs before the playback events that
            // append causes. A plain timer loses that race.
            function queueTask(fn) {
                if (typeof g.__fenQueueMediaTask === 'function') g.__fenQueueMediaTask(null, fn);
                else g.setTimeout(fn, 0);
            }
            function bytesOf(v) {
                if (v === undefined || v === null) return null;
                try {
                    if (v instanceof ArrayBuffer) return new Uint8Array(v.slice(0));
                    if (typeof ArrayBuffer.isView === 'function' && ArrayBuffer.isView(v))
                        return new Uint8Array(v.buffer.slice(v.byteOffset, v.byteOffset + v.byteLength));
                } catch (e) {}
                return null;
            }
            function sameBytes(a, b) {
                if (!a || !b || a.length !== b.length) return false;
                for (var i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
                return true;
            }
            function toBuffer(bytes) {
                // Key IDs come back to script as ArrayBuffers, which is what the IDL says
                // and what the tests compare with.
                var copy = new Uint8Array(bytes.length);
                copy.set(bytes);
                return copy.buffer;
            }

            // ---- MediaEncryptedEvent and MediaKeyMessageEvent ----

            function MediaEncryptedEvent(type, init) {
                if (arguments.length < 1) throw new TypeError("Failed to construct 'MediaEncryptedEvent': 1 argument required.");
                var ev = new g.Event(type, init);
                Object.setPrototypeOf(ev, MediaEncryptedEvent.prototype);
                init = init || {};
                ev._initDataType = init.initDataType === undefined ? '' : String(init.initDataType);
                ev._initData = init.initData === undefined || init.initData === null ? null : init.initData;
                return ev;
            }
            defineInterface('MediaEncryptedEvent', MediaEncryptedEvent, g.Event);
            accessor(MediaEncryptedEvent.prototype, 'initDataType', function () { return this._initDataType || ''; });
            accessor(MediaEncryptedEvent.prototype, 'initData', function () {
                return this._initData === undefined ? null : this._initData;
            });

            function MediaKeyMessageEvent(type, init) {
                if (arguments.length < 1) throw new TypeError("Failed to construct 'MediaKeyMessageEvent': 1 argument required.");
                init = init || {};
                if (init.messageType === undefined || init.message === undefined)
                    throw new TypeError("Failed to construct 'MediaKeyMessageEvent': messageType and message are required.");
                var ev = new g.Event(type, init);
                Object.setPrototypeOf(ev, MediaKeyMessageEvent.prototype);
                ev._messageType = String(init.messageType);
                ev._message = init.message;
                return ev;
            }
            defineInterface('MediaKeyMessageEvent', MediaKeyMessageEvent, g.Event);
            accessor(MediaKeyMessageEvent.prototype, 'messageType', function () { return this._messageType; });
            accessor(MediaKeyMessageEvent.prototype, 'message', function () { return this._message; });

            // ---- MediaKeyStatusMap ----

            function MediaKeyStatusMap(session) {
                this._session = session;
            }
            defineInterface('MediaKeyStatusMap', MediaKeyStatusMap, null);
            MediaKeyStatusMap.prototype._entries = function () {
                var raw = this._session._closed ? [] : g.__fenEmeKeyStatuses(this._session._id);
                var out = [];
                for (var i = 0; i < raw.length; i++) out.push({ keyId: raw[i].keyId, status: raw[i].status });
                return out;
            };
            accessor(MediaKeyStatusMap.prototype, 'size', function () { return this._entries().length; });
            method(MediaKeyStatusMap.prototype, 'has', function (keyId) {
                var wanted = bytesOf(keyId);
                if (!wanted) throw new TypeError("Failed to execute 'has' on 'MediaKeyStatusMap': the key ID is not a BufferSource.");
                var entries = this._entries();
                for (var i = 0; i < entries.length; i++) if (sameBytes(entries[i].keyId, wanted)) return true;
                return false;
            }, 1);
            method(MediaKeyStatusMap.prototype, 'get', function (keyId) {
                var wanted = bytesOf(keyId);
                if (!wanted) throw new TypeError("Failed to execute 'get' on 'MediaKeyStatusMap': the key ID is not a BufferSource.");
                var entries = this._entries();
                for (var i = 0; i < entries.length; i++) if (sameBytes(entries[i].keyId, wanted)) return entries[i].status;
                return undefined;
            }, 1);
            method(MediaKeyStatusMap.prototype, 'forEach', function (callback, thisArg) {
                if (typeof callback !== 'function') throw new TypeError("Failed to execute 'forEach' on 'MediaKeyStatusMap': the callback is not a function.");
                var entries = this._entries();
                for (var i = 0; i < entries.length; i++) callback.call(thisArg, entries[i].status, toBuffer(entries[i].keyId), this);
            }, 1);
            function mapIterator(map, pick) {
                var entries = map._entries();
                var index = 0;
                var iterator = {
                    next: function () {
                        if (index >= entries.length) return { value: undefined, done: true };
                        return { value: pick(entries[index++]), done: false };
                    }
                };
                iterator[Symbol.iterator] = function () { return this; };
                return iterator;
            }
            method(MediaKeyStatusMap.prototype, 'keys', function () {
                return mapIterator(this, function (e) { return toBuffer(e.keyId); });
            }, 0);
            method(MediaKeyStatusMap.prototype, 'values', function () {
                return mapIterator(this, function (e) { return e.status; });
            }, 0);
            method(MediaKeyStatusMap.prototype, 'entries', function () {
                return mapIterator(this, function (e) { return [toBuffer(e.keyId), e.status]; });
            }, 0);
            Object.defineProperty(MediaKeyStatusMap.prototype, Symbol.iterator, {
                value: MediaKeyStatusMap.prototype.entries, writable: true, configurable: true
            });

            // ---- MediaKeySession ----

            var sessionsById = new Map();

            function MediaKeySession(id, sessionType) {
                var self = g.EventTarget.call(this) || this;
                self._id = id;
                self._sessionType = sessionType;
                self._closed = false;
                self._callable = true;
                self._statuses = new MediaKeyStatusMap(self);
                self._closedPromise = new Promise(function (resolve) { self._resolveClosed = resolve; });
                // EME §6.2: nothing is watching the closed promise until script asks for
                // it, and an unhandled rejection is not wanted - it only ever resolves.
                sessionsById.set(id, self);
                return self;
            }
            defineInterface('MediaKeySession', MediaKeySession);
            accessor(MediaKeySession.prototype, 'sessionId', function () {
                return this._closed ? (this._lastSessionId || '') : g.__fenEmeSessionId(this._id);
            });
            accessor(MediaKeySession.prototype, 'expiration', function () {
                return this._closed ? NaN : g.__fenEmeExpiration(this._id);
            });
            accessor(MediaKeySession.prototype, 'closed', function () { return this._closedPromise; });
            accessor(MediaKeySession.prototype, 'keyStatuses', function () { return this._statuses; });
            handlerAttribute(MediaKeySession.prototype, 'onkeystatuseschange');
            handlerAttribute(MediaKeySession.prototype, 'onmessage');

            function sessionCall(session, run) {
                // Every MediaKeySession method is "return a promise, run the algorithm,
                // reject the promise with whatever it throws".
                return new Promise(function (resolve, reject) {
                    if (session._closed) {
                        reject(new g.DOMException('The session is closed.', 'InvalidStateError'));
                        return;
                    }
                    var value;
                    try { value = run(); } catch (e) { reject(e); return; }
                    resolve(value);
                });
            }

            method(MediaKeySession.prototype, 'generateRequest', function (initDataType, initData) {
                var self = this;
                if (arguments.length < 2) return Promise.reject(new TypeError("Failed to execute 'generateRequest' on 'MediaKeySession': 2 arguments required."));
                var bytes = bytesOf(initData);
                if (bytes === null) return Promise.reject(new TypeError("Failed to execute 'generateRequest' on 'MediaKeySession': the initialization data is not a BufferSource."));
                var type = String(initDataType);
                // §6.4.3 steps 2-3: an empty type or empty data is a TypeError before the
                // implementation is asked anything.
                if (type.length === 0) return Promise.reject(new TypeError("Failed to execute 'generateRequest' on 'MediaKeySession': the initialization data type is empty."));
                if (bytes.length === 0) return Promise.reject(new TypeError("Failed to execute 'generateRequest' on 'MediaKeySession': the initialization data is empty."));
                return sessionCall(self, function () { g.__fenEmeGenerateRequest(self._id, type, bytes); });
            }, 2);

            method(MediaKeySession.prototype, 'load', function (sessionId) {
                var self = this;
                if (arguments.length < 1) return Promise.reject(new TypeError("Failed to execute 'load' on 'MediaKeySession': 1 argument required."));
                var id = String(sessionId);
                return sessionCall(self, function () { return g.__fenEmeLoad(self._id, id); });
            }, 1);

            method(MediaKeySession.prototype, 'update', function (response) {
                var self = this;
                if (arguments.length < 1) return Promise.reject(new TypeError("Failed to execute 'update' on 'MediaKeySession': 1 argument required."));
                var bytes = bytesOf(response);
                if (bytes === null) return Promise.reject(new TypeError("Failed to execute 'update' on 'MediaKeySession': the response is not a BufferSource."));
                return sessionCall(self, function () { g.__fenEmeUpdate(self._id, bytes); });
            }, 1);

            method(MediaKeySession.prototype, 'close', function () {
                var self = this;
                if (self._closed) return Promise.resolve();
                return sessionCall(self, function () { g.__fenEmeClose(self._id); });
            }, 0);

            method(MediaKeySession.prototype, 'remove', function () {
                var self = this;
                return sessionCall(self, function () { g.__fenEmeRemove(self._id); });
            }, 0);

            // ---- MediaKeys ----

            function MediaKeys(id, keySystem) {
                this._id = id;
                this._keySystem = keySystem;
            }
            defineInterface('MediaKeys', MediaKeys, null);
            method(MediaKeys.prototype, 'createSession', function (sessionType) {
                var type = sessionType === undefined ? 'temporary' : String(sessionType);
                var id = g.__fenEmeCreateSession(this._id, type);
                return new MediaKeySession(id, type);
            }, 0);
            method(MediaKeys.prototype, 'setServerCertificate', function (certificate) {
                var self = this;
                if (arguments.length < 1) return Promise.reject(new TypeError("Failed to execute 'setServerCertificate' on 'MediaKeys': 1 argument required."));
                var bytes = bytesOf(certificate);
                if (bytes === null) return Promise.reject(new TypeError("Failed to execute 'setServerCertificate' on 'MediaKeys': the certificate is not a BufferSource."));
                return new Promise(function (resolve, reject) {
                    try { resolve(g.__fenEmeSetServerCertificate(self._id, bytes)); } catch (e) { reject(e); }
                });
            }, 1);
            method(MediaKeys.prototype, 'getStatusForPolicy', function (policy) {
                var self = this;
                var hdcp = policy && policy.minHdcpVersion !== undefined ? String(policy.minHdcpVersion) : '';
                return new Promise(function (resolve, reject) {
                    try { resolve(g.__fenEmeGetStatusForPolicy(self._id, hdcp)); } catch (e) { reject(e); }
                });
            }, 0);

            // ---- MediaKeySystemAccess ----

            function MediaKeySystemAccess(keySystem, configuration) {
                this._keySystem = keySystem;
                this._configuration = configuration;
            }
            defineInterface('MediaKeySystemAccess', MediaKeySystemAccess, null);
            accessor(MediaKeySystemAccess.prototype, 'keySystem', function () { return this._keySystem; });
            method(MediaKeySystemAccess.prototype, 'getConfiguration', function () {
                // A fresh copy each time: the page must not be able to edit what it was granted.
                return JSON.parse(JSON.stringify(this._configuration));
            }, 0);
            method(MediaKeySystemAccess.prototype, 'createMediaKeys', function () {
                var self = this;
                return new Promise(function (resolve, reject) {
                    try {
                        resolve(new MediaKeys(g.__fenEmeCreateKeys(self._keySystem), self._keySystem));
                    } catch (e) { reject(e); }
                });
            }, 0);

            // ---- navigator.requestMediaKeySystemAccess ----

            function toCapabilities(list, what) {
                var out = [];
                if (list === undefined || list === null) return out;
                var array = Array.from(list);
                for (var i = 0; i < array.length; i++) {
                    var entry = array[i] || {};
                    if (typeof entry !== 'object') throw new TypeError(what + ' is not a dictionary.');
                    var capability = {
                        contentType: entry.contentType === undefined ? '' : String(entry.contentType),
                        robustness: entry.robustness === undefined ? '' : String(entry.robustness)
                    };
                    if (entry.encryptionScheme !== undefined && entry.encryptionScheme !== null)
                        capability.encryptionScheme = String(entry.encryptionScheme);
                    out.push(capability);
                }
                return out;
            }
            var REQUIREMENTS = ['required', 'optional', 'not-allowed'];
            function toRequirement(value, name) {
                if (value === undefined) return 'optional';
                var text = String(value);
                if (REQUIREMENTS.indexOf(text) < 0)
                    throw new TypeError("Failed to execute 'requestMediaKeySystemAccess' on 'Navigator': '" + text + "' is not a valid enum value of type MediaKeysRequirement.");
                return text;
            }

            function requestMediaKeySystemAccess(keySystem, supportedConfigurations) {
                return new Promise(function (resolve, reject) {
                    if (arguments.length < 0) { /* keeps the shape obvious */ }
                    var system = String(keySystem);
                    // §3.1.1 steps 1-2: an empty key system, or no configurations at all,
                    // is a TypeError before anything is asked of the implementation.
                    if (system.length === 0) {
                        reject(new TypeError("Failed to execute 'requestMediaKeySystemAccess' on 'Navigator': the key system is empty."));
                        return;
                    }
                    var configurations;
                    try {
                        configurations = Array.from(supportedConfigurations === undefined ? [] : supportedConfigurations);
                    } catch (e) {
                        reject(new TypeError("Failed to execute 'requestMediaKeySystemAccess' on 'Navigator': the configurations are not a sequence."));
                        return;
                    }
                    if (configurations.length === 0) {
                        reject(new TypeError("Failed to execute 'requestMediaKeySystemAccess' on 'Navigator': the configuration list is empty."));
                        return;
                    }

                    var candidates = [];
                    try {
                        for (var i = 0; i < configurations.length; i++) {
                            var source = configurations[i];
                            // WebIDL: each entry is a dictionary, and a value that is not
                            // an object cannot be converted to one.
                            if (source === undefined || source === null) source = {};
                            else if (typeof source !== 'object')
                                throw new TypeError("Failed to execute 'requestMediaKeySystemAccess' on 'Navigator': a configuration is not a dictionary.");
                            var candidate = {
                                label: source.label === undefined ? '' : String(source.label),
                                initDataTypes: source.initDataTypes === undefined || source.initDataTypes === null
                                    ? [] : Array.from(source.initDataTypes).map(String),
                                audioCapabilities: toCapabilities(source.audioCapabilities, 'An audio capability'),
                                videoCapabilities: toCapabilities(source.videoCapabilities, 'A video capability'),
                                distinctiveIdentifier: toRequirement(source.distinctiveIdentifier),
                                persistentState: toRequirement(source.persistentState)
                            };
                            if (source.sessionTypes !== undefined && source.sessionTypes !== null)
                                candidate.sessionTypes = Array.from(source.sessionTypes).map(String);
                            candidates.push(candidate);
                        }
                    } catch (e) {
                        reject(e instanceof TypeError ? e : new TypeError(String(e && e.message || e)));
                        return;
                    }

                    var supported = g.__fenEmeRequestAccess(system, JSON.stringify(candidates));
                    if (supported === null || supported === undefined) {
                        reject(new g.DOMException('No supported configuration for that key system.', 'NotSupportedError'));
                        return;
                    }

                    resolve(new MediaKeySystemAccess(system, JSON.parse(supported)));
                });
            }

            var navigatorObject = g.navigator;
            if (navigatorObject) {
                Object.defineProperty(navigatorObject, 'requestMediaKeySystemAccess', {
                    value: requestMediaKeySystemAccess, writable: true, enumerable: true, configurable: true
                });
                Object.defineProperty(requestMediaKeySystemAccess, 'length', { value: 2, configurable: true });
                Object.defineProperty(requestMediaKeySystemAccess, 'name', { value: 'requestMediaKeySystemAccess', configurable: true });
            }

            // ---- the element's MediaKeys ----

            var keysByElement = new WeakMap();

            g.__fenEmeElementMediaKeys = function (element) {
                return keysByElement.get(element) || null;
            };

            g.__fenEmeElementSetMediaKeys = function (element, mediaKeys) {
                return new Promise(function (resolve, reject) {
                    if (mediaKeys !== null && mediaKeys !== undefined && !(mediaKeys instanceof MediaKeys)) {
                        reject(new TypeError("Failed to execute 'setMediaKeys' on 'HTMLMediaElement': the argument is not a MediaKeys."));
                        return;
                    }
                    var keys = (mediaKeys === undefined || mediaKeys === null) ? null : mediaKeys;
                    if (keysByElement.get(element) === keys || (keys === null && !keysByElement.has(element))) {
                        resolve(undefined);
                        return;
                    }
                    try {
                        g.__fenEmeAttach(element, keys === null ? null : keys._id);
                    } catch (e) {
                        reject(e);
                        return;
                    }
                    if (keys === null) keysByElement.delete(element);
                    else keysByElement.set(element, keys);
                    resolve(undefined);
                });
            };

            // ---- hooks the engine calls ----

            g.__fenEmeOnMessage = function (sessionId, messageType, message) {
                var session = sessionsById.get(sessionId);
                if (!session) return;
                // EME §6.4.1 "Queue a 'message' Event": it is a task, so the promise the
                // algorithm returned settles first.
                var buffer = toBuffer(message);
                queueTask(function () {
                    if (session._closed) return;
                    var ev = new MediaKeyMessageEvent('message', { messageType: messageType, message: buffer });
                    try { session.dispatchEvent(ev); } catch (e) {}
                });
            };

            g.__fenEmeOnKeyStatusesChange = function (sessionId) {
                var session = sessionsById.get(sessionId);
                if (!session) return;
                queueTask(function () {
                    try { session.dispatchEvent(new g.Event('keystatuseschange')); } catch (e) {}
                });
            };

            g.__fenEmeOnClosed = function (sessionId, reason) {
                var session = sessionsById.get(sessionId);
                if (!session) return;
                session._lastSessionId = session.sessionId;
                session._closed = true;
                sessionsById.delete(sessionId);
                if (session._resolveClosed) session._resolveClosed(reason);
            };

            g.__fenEmeFireEncrypted = function (element, initDataType, initData) {
                if (!element) return;
                var ev = new MediaEncryptedEvent('encrypted', { initDataType: initDataType, initData: toBuffer(initData) });
                try { element.dispatchEvent(ev); } catch (e) {}
            };
        })();
        """;
}
