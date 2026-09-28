using System;
using System.Collections.Generic;
using FenBrowser.Core.Security;
using FenBrowser.FenEngine.Security;
using FenBrowser.Js.Runtime;
using FenBrowser.Media;
using FenBrowser.Media.Capture;
using FenBrowser.Media.Streams;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// <c>navigator.mediaDevices.getUserMedia()</c> and the capture half of
/// <c>enumerateDevices()</c> (mediacapture-main 9-10) over <see cref="CaptureDevices"/>.
/// A captured track carries its device's audio pipe or picture source, the same sources a
/// Web Audio destination or a canvas capture carries, so it plays anywhere they do.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    /// <summary>Open devices, by the key their track carries.</summary>
    private readonly Dictionary<string, ICaptureSession> _captureSessions = new(StringComparer.Ordinal);

    /// <summary>Captured cameras' pictures, by the key their track carries (read by the stream sync).</summary>
    private readonly Dictionary<string, VideoTrackSource> _captureVideoSources = new(StringComparer.Ordinal);

    /// <summary>
    /// mediacapture-main 9.2.1 "device information can be exposed": once this document has
    /// been given a device of a kind, enumerateDevices() names the devices of that kind.
    /// A granted permission alone does not; capture has to have started.
    /// </summary>
    private readonly HashSet<MediaTrackKind> _captureKindsExposed = new();

    private bool HasCapturePermission()
    {
        if (CaptureDevices.AutoGrant)
        {
            return true;
        }

        var origin = PermissionStore.NormalizeOrigin(TryResolveCurrentOrigin(null));
        return origin != null && PermissionStore.Instance.GetState(origin, JsPermissions.Camera) == PermissionState.Granted;
    }

    private JsValue CaptureDeviceRecord(CaptureDeviceInfo device, bool exposed) =>
        _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["deviceId"] = JsValue.FromString(exposed ? OriginScopedId("capture", device.DeviceId) : string.Empty),
            ["groupId"] = JsValue.FromString(exposed ? OriginScopedId("group", device.GroupId) : string.Empty),
            ["label"] = JsValue.FromString(exposed ? device.Label : string.Empty),
            ["kind"] = JsValue.FromString(device.Kind == MediaTrackKind.Audio ? "audioinput" : "videoinput"),
            ["width"] = JsValue.FromInt32(device.Width),
            ["height"] = JsValue.FromInt32(device.Height),
            ["frameRate"] = JsValue.FromNumber(device.FrameRate),
            ["sampleRate"] = JsValue.FromInt32(device.SampleRate),
            ["channelCount"] = JsValue.FromInt32(device.Channels),
        });

    private void InstallFenJsUserMedia()
    {
        // (forEnumeration): the capture devices as getUserMedia() sees them, or as
        // enumerateDevices() may list them (9.2.1): only kinds the Permissions Policy
        // allows, and before a kind was exposed one entry for it with no identifier or label.
        Native("__fenCaptureDeviceList", 1, args =>
        {
            bool forEnumeration = args.Count > 0 && args[0].Tag == JsValueTag.Boolean && args[0].AsBoolean();
            var records = new List<JsValue>();
            bool audioListed = false, videoListed = false;
            foreach (var device in CaptureDevices.Provider.Devices)
            {
                bool exposed = !forEnumeration || _captureKindsExposed.Contains(device.Kind);
                if (forEnumeration && !IsFeatureEnabledInDocument(
                        device.Kind == MediaTrackKind.Audio ? PolicyControlledFeature.Microphone : PolicyControlledFeature.Camera))
                {
                    continue;
                }

                if (!exposed)
                {
                    ref bool listed = ref (device.Kind == MediaTrackKind.Audio ? ref audioListed : ref videoListed);
                    if (listed)
                    {
                        continue;
                    }

                    listed = true;
                }

                records.Add(CaptureDeviceRecord(device, exposed));
            }

            return _interpreter.AllocateArray(records);
        });

        // (audio, video): { policy, permission } - whether the Permissions Policy allows
        // those kinds (10.1 step 6, checked first) and whether capture is permitted (step
        // 10, checked once devices were chosen). A refusal of either is NotAllowedError.
        Native("__fenUserMediaCheck", 2, args =>
        {
            bool audio = args.Count > 0 && args[0].Tag == JsValueTag.Boolean && args[0].AsBoolean();
            bool video = args.Count > 1 && args[1].Tag == JsValueTag.Boolean && args[1].AsBoolean();
            bool policy = (!audio || IsFeatureEnabledInDocument(PolicyControlledFeature.Microphone)) &&
                          (!video || IsFeatureEnabledInDocument(PolicyControlledFeature.Camera));
            return _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["policy"] = JsValue.FromBoolean(policy),
                ["permission"] = JsValue.FromBoolean(HasCapturePermission()),
            });
        });

        // (pageDeviceId): opens the device; { key, pipe, video } or null.
        Native("__fenOpenCapture", 1, args =>
        {
            var pageId = args.Count > 0 && args[0].Tag == JsValueTag.String ? CoerceToHostString(args[0]) : string.Empty;
            CaptureDeviceInfo target = null;
            foreach (var device in CaptureDevices.Provider.Devices)
            {
                if (string.Equals(OriginScopedId("capture", device.DeviceId), pageId, StringComparison.Ordinal))
                {
                    target = device;
                    break;
                }
            }

            var session = target == null ? null : CaptureDevices.Provider.Open(target.DeviceId);
            if (session == null)
            {
                return JsValue.Null;
            }

            var key = Guid.NewGuid().ToString("N");
            _captureSessions[key] = session;
            _captureKindsExposed.Add(session.Device.Kind);
            if (session.Audio is { } pipe)
            {
                lock (_audioTrackPipes)
                    _audioTrackPipes[key] = pipe;
            }

            if (session.Video is { } video)
            {
                lock (_captureVideoSources)
                    _captureVideoSources[key] = video;
            }

            return _interpreter.AllocateObject(new Dictionary<string, JsValue>
            {
                ["key"] = JsValue.FromString(key),
                ["pipe"] = JsValue.FromString(session.Audio != null ? key : string.Empty),
                ["video"] = JsValue.FromString(session.Video != null ? key : string.Empty),
            });
        });

        // The track stopped: the device is released.
        Native("__fenCloseCapture", 1, args =>
        {
            var key = args.Count > 0 && args[0].Tag == JsValueTag.String ? CoerceToHostString(args[0]) : null;
            if (key != null && _captureSessions.Remove(key, out var session))
            {
                session.Dispose();
                lock (_audioTrackPipes)
                    _audioTrackPipes.Remove(key);
                lock (_captureVideoSources)
                    _captureVideoSources.Remove(key);
            }

            return JsValue.Undefined;
        });

        EvaluateWithFenJsRaw(UserMediaPrelude);

        void Native(string name, int length, Func<IReadOnlyList<JsValue>, JsValue> body) =>
            _interpreter.RegisterGlobalValue(name, _interpreter.AllocateNativeFunction(name, (_, args) => body(args), length: length));
    }

    private const string UserMediaPrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            var devices = g.navigator && g.navigator.mediaDevices;
            if (!devices || typeof g.__fenMakeCaptureTrack !== 'function') return;

            // mediacapture-main 11: OverconstrainedError names the constraint no device met.
            if (typeof g.OverconstrainedError !== 'function') {
                var OverconstrainedError = function OverconstrainedError(constraint, message) {
                    var error = new g.DOMException(message === undefined ? '' : String(message), 'OverconstrainedError');
                    Object.setPrototypeOf(error, OverconstrainedError.prototype);
                    Object.defineProperty(error, '_constraint', { value: String(constraint) });
                    return error;
                };
                OverconstrainedError.prototype = Object.create(g.DOMException.prototype);
                Object.defineProperty(OverconstrainedError.prototype, 'constructor', { value: OverconstrainedError, writable: true, configurable: true });
                Object.defineProperty(OverconstrainedError.prototype, 'constraint', {
                    get: function () { return this._constraint; }, configurable: true, enumerable: true
                });
                Object.defineProperty(OverconstrainedError.prototype, Symbol.toStringTag, { value: 'OverconstrainedError', configurable: true });
                Object.defineProperty(g, 'OverconstrainedError', { value: OverconstrainedError, writable: true, configurable: true });
            }

            function reject(name, message) {
                return Promise.reject(new g.DOMException(message, name));
            }

            // A constraint's exact and ideal parts: a bare value is ideal (mediacapture-main 5).
            function parts(value) {
                if (value === undefined) return {};
                if (value !== null && typeof value === 'object' && !Array.isArray(value)) return value;
                return { ideal: value };
            }
            function list(v) { return v === undefined ? undefined : Array.isArray(v) ? v.map(String) : [String(v)]; }

            // 5.2 SelectSettings, for the properties a device has one value of: an exact,
            // min or max it misses rules it out; the first device left wins, ideal deviceId
            // first. Returns { device } or { failed: constraintName }.
            function select(candidates, constraints) {
                var c = constraints && typeof constraints === 'object' ? constraints : {};
                var failed = null;
                var left = candidates.filter(function (d) {
                    var id = parts(c.deviceId), exactIds = list(id.exact);
                    if (exactIds && exactIds.indexOf(d.deviceId) < 0) { failed = failed || 'deviceId'; return false; }
                    var numeric = d.kind === 'videoinput'
                        ? { width: d.width, height: d.height, frameRate: d.frameRate }
                        : { sampleRate: d.sampleRate, channelCount: d.channelCount };
                    for (var name in numeric) {
                        var p = parts(c[name]), v = numeric[name];
                        if (p.exact !== undefined && Number(p.exact) !== v) { failed = failed || name; return false; }
                        if (p.min !== undefined && v < Number(p.min)) { failed = failed || name; return false; }
                        if (p.max !== undefined && v > Number(p.max)) { failed = failed || name; return false; }
                    }
                    return true;
                });
                if (!left.length) return { failed: failed || 'deviceId' };
                var ideal = list(parts(c.deviceId).ideal);
                if (ideal) {
                    var preferred = left.filter(function (d) { return ideal.indexOf(d.deviceId) >= 0; });
                    if (preferred.length) left = preferred;
                }
                return { device: left[0] };
            }

            function settingsFor(d) {
                return d.kind === 'videoinput'
                    ? { deviceId: d.deviceId, groupId: d.groupId, width: d.width, height: d.height,
                        frameRate: d.frameRate, aspectRatio: d.width / d.height, resizeMode: 'none' }
                    : { deviceId: d.deviceId, groupId: d.groupId, sampleRate: d.sampleRate,
                        channelCount: d.channelCount, echoCancellation: false, autoGainControl: false,
                        noiseSuppression: false };
            }

            Object.defineProperty(devices, 'getUserMedia', {
                value: function getUserMedia(constraints) {
                    // 10.1 steps 1-3: a dictionary asking for at least one kind.
                    if (constraints === undefined) constraints = {};
                    if (constraints === null || typeof constraints !== 'object') {
                        return Promise.reject(new TypeError("Failed to execute 'getUserMedia': constraints is not an object."));
                    }
                    var wantAudio = constraints.audio !== undefined && constraints.audio !== false;
                    var wantVideo = constraints.video !== undefined && constraints.video !== false;
                    if (!wantAudio && !wantVideo) {
                        return Promise.reject(new TypeError("Failed to execute 'getUserMedia': at least one of audio and video must be requested."));
                    }

                    var check = g.__fenUserMediaCheck(wantAudio, wantVideo);
                    if (!check.policy) return reject('NotAllowedError', "The document's permissions policy does not allow capture.");

                    var all = g.__fenCaptureDeviceList(false);
                    var chosen = [];
                    var kinds = [];
                    if (wantAudio) kinds.push(['audioinput', constraints.audio]);
                    if (wantVideo) kinds.push(['videoinput', constraints.video]);
                    for (var k = 0; k < kinds.length; k++) {
                        var candidates = all.filter(function (d) { return d.kind === kinds[k][0]; });
                        if (!candidates.length) return reject('NotFoundError', 'No ' + kinds[k][0] + ' device is available.');
                        var result = select(candidates, kinds[k][1]);
                        if (result.failed) {
                            return Promise.reject(new g.OverconstrainedError(result.failed, 'No device satisfies the ' + result.failed + ' constraint.'));
                        }
                        chosen.push(result.device);
                    }

                    if (!check.permission) return reject('NotAllowedError', 'Permission to capture was not granted.');

                    var tracks = [];
                    for (var i = 0; i < chosen.length; i++) {
                        var d = chosen[i];
                        var opened = g.__fenOpenCapture(d.deviceId);
                        if (!opened) {
                            tracks.forEach(function (t) { t.stop(); });
                            return reject('NotReadableError', 'The ' + d.kind + ' device could not be started.');
                        }
                        tracks.push(g.__fenMakeCaptureTrack(d.kind === 'audioinput' ? 'audio' : 'video', d.label,
                            opened.pipe || null, opened.video || '', opened.key, settingsFor(d)));
                    }
                    return Promise.resolve(new g.MediaStream(tracks));
                },
                writable: true, configurable: true
            });

            // 9.2 enumerateDevices(): the capture devices, then what was listed before
            // (the audio outputs this document was given).
            var listOutputs = devices.enumerateDevices;
            var InputDeviceInfo = typeof g.InputDeviceInfo === 'function' ? g.InputDeviceInfo : null;
            if (!InputDeviceInfo && typeof g.MediaDeviceInfo === 'function') {
                InputDeviceInfo = function InputDeviceInfo() { throw new TypeError('Illegal constructor'); };
                InputDeviceInfo.prototype = Object.create(g.MediaDeviceInfo.prototype);
                Object.defineProperty(InputDeviceInfo.prototype, 'constructor', { value: InputDeviceInfo, writable: true, configurable: true });
                Object.defineProperty(InputDeviceInfo.prototype, Symbol.toStringTag, { value: 'InputDeviceInfo', configurable: true });
                Object.defineProperty(InputDeviceInfo.prototype, 'getCapabilities', {
                    value: function getCapabilities() {
                        var r = this._record;
                        if (!r.deviceId) return {};
                        return r.kind === 'videoinput'
                            ? { deviceId: r.deviceId, groupId: r.groupId, width: { min: 1, max: r.width },
                                height: { min: 1, max: r.height }, frameRate: { min: 1, max: r.frameRate } }
                            : { deviceId: r.deviceId, groupId: r.groupId, sampleRate: { min: r.sampleRate, max: r.sampleRate },
                                channelCount: { min: r.channelCount, max: r.channelCount } };
                    },
                    writable: true, configurable: true
                });
                Object.defineProperty(g, 'InputDeviceInfo', { value: InputDeviceInfo, writable: true, configurable: true });
            }
            Object.defineProperty(devices, 'enumerateDevices', {
                value: function enumerateDevices() {
                    var inputs = g.__fenCaptureDeviceList(true).map(function (r) {
                        var info = Object.create(InputDeviceInfo ? InputDeviceInfo.prototype : Object.prototype);
                        Object.defineProperty(info, '_record', { value: r });
                        return info;
                    });
                    var outputs = typeof listOutputs === 'function' ? listOutputs.call(devices) : Promise.resolve([]);
                    return Promise.resolve(outputs).then(function (o) { return inputs.concat(o); });
                },
                writable: true, configurable: true
            });

            Object.defineProperty(devices, 'getSupportedConstraints', {
                value: function getSupportedConstraints() {
                    return { deviceId: true, groupId: true, width: true, height: true, frameRate: true,
                             aspectRatio: true, sampleRate: true, channelCount: true, resizeMode: true,
                             echoCancellation: true, autoGainControl: true, noiseSuppression: true };
                },
                writable: true, configurable: true
            });
        })();
        """;
}
