using System;
using System.Collections.Generic;
using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Media;
using FenBrowser.Js.Runtime;
using FenBrowser.Media.Eme;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Encrypted Media Extensions (https://w3c.github.io/encrypted-media/), Clear Key only
/// (ADR-0005). The WebIDL shapes, the promises, the event queue and the
/// <c>MediaKeyStatusMap</c> live in the realm as script (<see cref="EmePrelude"/>); the
/// key system negotiation, the session state machine and the key store are
/// <see cref="MediaKeySystemSupport"/>, <see cref="ClearKeySession"/> and
/// <see cref="ClearKeyCdm"/>, reached through the <c>__fenEme*</c> natives below.
/// </summary>
/// <remarks>
/// No key material ever crosses into the realm: script hands us a licence and we hand back
/// key statuses, never keys. Decryption happens beside the decoder, so a <c>MediaKeys</c>
/// reaches the element's resource as an <see cref="IMediaKeySource"/> and nothing more.
/// </remarks>
public sealed partial class FenJsBrowserScriptEngine
{
    private sealed class EmeKeysEntry
    {
        public ClearKeyCdm Cdm;
        public string KeySystem;
        public readonly List<int> SessionIds = new();
    }

    private readonly Dictionary<int, EmeKeysEntry> _emeKeys = new();
    private readonly Dictionary<int, ClearKeySession> _emeSessions = new();
    private int _nextEmeId = 1;

    private void InstallFenJsEme()
    {
        Native("__fenEmeRequestAccess", 2, args =>
        {
            string keySystem = args.Count > 0 && args[0].Tag == JsValueTag.String ? CoerceToHostString(args[0]) : null;
            string json = args.Count > 1 && args[1].Tag == JsValueTag.String ? CoerceToHostString(args[1]) : null;
            if (!MediaKeySystemSupport.IsSupportedKeySystem(keySystem) || json == null)
            {
                return JsValue.Null;
            }

            // EME §6: a document with an opaque origin has nowhere to keep a licence and
            // nothing to tie one to, so it gets no key system access at all. A sandboxed
            // frame without allow-same-origin is exactly that.
            if (IsOpaqueOriginRealm)
            {
                return JsValue.Null;
            }

            var candidates = ParseKeySystemConfigurations(json);
            if (candidates.Count == 0)
            {
                return JsValue.Null;
            }

            var support = new MediaKeySystemSupport(MediaEngineServices.TypeSupport);
            var supported = support.GetSupportedConfiguration(candidates);
            return supported == null ? JsValue.Null : JsValue.FromString(SerializeKeySystemConfiguration(supported));
        });

        Native("__fenEmeCreateKeys", 1, args =>
        {
            string keySystem = args.Count > 0 && args[0].Tag == JsValueTag.String ? CoerceToHostString(args[0]) : null;
            if (!MediaKeySystemSupport.IsSupportedKeySystem(keySystem))
            {
                ThrowDomException("NotSupportedError", "That key system is not supported.");
                return JsValue.Undefined;
            }

            int id = _nextEmeId++;
            _emeKeys[id] = new EmeKeysEntry { Cdm = new ClearKeyCdm(MediaEngineServices.Log), KeySystem = keySystem };
            return JsValue.FromInt32(id);
        });

        Native("__fenEmeCreateSession", 2, args =>
        {
            var entry = EmeKeysFor(args, 0);
            if (entry == null)
            {
                return JsValue.Undefined;
            }

            string typeName = args.Count > 1 && args[1].Tag == JsValueTag.String ? CoerceToHostString(args[1]) : "temporary";
            if (!ClearKeyLicense.TryParseSessionType(typeName, out var sessionType))
            {
                ThrowDomException("TypeError", $"'{typeName}' is not a MediaKeySessionType.");
                return JsValue.Undefined;
            }

            if (sessionType != MediaKeySessionType.Temporary)
            {
                // The configuration this MediaKeys came from can only have asked for
                // temporary sessions, so anything else was never on offer.
                ThrowDomException("NotSupportedError", "Only temporary sessions are supported.");
                return JsValue.Undefined;
            }

            var session = entry.Cdm.CreateSession(sessionType);
            if (session == null)
            {
                ThrowDomException("QuotaExceededError", "Too many open key sessions.");
                return JsValue.Undefined;
            }

            int id = _nextEmeId++;
            _emeSessions[id] = session;
            entry.SessionIds.Add(id);
            WatchEmeSession(id, session);
            return JsValue.FromInt32(id);
        });

        Native("__fenEmeSessionId", 1, args =>
        {
            var session = EmeSessionFor(args, 0);
            return session == null ? JsValue.FromString(string.Empty) : JsValue.FromString(session.SessionId);
        });

        Native("__fenEmeGenerateRequest", 3, args =>
        {
            var session = EmeSessionFor(args, 0);
            if (session == null)
            {
                return JsValue.Undefined;
            }

            string typeName = args.Count > 1 && args[1].Tag == JsValueTag.String ? CoerceToHostString(args[1]) : null;
            if (typeName == null || !EmeInitData.TryParseInitDataType(typeName, out var initDataType))
            {
                ThrowDomException("NotSupportedError", "That initialization data type is not supported.");
                return JsValue.Undefined;
            }

            byte[] initData = args.Count > 2 ? ExtractBytesFromArrayLike(args[2]) : null;
            return EmeResultToScript(session.GenerateRequest(initDataType, initData ?? Array.Empty<byte>()));
        });

        Native("__fenEmeLoad", 2, args =>
        {
            var session = EmeSessionFor(args, 0);
            if (session == null)
            {
                return JsValue.Undefined;
            }

            string sessionId = args.Count > 1 && args[1].Tag == JsValueTag.String ? CoerceToHostString(args[1]) : string.Empty;
            var result = session.Load(sessionId, out bool loaded);
            return EmeResultToScript(result, JsValue.FromBoolean(loaded));
        });

        Native("__fenEmeUpdate", 2, args =>
        {
            var session = EmeSessionFor(args, 0);
            if (session == null)
            {
                return JsValue.Undefined;
            }

            byte[] response = args.Count > 1 ? ExtractBytesFromArrayLike(args[1]) : null;
            return EmeResultToScript(session.Update(response ?? Array.Empty<byte>()));
        });

        Native("__fenEmeClose", 1, args =>
        {
            var session = EmeSessionFor(args, 0);
            return session == null ? JsValue.Undefined : EmeResultToScript(session.Close());
        });

        Native("__fenEmeRemove", 1, args =>
        {
            var session = EmeSessionFor(args, 0);
            return session == null ? JsValue.Undefined : EmeResultToScript(session.Remove());
        });

        Native("__fenEmeKeyStatuses", 1, args =>
        {
            var session = EmeSessionFor(args, 0);
            var statuses = new List<JsValue>();
            if (session != null)
            {
                foreach (var pair in session.KeyStatuses)
                {
                    statuses.Add(_interpreter.AllocateObject(new Dictionary<string, JsValue>
                    {
                        ["keyId"] = CreateUint8ArrayFromBytes(pair.Key.ToArray()),
                        ["status"] = JsValue.FromString(KeyStatusName(pair.Value)),
                    }));
                }
            }

            return _interpreter.AllocateArray(statuses);
        });

        Native("__fenEmeExpiration", 1, args =>
        {
            var session = EmeSessionFor(args, 0);
            return JsValue.FromNumber(session?.Expiration ?? double.NaN);
        });

        Native("__fenEmeSetServerCertificate", 2, args =>
        {
            var entry = EmeKeysFor(args, 0);
            if (entry == null)
            {
                return JsValue.Undefined;
            }

            byte[] certificate = args.Count > 1 ? ExtractBytesFromArrayLike(args[1]) : null;
            if (certificate == null || certificate.Length == 0)
            {
                ThrowDomException("TypeError", "The server certificate is empty.");
                return JsValue.Undefined;
            }

            // Clear Key uses no server certificate, so EME §5.2 says to resolve with false.
            return JsValue.FromBoolean(ClearKeyCdm.SetServerCertificate(certificate));
        });

        Native("__fenEmeGetStatusForPolicy", 2, args =>
        {
            var entry = EmeKeysFor(args, 0);
            if (entry == null)
            {
                return JsValue.Undefined;
            }

            string hdcp = args.Count > 1 && args[1].Tag == JsValueTag.String ? CoerceToHostString(args[1]) : null;
            return JsValue.FromString(KeyStatusName(ClearKeyCdm.GetStatusForPolicy(hdcp)));
        });

        Native("__fenEmeAttach", 2, args =>
        {
            var element = ResolveHostObjectOrNull(args.Count > 0 ? args[0] : JsValue.Undefined) as Element;
            if (element == null || !IsMediaElement(element))
            {
                ThrowDomException("InvalidStateError", "setMediaKeys() needs a media element.");
                return JsValue.Undefined;
            }

            ClearKeyCdm cdm = null;
            if (args.Count > 1 && args[1].Tag != JsValueTag.Null && args[1].Tag != JsValueTag.Undefined)
            {
                var entry = EmeKeysFor(args, 1);
                if (entry == null)
                {
                    return JsValue.Undefined;
                }

                cdm = entry.Cdm;
            }

            GetOrCreateMediaBinding(element).Controller.SetMediaKeys(cdm);
            return JsValue.Undefined;
        });

        try
        {
            EvaluateWithFenJsRaw(EmePrelude);
        }
        catch (Exception ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] encrypted media prelude failed: {ex.Message}", LogCategory.JavaScript);
        }

        void Native(string name, int length, Func<IReadOnlyList<JsValue>, JsValue> body) =>
            _interpreter.RegisterGlobalValue(name, _interpreter.AllocateNativeFunction(name, (_, args) => body(args), length: length));
    }

    /// <summary>
    /// Subscribes to a session's messages and status changes. The prelude turns each one
    /// into a queued event, so nothing fires synchronously inside the call that caused it.
    /// </summary>
    private void WatchEmeSession(int id, ClearKeySession session)
    {
        session.MessageGenerated += (type, message) => CallEmeHook(
            "__fenEmeOnMessage",
            JsValue.FromInt32(id),
            JsValue.FromString(MessageTypeName(type)),
            CreateUint8ArrayFromBytes(message));

        session.KeyStatusesChanged += () => CallEmeHook("__fenEmeOnKeyStatusesChange", JsValue.FromInt32(id));

        session.SessionClosed += reason =>
        {
            CallEmeHook("__fenEmeOnClosed", JsValue.FromInt32(id), JsValue.FromString(reason));
            _emeSessions.Remove(id);
        };
    }

    /// <summary>EME §7.1: the element's resource announced initialization data.</summary>
    internal void FireMediaEncryptedEvent(Element element, string initDataType, byte[] initData)
    {
        if (element == null || _realmAbandoned || _interpreter == null)
        {
            return;
        }

        CallEmeHook(
            "__fenEmeFireEncrypted",
            ToHostNodeOrNull(element),
            JsValue.FromString(initDataType ?? string.Empty),
            CreateUint8ArrayFromBytes(initData ?? Array.Empty<byte>()));
    }

    private bool TryGetMediaElementEmeProperty(Element element, string property, out JsValue value)
    {
        switch (property)
        {
            case "mediaKeys":
                value = CallEmeHook("__fenEmeElementMediaKeys", ToHostNodeOrNull(element));
                return value.Tag != JsValueTag.Undefined;
            case "setMediaKeys":
                value = GetOrCreateHostCallable(element, "setMediaKeys", (_, args) =>
                {
                    var hookArgs = new JsValue[args.Count + 1];
                    hookArgs[0] = ToHostNodeOrNull(element);
                    for (var i = 0; i < args.Count; i++)
                    {
                        hookArgs[i + 1] = args[i];
                    }

                    return CallEmeHook("__fenEmeElementSetMediaKeys", hookArgs);
                });
                return true;
            default:
                value = JsValue.Undefined;
                return false;
        }
    }

    /// <summary>
    /// Whether this realm's document has an opaque origin: a frame sandboxed without
    /// allow-same-origin. Such a document is its own origin, unequal to every other,
    /// which is what makes storage-backed APIs refuse it.
    /// </summary>
    private bool IsOpaqueOriginRealm =>
        _embeddingFrameElement != null &&
        _embeddingFrameElement.HasAttribute("sandbox") &&
        (SandboxPolicy.ParseIframeSandboxFlags(_embeddingFrameElement.GetAttribute("sandbox")) & IframeSandboxFlags.SameOrigin) == 0;

    private JsValue CallEmeHook(string name, params JsValue[] args)
    {
        if (_realmAbandoned || _interpreter == null)
        {
            return JsValue.Undefined;
        }

        var hook = ReadGlobalValueOrUndefined(name);
        if (!_interpreter.CanCallValue(hook))
        {
            return JsValue.Undefined;
        }

        try
        {
            return _interpreter.InvokeFunction(hook, args, JsValue.Undefined);
        }
        catch (Js.Interpreter.JsThrownException ex)
        {
            EngineLogCompat.Warn($"[FenJsBridge] {name} failed: {ex.Description ?? ex.Message}", LogCategory.JavaScript);
            return JsValue.Undefined;
        }
    }

    private EmeKeysEntry EmeKeysFor(IReadOnlyList<JsValue> args, int index)
    {
        if (index < args.Count && args[index].Tag is JsValueTag.Int32 or JsValueTag.Number
            && _emeKeys.TryGetValue((int)args[index].AsNumber(), out var entry))
        {
            return entry;
        }

        ThrowDomException("InvalidStateError", "The MediaKeys object is gone.");
        return null;
    }

    private ClearKeySession EmeSessionFor(IReadOnlyList<JsValue> args, int index)
    {
        if (index < args.Count && args[index].Tag is JsValueTag.Int32 or JsValueTag.Number
            && _emeSessions.TryGetValue((int)args[index].AsNumber(), out var session))
        {
            return session;
        }

        ThrowDomException("InvalidStateError", "The MediaKeySession is closed.");
        return null;
    }

    /// <summary>Turns the model's refusal into the exception EME names, or hands back a value.</summary>
    private JsValue EmeResultToScript(EmeResult result, JsValue value = default)
    {
        if (result.Succeeded)
        {
            return value;
        }

        if (result.ExceptionName == "TypeError")
        {
            ThrowDomException("TypeError", result.Message);
        }
        else
        {
            ThrowDomException(result.ExceptionName, result.Message);
        }

        return JsValue.Undefined;
    }

    private static string KeyStatusName(MediaKeyStatus status) => status switch
    {
        MediaKeyStatus.Usable => "usable",
        MediaKeyStatus.Expired => "expired",
        MediaKeyStatus.Released => "released",
        MediaKeyStatus.OutputRestricted => "output-restricted",
        MediaKeyStatus.OutputDownscaled => "output-downscaled",
        MediaKeyStatus.StatusPending => "status-pending",
        _ => "internal-error",
    };

    private static string MessageTypeName(MediaKeyMessageType type) => type switch
    {
        MediaKeyMessageType.LicenseRequest => "license-request",
        MediaKeyMessageType.LicenseRenewal => "license-renewal",
        MediaKeyMessageType.LicenseRelease => "license-release",
        _ => "individualization-request",
    };

    /// <summary>
    /// Reads the configurations the prelude serialized. The content types come through
    /// exactly as the page wrote them, because <c>getConfiguration()</c> hands them back.
    /// </summary>
    private static List<MediaKeySystemConfiguration> ParseKeySystemConfigurations(string json)
    {
        var candidates = new List<MediaKeySystemConfiguration>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return candidates;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                MediaKeySystemSupport.TryParseRequirement(ReadString(element, "distinctiveIdentifier"), out var identifier);
                MediaKeySystemSupport.TryParseRequirement(ReadString(element, "persistentState"), out var persistent);

                candidates.Add(new MediaKeySystemConfiguration
                {
                    Label = ReadString(element, "label") ?? string.Empty,
                    InitDataTypes = ReadStrings(element, "initDataTypes"),
                    AudioCapabilities = ReadCapabilities(element, "audioCapabilities"),
                    VideoCapabilities = ReadCapabilities(element, "videoCapabilities"),
                    DistinctiveIdentifier = identifier,
                    PersistentState = persistent,
                    SessionTypes = element.TryGetProperty("sessionTypes", out var types) && types.ValueKind == JsonValueKind.Array
                        ? ReadStrings(element, "sessionTypes")
                        : null,
                });
            }
        }
        catch (JsonException)
        {
            candidates.Clear();
        }

        return candidates;
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> ReadStrings(JsonElement element, string name)
    {
        var values = new List<string>();
        if (element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    values.Add(item.GetString());
                }
            }
        }

        return values;
    }

    private static List<MediaKeySystemMediaCapability> ReadCapabilities(JsonElement element, string name)
    {
        var capabilities = new List<MediaKeySystemMediaCapability>();
        if (element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                capabilities.Add(new MediaKeySystemMediaCapability(
                    ReadString(item, "contentType") ?? string.Empty,
                    ReadString(item, "robustness") ?? string.Empty,
                    ReadString(item, "encryptionScheme")));
            }
        }

        return capabilities;
    }

    private static string SerializeKeySystemConfiguration(MediaKeySystemConfiguration configuration)
    {
        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("label", configuration.Label);

            writer.WriteStartArray("initDataTypes");
            foreach (string type in configuration.InitDataTypes)
            {
                writer.WriteStringValue(type);
            }

            writer.WriteEndArray();

            WriteCapabilities(writer, "audioCapabilities", configuration.AudioCapabilities);
            WriteCapabilities(writer, "videoCapabilities", configuration.VideoCapabilities);

            writer.WriteString("distinctiveIdentifier", MediaKeySystemSupport.ToName(configuration.DistinctiveIdentifier));
            writer.WriteString("persistentState", MediaKeySystemSupport.ToName(configuration.PersistentState));

            writer.WriteStartArray("sessionTypes");
            foreach (string type in configuration.SessionTypes ?? new List<string> { "temporary" })
            {
                writer.WriteStringValue(type);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteCapabilities(Utf8JsonWriter writer, string name, IReadOnlyList<MediaKeySystemMediaCapability> capabilities)
    {
        writer.WriteStartArray(name);
        foreach (var capability in capabilities)
        {
            writer.WriteStartObject();
            writer.WriteString("contentType", capability.ContentType);
            writer.WriteString("robustness", capability.Robustness);
            if (capability.EncryptionScheme != null)
            {
                writer.WriteString("encryptionScheme", capability.EncryptionScheme);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
