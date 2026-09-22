using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Media;
using FenBrowser.Js.Runtime;
using FenBrowser.Media.Types;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Media Capabilities (https://w3c.github.io/media-capabilities/):
/// <c>navigator.mediaCapabilities.decodingInfo()</c> and <c>encodingInfo()</c>. The
/// dictionary conversions and the enum checks are WebIDL work and live in the prelude;
/// whether a configuration is a valid one, and whether this engine can decode it, is
/// answered by <see cref="MediaTypeSupport"/> over the demuxer and decoder registries, so
/// the answer moves with what the engine can actually do.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private void InstallFenJsMediaCapabilities()
    {
        _interpreter.RegisterGlobalValue(
            "__fenMediaCapabilities",
            _interpreter.AllocateNativeFunction("__fenMediaCapabilities", (_, args) => AnswerMediaCapabilities(args), length: 2));

        try
        {
            EvaluateWithFenJsRaw(MediaCapabilitiesPrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] media capabilities prelude failed: {ex.Message}", LogCategory.JavaScript);
        }
    }

    /// <summary>
    /// Answers one <c>decodingInfo()</c> or <c>encodingInfo()</c> query. The prelude has
    /// already done the WebIDL conversions, so the arguments are a use, an optional audio
    /// content type, and the video configuration's members.
    /// </summary>
    private JsValue AnswerMediaCapabilities(IReadOnlyList<JsValue> args)
    {
        bool decoding = args.Count > 0 && args[0].Tag == JsValueTag.Boolean && args[0].AsBoolean();
        string use = args.Count > 1 && args[1].Tag == JsValueTag.String ? CoerceToHostString(args[1]) : string.Empty;
        string? audioType = ReadOptionalString(args, 2);
        string? videoType = ReadOptionalString(args, 3);

        var kind = use switch
        {
            "file" => MediaCapabilitiesUse.File,
            "media-source" => MediaCapabilitiesUse.MediaSource,
            "webrtc" => MediaCapabilitiesUse.WebRtc,
            "record" => MediaCapabilitiesUse.Record,
            _ => (MediaCapabilitiesUse?)null,
        };

        MediaCapabilitiesAudio? audio = audioType is null ? null : new MediaCapabilitiesAudio(audioType);
        MediaCapabilitiesVideo? video = videoType is null
            ? null
            : new MediaCapabilitiesVideo(
                videoType,
                (uint)Math.Clamp(ReadNumber(args, 4), 0, uint.MaxValue),
                (uint)Math.Clamp(ReadNumber(args, 5), 0, uint.MaxValue),
                ReadNumber(args, 6),
                ReadOptionalString(args, 7),
                ReadOptionalString(args, 8),
                ReadOptionalString(args, 9));

        bool valid = kind is not null
            && (audio is not null || video is not null)
            && (audio is null || MediaTypeSupport.IsValidAudioConfiguration(kind.Value, audio))
            && (video is null || MediaTypeSupport.IsValidVideoConfiguration(kind.Value, video));

        var info = default(MediaCapabilitiesInfo);
        if (valid)
        {
            var support = MediaEngineServices.TypeSupport;
            info = decoding ? support.DecodingInfo(kind!.Value, audio, video) : support.EncodingInfo(kind!.Value, audio, video);
        }

        return _interpreter.AllocateObject(new Dictionary<string, JsValue>
        {
            ["valid"] = JsValue.FromBoolean(valid),
            ["supported"] = JsValue.FromBoolean(info.Supported),
            ["smooth"] = JsValue.FromBoolean(info.Smooth),
            ["powerEfficient"] = JsValue.FromBoolean(info.PowerEfficient),
        });
    }

    private string? ReadOptionalString(IReadOnlyList<JsValue> args, int index) =>
        index < args.Count && args[index].Tag == JsValueTag.String ? CoerceToHostString(args[index]) : null;

    private static double ReadNumber(IReadOnlyList<JsValue> args, int index)
    {
        if (index >= args.Count)
            return 0;
        return args[index].Tag switch
        {
            JsValueTag.Number => args[index].AsNumber(),
            JsValueTag.Int32 => args[index].AsNumber(),
            _ => 0,
        };
    }

    private const string MediaCapabilitiesPrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            if (typeof g.MediaCapabilities === 'function') return;

            var DECODING_TYPES = ['file', 'media-source', 'webrtc'];
            var ENCODING_TYPES = ['record', 'webrtc'];
            var COLOR_GAMUTS = ['srgb', 'p3', 'rec2020'];
            var TRANSFER_FUNCTIONS = ['srgb', 'pq', 'hlg'];
            var HDR_METADATA_TYPES = ['smpteSt2086', 'smpteSt2094-10', 'smpteSt2094-40'];
            var KEY_REQUIREMENTS = ['required', 'optional', 'not-allowed'];

            function dictionary(value, what) {
                if (value === undefined || value === null) return {};
                if (typeof value !== 'object' && typeof value !== 'function') {
                    throw new TypeError(what + ': The provided value is not of type dictionary.');
                }
                return value;
            }
            function required(value, member, what) {
                if (value === undefined) {
                    throw new TypeError("Failed to read the '" + member + "' property from '" + what + "': Required member is undefined.");
                }
                return value;
            }
            function toUnsignedLong(value, member, what) {
                var n = Number(required(value, member, what));
                if (!isFinite(n)) throw new TypeError(what + '.' + member + ' is not a finite value.');
                return n < 0 ? 0 : Math.floor(n);
            }
            function toDouble(value, member, what) {
                var n = Number(required(value, member, what));
                return n;
            }
            function toEnum(value, values, member, what) {
                if (value === undefined) return null;
                var s = String(value);
                if (values.indexOf(s) < 0) {
                    throw new TypeError("Failed to read the '" + member + "' property from '" + what +
                        "': The provided value '" + s + "' is not a valid enum value.");
                }
                return s;
            }

            function readVideo(init, type) {
                init = dictionary(init, 'VideoConfiguration');
                var video = {
                    contentType: String(required(init.contentType, 'contentType', 'VideoConfiguration')),
                    width: toUnsignedLong(init.width, 'width', 'VideoConfiguration'),
                    height: toUnsignedLong(init.height, 'height', 'VideoConfiguration'),
                    bitrate: toUnsignedLong(init.bitrate, 'bitrate', 'VideoConfiguration'),
                    framerate: toDouble(init.framerate, 'framerate', 'VideoConfiguration'),
                    colorGamut: toEnum(init.colorGamut, COLOR_GAMUTS, 'colorGamut', 'VideoConfiguration'),
                    transferFunction: toEnum(init.transferFunction, TRANSFER_FUNCTIONS, 'transferFunction', 'VideoConfiguration'),
                    hdrMetadataType: toEnum(init.hdrMetadataType, HDR_METADATA_TYPES, 'hdrMetadataType', 'VideoConfiguration')
                };
                // §3.1: the dynamic-range members describe a decoded picture, so they only
                // belong to a media resource; the scalability mode only belongs to a
                // WebRTC stream.
                if (type !== 'file' && type !== 'media-source' &&
                    (video.colorGamut !== null || video.transferFunction !== null || video.hdrMetadataType !== null)) {
                    throw new TypeError("VideoConfiguration: colorGamut, transferFunction and hdrMetadataType are not allowed for type '" + type + "'.");
                }
                if (type !== 'webrtc' && init.scalabilityMode !== undefined) {
                    throw new TypeError("VideoConfiguration: scalabilityMode is not allowed for type '" + type + "'.");
                }
                return video;
            }
            // §3.2 steps 4-6: the key system configuration is read and checked, but no key
            // system is wired to Media Capabilities yet (ADR-0005 allows Clear Key only, and
            // EME is a later phase), so an encrypted configuration is never supported.
            function readKeySystem(init, configuration, what) {
                init = dictionary(init, 'MediaCapabilitiesKeySystemConfiguration');
                var keySystem = String(required(init.keySystem, 'keySystem', 'MediaCapabilitiesKeySystemConfiguration'));
                if (init.initDataType !== undefined) String(init.initDataType);
                toEnum(init.distinctiveIdentifier, KEY_REQUIREMENTS, 'distinctiveIdentifier', 'MediaCapabilitiesKeySystemConfiguration');
                toEnum(init.persistentState, KEY_REQUIREMENTS, 'persistentState', 'MediaCapabilitiesKeySystemConfiguration');
                if (init.sessionTypes !== undefined) {
                    // WebIDL sequence conversion takes an object with an iterator; a bare
                    // string is iterable but is not an object, so it is a TypeError.
                    if (init.sessionTypes === null || typeof init.sessionTypes !== 'object' ||
                        typeof init.sessionTypes[Symbol.iterator] !== 'function') {
                        throw new TypeError(what + ": The 'sessionTypes' member cannot be converted to a sequence.");
                    }
                    Array.from(init.sessionTypes).forEach(function (t) { String(t); });
                }
                // Robustness for a track the configuration does not describe is meaningless.
                if (init.audio !== undefined && configuration.audio === undefined) {
                    throw new TypeError(what + ': The key system configuration describes audio the media configuration does not.');
                }
                if (init.video !== undefined && configuration.video === undefined) {
                    throw new TypeError(what + ': The key system configuration describes video the media configuration does not.');
                }
                if (init.audio !== undefined) dictionary(init.audio, 'KeySystemTrackConfiguration');
                if (init.video !== undefined) dictionary(init.video, 'KeySystemTrackConfiguration');
                return keySystem;
            }

            function readAudio(init) {
                init = dictionary(init, 'AudioConfiguration');
                return { contentType: String(required(init.contentType, 'contentType', 'AudioConfiguration')) };
            }

            // §3.2 / §3.3: everything that can go wrong with the configuration rejects the
            // promise with a TypeError; a configuration this engine cannot handle resolves
            // with supported false.
            function query(decoding, types, configuration, what) {
                return new Promise(function (resolve, reject) {
                    var audio = null;
                    var video = null;
                    var encrypted = false;
                    var type;
                    try {
                        configuration = dictionary(configuration, what);
                        type = configuration.type === undefined ? undefined : String(configuration.type);
                        if (types.indexOf(type) < 0) {
                            throw new TypeError(what + ": The provided value '" + type + "' is not a valid enum value.");
                        }
                        if (configuration.audio !== undefined) audio = readAudio(configuration.audio);
                        if (configuration.video !== undefined) video = readVideo(configuration.video, type);
                        if (audio === null && video === null) {
                            throw new TypeError(what + ': The configuration dictionary has neither audio nor video.');
                        }
                        if (decoding && configuration.keySystemConfiguration !== undefined) {
                            if (type !== 'file' && type !== 'media-source') {
                                throw new TypeError(what + ": A key system configuration is not allowed for type '" + type + "'.");
                            }
                            // EME needs a secure context, so asking about an encrypted
                            // configuration outside one is a SecurityError.
                            if (g.isSecureContext === false) {
                                var error = new DOMException('Encrypted media is only available in a secure context.', 'SecurityError');
                                reject(error);
                                return;
                            }
                            readKeySystem(configuration.keySystemConfiguration, configuration, what);
                            encrypted = true;
                        }
                    } catch (e) {
                        reject(e);
                        return;
                    }

                    var answer = __fenMediaCapabilities(
                        decoding, type,
                        audio ? audio.contentType : null,
                        video ? video.contentType : null,
                        video ? video.width : 0,
                        video ? video.height : 0,
                        video ? video.framerate : 0,
                        video ? video.colorGamut : null,
                        video ? video.transferFunction : null,
                        video ? video.hdrMetadataType : null);

                    if (!answer.valid) {
                        reject(new TypeError(what + ': The configuration is not a valid MediaConfiguration.'));
                        return;
                    }

                    var info = Object.create(MediaCapabilitiesInfo.prototype);
                    info._supported = answer.supported && !encrypted;
                    info._smooth = answer.smooth && !encrypted;
                    info._powerEfficient = answer.powerEfficient && !encrypted;
                    resolve(info);
                });
            }

            function defineInterface(name, ctor) {
                Object.defineProperty(ctor.prototype, 'constructor', { value: ctor, writable: true, configurable: true });
                Object.defineProperty(ctor.prototype, Symbol.toStringTag, { value: name, configurable: true });
                Object.defineProperty(ctor, 'name', { value: name, configurable: true });
                g[name] = ctor;
            }
            function accessor(proto, name, get) {
                Object.defineProperty(proto, name, { get: get, enumerable: true, configurable: true });
            }
            function method(proto, name, fn) {
                Object.defineProperty(proto, name, { value: fn, writable: true, enumerable: true, configurable: true });
            }

            function MediaCapabilitiesInfo() { throw new TypeError('Illegal constructor'); }
            defineInterface('MediaCapabilitiesInfo', MediaCapabilitiesInfo);
            accessor(MediaCapabilitiesInfo.prototype, 'supported', function () { return this._supported; });
            accessor(MediaCapabilitiesInfo.prototype, 'smooth', function () { return this._smooth; });
            accessor(MediaCapabilitiesInfo.prototype, 'powerEfficient', function () { return this._powerEfficient; });
            // Clear Key is the only key system this engine has (ADR-0005) and it is not
            // wired to Media Capabilities yet, so no configuration comes with access.
            accessor(MediaCapabilitiesInfo.prototype, 'keySystemAccess', function () { return null; });

            function MediaCapabilities() { throw new TypeError('Illegal constructor'); }
            defineInterface('MediaCapabilities', MediaCapabilities);
            method(MediaCapabilities.prototype, 'decodingInfo', function (configuration) {
                return query(true, DECODING_TYPES, configuration, "Failed to execute 'decodingInfo' on 'MediaCapabilities'");
            });
            method(MediaCapabilities.prototype, 'encodingInfo', function (configuration) {
                return query(false, ENCODING_TYPES, configuration, "Failed to execute 'encodingInfo' on 'MediaCapabilities'");
            });

            var capabilities = Object.create(MediaCapabilities.prototype);
            var installed = false;
            if (typeof g.Navigator === 'function' && g.Navigator.prototype) {
                try {
                    Object.defineProperty(g.Navigator.prototype, 'mediaCapabilities', { get: function () { return capabilities; }, enumerable: true, configurable: true });
                    installed = g.navigator && g.navigator.mediaCapabilities === capabilities;
                } catch (e) { installed = false; }
            }
            if (!installed && g.navigator) {
                try { Object.defineProperty(g.navigator, 'mediaCapabilities', { value: capabilities, enumerable: true, configurable: true }); }
                catch (e) { try { g.navigator.mediaCapabilities = capabilities; } catch (e2) {} }
            }
        })();
        """;
}
