using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Media;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Audio Output Devices API (https://w3c.github.io/mediacapture-output/):
/// <c>navigator.mediaDevices.selectAudioOutput()</c>, the audio outputs
/// <c>enumerateDevices()</c> lists once one has been chosen, and the identifiers
/// <c>setSinkId()</c> accepts. A page never sees a platform endpoint identifier: it sees
/// one derived from that identifier and its origin, the same in every same-origin
/// document and different in every other origin (mediacapture-main §9.2.1 deviceId).
/// There is no chooser to show, so a request picks the device the page asked for when it
/// may have it, and the system default otherwise.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    /// <summary>
    /// Platform endpoints this document has been given, in the order they were chosen: the
    /// ones enumerateDevices() lists and setSinkId() accepts (§3.1 "exposed").
    /// </summary>
    private readonly List<string> _exposedAudioOutputs = new();

    /// <summary>The identifier a page sees for a platform endpoint; empty stays empty (the default).</summary>
    internal string PageAudioOutputId(string platformId) =>
        string.IsNullOrEmpty(platformId) ? string.Empty : OriginScopedId("audiooutput", platformId);

    /// <summary>
    /// The exposed platform endpoint a page's identifier names: empty for the default,
    /// null for an identifier this document was never given.
    /// </summary>
    internal string ResolveExposedAudioOutput(string pageId)
    {
        if (string.IsNullOrEmpty(pageId))
            return string.Empty;
        foreach (var platformId in _exposedAudioOutputs)
        {
            if (string.Equals(PageAudioOutputId(platformId), pageId, StringComparison.Ordinal))
                return platformId;
        }

        return null;
    }

    private string OriginScopedId(string purpose, string platformId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(purpose + "\n" + TryResolveCurrentOrigin(null) + "\n" + platformId));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private JsValue AudioOutputRecord(FenBrowser.Media.Audio.AudioOutputDevice device) =>
        _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["deviceId"] = JsValue.FromString(PageAudioOutputId(device.DeviceId)),
            ["groupId"] = JsValue.FromString(OriginScopedId("group", device.DeviceId)),
            ["label"] = JsValue.FromString(device.Label ?? string.Empty),
        });

    private void InstallFenJsAudioOutputDevices()
    {
        // §3.1 selectAudioOutput(): the policy and the activation are checked when the page
        // calls, so a refusal comes back already settled.
        Native("__fenSelectAudioOutput", 1, args =>
        {
            if (!IsFeatureEnabledInDocument(FenBrowser.Core.Security.PolicyControlledFeature.SpeakerSelection))
                return Refusal("NotAllowedError", "The permissions policy does not allow 'speaker-selection' in this document.");
            if (!HasTransientActivation)
                return Refusal("InvalidStateError", "selectAudioOutput() needs a user gesture.");

            string requested = args.Count > 0 && args[0].Tag == JsValueTag.String ? CoerceToHostString(args[0]) : string.Empty;
            FenBrowser.Media.Audio.AudioOutputDevice? chosen = null;
            foreach (var device in MediaEngineServices.AudioOutputs.Devices)
            {
                // A deviceId the page was given before, here or in another document of its
                // origin, selects that device again without asking.
                if (requested.Length > 0 && string.Equals(PageAudioOutputId(device.DeviceId), requested, StringComparison.Ordinal))
                {
                    chosen = device;
                    break;
                }

                if (chosen == null || device.IsDefault)
                    chosen = device;
            }

            if (chosen is not { } selected)
                return Refusal("NotFoundError", "There is no audio output device to choose.");

            if (!_exposedAudioOutputs.Contains(selected.DeviceId))
                _exposedAudioOutputs.Add(selected.DeviceId);
            return AudioOutputRecord(selected);
        });

        // The audio outputs enumerateDevices() lists: only the ones this document was given.
        Native("__fenExposedAudioOutputs", 0, _ =>
        {
            var records = new List<JsValue>();
            foreach (var device in MediaEngineServices.AudioOutputs.Devices)
            {
                if (_exposedAudioOutputs.Contains(device.DeviceId))
                    records.Add(AudioOutputRecord(device));
            }

            return _interpreter.AllocateArray(records.ToArray());
        });

        try
        {
            EvaluateWithFenJsRaw(AudioOutputPrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] audio output prelude failed: {ex.Message}", LogCategory.JavaScript);
        }

        JsValue Refusal(string name, string message) => _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["error"] = JsValue.FromString(name),
            ["message"] = JsValue.FromString(message),
        });

        void Native(string name, int length, Func<IReadOnlyList<JsValue>, JsValue> body) =>
            _interpreter.RegisterGlobalValue(name, _interpreter.AllocateNativeFunction(name, (_, args) => body(args), length: length));
    }

    private const string AudioOutputPrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            var devices = g.navigator && g.navigator.mediaDevices;
            if (!devices || typeof g.__fenSelectAudioOutput !== 'function') return;

            var INTERNAL = {};
            var FIELDS = ['deviceId', 'kind', 'label', 'groupId'];

            // mediacapture-main §9.2.1 MediaDeviceInfo: made by the user agent, never by a page.
            function MediaDeviceInfo(key, record) {
                if (key !== INTERNAL) throw new TypeError('Illegal constructor');
                Object.defineProperty(this, '_record', { value: record });
            }
            FIELDS.forEach(function (name) {
                Object.defineProperty(MediaDeviceInfo.prototype, name, {
                    get: function () { return this._record[name]; },
                    enumerable: true,
                    configurable: true
                });
            });
            Object.defineProperty(MediaDeviceInfo.prototype, 'toJSON', {
                value: function toJSON() {
                    var self = this;
                    var json = {};
                    FIELDS.forEach(function (name) { json[name] = self[name]; });
                    return json;
                },
                writable: true,
                configurable: true
            });
            Object.defineProperty(MediaDeviceInfo.prototype, Symbol.toStringTag, { value: 'MediaDeviceInfo', configurable: true });
            if (typeof g.MediaDeviceInfo !== 'function')
                Object.defineProperty(g, 'MediaDeviceInfo', { value: MediaDeviceInfo, writable: true, configurable: true });

            function info(record) {
                return new MediaDeviceInfo(INTERNAL, {
                    deviceId: record.deviceId,
                    kind: 'audiooutput',
                    label: record.label,
                    groupId: record.groupId
                });
            }

            Object.defineProperty(devices, 'selectAudioOutput', {
                value: function selectAudioOutput(options) {
                    var requested = options !== undefined && options !== null && options.deviceId !== undefined
                        ? String(options.deviceId) : '';
                    var result = g.__fenSelectAudioOutput(requested);
                    if (result.error) return Promise.reject(new g.DOMException(result.message, result.error));
                    return Promise.resolve(info(result));
                },
                writable: true,
                configurable: true
            });

            Object.defineProperty(devices, 'enumerateDevices', {
                value: function enumerateDevices() {
                    return Promise.resolve(Array.prototype.map.call(g.__fenExposedAudioOutputs(), info));
                },
                writable: true,
                configurable: true
            });
        })();
        """;
}
