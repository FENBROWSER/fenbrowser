using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Security;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The Permissions Policy introspection API: <c>document.permissionsPolicy</c> and
/// <c>iframe.permissionsPolicy</c> (Permissions Policy, "The PermissionsPolicy interface").
/// A document's view answers for the document itself; an iframe's answers for the
/// document the frame would hold, from its container policy, so it follows the
/// <c>allow</c> attribute as it changes. Both are live: nothing is cached.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    // The features this engine enforces, by their policy-controlled feature names.
    private static readonly (string Name, PolicyControlledFeature Feature)[] PolicyFeatureNames =
    {
        ("autoplay", PolicyControlledFeature.Autoplay),
        ("battery", PolicyControlledFeature.Battery),
        ("camera", PolicyControlledFeature.Camera),
        ("clipboard-read", PolicyControlledFeature.ClipboardRead),
        ("clipboard-write", PolicyControlledFeature.ClipboardWrite),
        ("encrypted-media", PolicyControlledFeature.EncryptedMedia),
        ("fullscreen", PolicyControlledFeature.Fullscreen),
        ("gamepad", PolicyControlledFeature.Gamepad),
        ("geolocation", PolicyControlledFeature.Geolocation),
        ("microphone", PolicyControlledFeature.Microphone),
        ("payment", PolicyControlledFeature.Payment),
        ("picture-in-picture", PolicyControlledFeature.PictureInPicture),
        ("screen-wake-lock", PolicyControlledFeature.ScreenWakeLock),
        ("serial", PolicyControlledFeature.Serial),
        ("speaker-selection", PolicyControlledFeature.SpeakerSelection),
        ("usb", PolicyControlledFeature.Usb),
    };

    private static bool TryParsePolicyFeature(string name, out PolicyControlledFeature feature)
    {
        foreach (var (candidate, value) in PolicyFeatureNames)
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal))
            {
                feature = value;
                return true;
            }
        }

        feature = PolicyControlledFeature.Unknown;
        return false;
    }

    // The top-level document's declared (header) policy. A frame realm carries its
    // parent's security context, so its own header is not consulted
    // (see IsFeatureEnabledInDocument).
    private PermissionsPolicy DeclaredPolicyForThisDocument() =>
        _parentRealmOwner == null
            ? DocumentSecurityContext?.PermissionsPolicy ?? PermissionsPolicyProvider?.Invoke()
            : null;

    /// <summary>
    /// "Is feature enabled in document for origin?" for this realm's document: the
    /// feature must be enabled for the document itself, and the origin must be in the
    /// declared allowlist, or in the feature's default allowlist when none is declared.
    /// </summary>
    private bool DocumentPolicyAllows(PolicyControlledFeature feature, string origin)
    {
        if (!IsFeatureEnabledInDocument(feature))
        {
            return false;
        }

        var self = TryResolveCurrentOrigin(null);
        if (origin == null || string.Equals(origin.TrimEnd('/'), self.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var declared = DeclaredPolicyForThisDocument();
        if (declared != null && declared.Allowlists.TryGetValue(feature, out var allowlist))
        {
            return allowlist.Allows(origin, self);
        }

        return PermissionsPolicy.DefaultAllowlistAllows(feature, origin, self);
    }

    private IReadOnlyList<string> DocumentAllowlist(PolicyControlledFeature feature)
    {
        if (!IsFeatureEnabledInDocument(feature))
        {
            return Array.Empty<string>();
        }

        var self = TryResolveCurrentOrigin(null);
        var declared = DeclaredPolicyForThisDocument();
        if (declared != null && declared.Allowlists.TryGetValue(feature, out var allowlist))
        {
            if (allowlist.AllowsAll)
            {
                return new[] { "*" };
            }

            var origins = new List<string>();
            if (allowlist.AllowsSelf)
            {
                origins.Add(self);
            }

            origins.AddRange(allowlist.AllowedOrigins.Where(o => !origins.Contains(o, StringComparer.OrdinalIgnoreCase)));
            return origins;
        }

        return PermissionsPolicy.DefaultAllowlistIsAll(feature) ? new[] { "*" } : new[] { self };
    }

    // The origin the document in `frame` has or would have: its src's origin, or for
    // about:blank, srcdoc and a missing src the embedding document's.
    private string FrameDocumentOrigin(Element frame)
    {
        var self = TryResolveCurrentOrigin(null);
        if (frame.HasAttribute("srcdoc"))
        {
            return self;
        }

        var src = ResolveElementUrlProperty(frame, "src");
        if (string.IsNullOrWhiteSpace(src) ||
            !Uri.TryCreate(src, UriKind.Absolute, out var uri) ||
            string.Equals(uri.Scheme, "about", StringComparison.OrdinalIgnoreCase))
        {
            return self;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// Permissions Policy 9.7 "define an inherited policy for feature in container at
    /// origin", for the document an iframe of this document holds or would hold.
    /// </summary>
    private bool ContainerPolicyAllows(Element frame, PolicyControlledFeature feature, string origin)
    {
        if (!IsFeatureEnabledInDocument(feature))
        {
            return false;
        }

        var self = TryResolveCurrentOrigin(null);
        var frameOrigin = origin ?? FrameDocumentOrigin(frame);

        // The embedding document's declared policy decides which origins may be given
        // the feature at all.
        var declared = DeclaredPolicyForThisDocument();
        if (declared != null && declared.Allowlists.TryGetValue(feature, out var allowlist) &&
            !allowlist.Allows(frameOrigin, self))
        {
            return false;
        }

        var allow = frame.GetAttribute("allow");
        var fromAttribute = PermissionsPolicy.EvaluateContainerAllow(allow, feature, frameOrigin, self);
        if (fromAttribute.HasValue)
        {
            return fromAttribute.Value;
        }

        // HTML 4.8.5: allowfullscreen is "fullscreen *" in the container policy.
        if (feature == PolicyControlledFeature.Fullscreen && frame.HasAttribute("allowfullscreen"))
        {
            return true;
        }

        return PermissionsPolicy.DefaultAllowlistAllows(feature, frameOrigin, self);
    }

    private void InstallFenJsPermissionsPolicyApi()
    {
        Native("__fenPolicyFeatures", 0, _ =>
            _interpreter.AllocateArray(PolicyFeatureNames.Select(f => JsValue.FromString(f.Name)).ToArray()));

        // (target, feature, origin): target null for this document, else an iframe.
        Native("__fenPolicyQuery", 3, args =>
        {
            var feature = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
            if (!TryParsePolicyFeature(feature, out var parsed))
            {
                return JsValue.FromBoolean(false);
            }

            string origin = null;
            if (args.Count > 2 && args[2].Tag is not (JsValueTag.Null or JsValueTag.Undefined))
            {
                origin = PermissionsPolicy.NormalizeOrigin(CoerceToHostString(args[2]));
                if (string.IsNullOrEmpty(origin))
                {
                    return JsValue.FromBoolean(false);
                }
            }

            var frame = args.Count > 0 ? ResolveHostObjectOrNull<Element>(args[0]) : null;
            return JsValue.FromBoolean(frame != null
                ? ContainerPolicyAllows(frame, parsed, origin)
                : DocumentPolicyAllows(parsed, origin));
        });

        Native("__fenPolicyAllowlist", 2, args =>
        {
            var feature = args.Count > 1 ? CoerceToHostString(args[1]) : string.Empty;
            if (!TryParsePolicyFeature(feature, out var parsed))
            {
                return _interpreter.AllocateArray(Array.Empty<JsValue>());
            }

            var frame = args.Count > 0 ? ResolveHostObjectOrNull<Element>(args[0]) : null;
            IReadOnlyList<string> origins = frame == null
                ? DocumentAllowlist(parsed)
                : ContainerPolicyAllows(frame, parsed, null)
                    ? new[] { FrameDocumentOrigin(frame) }
                    : Array.Empty<string>();
            return _interpreter.AllocateArray(origins.Select(JsValue.FromString).ToArray());
        });

        EvaluateWithFenJsRaw(PermissionsPolicyPrelude);

        void Native(string name, int length, Func<IReadOnlyList<JsValue>, JsValue> body) =>
            _interpreter.RegisterGlobalValue(name, _interpreter.AllocateNativeFunction(name, (_, args) => body(args), length: length));
    }

    private const string PermissionsPolicyPrelude = """
        (function () {
            'use strict';
            var g = globalThis;
            if (typeof g.__fenPolicyQuery !== 'function') return;
            var query = g.__fenPolicyQuery, allowlist = g.__fenPolicyAllowlist, features = g.__fenPolicyFeatures;
            var INTERNAL = {};

            function PermissionsPolicy(key, target) {
                if (key !== INTERNAL) throw new TypeError('Illegal constructor');
                Object.defineProperty(this, '_target', { value: target });
            }
            function method(name, fn) {
                Object.defineProperty(PermissionsPolicy.prototype, name, { value: fn, writable: true, configurable: true });
            }
            method('allowsFeature', function allowsFeature(feature, origin) {
                return query(this._target, String(feature), origin === undefined ? null : String(origin));
            });
            method('features', function features_() { return features(); });
            method('allowedFeatures', function allowedFeatures() {
                var target = this._target;
                return features().filter(function (f) { return query(target, f, null); });
            });
            method('getAllowlistForFeature', function getAllowlistForFeature(feature) {
                return allowlist(this._target, String(feature));
            });
            Object.defineProperty(PermissionsPolicy.prototype, Symbol.toStringTag, { value: 'PermissionsPolicy', configurable: true });
            Object.defineProperty(g, 'PermissionsPolicy', { value: PermissionsPolicy, writable: true, configurable: true });

            var documentPolicy = new PermissionsPolicy(INTERNAL, null);
            if (typeof g.Document === 'function') {
                Object.defineProperty(g.Document.prototype, 'permissionsPolicy', {
                    get: function () { return documentPolicy; },
                    configurable: true, enumerable: true
                });
            }

            var framePolicies = new WeakMap();
            if (typeof g.HTMLIFrameElement === 'function') {
                Object.defineProperty(g.HTMLIFrameElement.prototype, 'permissionsPolicy', {
                    get: function () {
                        var policy = framePolicies.get(this);
                        if (!policy) {
                            policy = new PermissionsPolicy(INTERNAL, this);
                            framePolicies.set(this, policy);
                        }
                        return policy;
                    },
                    configurable: true, enumerable: true
                });
            }
        })();
        """;
}
