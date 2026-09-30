using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.WebAPIs.WebAuthn;
using FenBrowser.Js.Builtins;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// The native half of Web Authentication (WebAuthn L3 §5.1): the page's publicKey options
/// arrive as JSON with BufferSources as base64url, the ceremony runs on the process's
/// <see cref="WebAuthnPlatform.Authenticator"/> (Windows Hello, directly or through the
/// broker), and the credential comes back as JSON for the prelude to wrap. The origin is
/// this document's, never the page's say-so.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private static readonly JsonSerializerOptions WebAuthnJson = new(JsonSerializerDefaults.Web);

    private void InstallFenJsWebAuthn()
    {
        Native("__fenWebAuthnAvailable", 0, _ => JsValue.FromBoolean(WebAuthnPlatform.Authenticator != null));

        // isUserVerifyingPlatformAuthenticatorAvailable() (§5.1.7).
        Native("__fenWebAuthnUvpaa", 0, noArgs =>
        {
            var (promise, resolve, _) = ((IBuiltinContext)_interpreter).CreatePromiseCapability();
            var authenticator = WebAuthnPlatform.Authenticator;
            var document = _currentDomRoot as Document ?? _currentDomRoot?.OwnerDocument;
            if (authenticator == null || document == null)
            {
                _ = _interpreter.InvokeFunction(resolve, new[] { JsValue.FromBoolean(false) }, JsValue.Undefined);
                return promise;
            }

            _ = Task.Run(async () =>
            {
                bool available;
                try
                {
                    available = await authenticator.IsUserVerifyingPlatformAuthenticatorAvailableAsync().ConfigureAwait(false);
                }
                catch
                {
                    available = false;
                }

                QueueMediaTask(document, () =>
                    _ = _interpreter.InvokeFunction(resolve, new[] { JsValue.FromBoolean(available) }, JsValue.Undefined));
            });
            return promise;
        });

        // (kind, optionsJson) -> Promise<resultJson>: kind is "get" or "create".
        Native("__fenWebAuthn", 2, args =>
        {
            var (promise, resolve, _) = ((IBuiltinContext)_interpreter).CreatePromiseCapability();
            string kind = args.Count > 0 ? CoerceToHostString(args[0]) : null;
            string json = args.Count > 1 ? CoerceToHostString(args[1]) : null;
            var authenticator = WebAuthnPlatform.Authenticator;
            var document = _currentDomRoot as Document ?? _currentDomRoot?.OwnerDocument;

            void Settle(WebAuthnResult result) =>
                _ = _interpreter.InvokeFunction(
                    resolve,
                    new[] { JsValue.FromString(JsonSerializer.Serialize(result, WebAuthnJson)) },
                    JsValue.Undefined);

            string origin = TryResolveCurrentOrigin(null);
            if (authenticator == null || document == null || string.IsNullOrEmpty(json) || string.IsNullOrEmpty(origin))
            {
                Settle(WebAuthnResult.NotAllowed());
                return promise;
            }

            // WebAuthn's permissions-policy features default to 'self': a cross-origin
            // subframe may not run a ceremony.
            if (_parentRealmOwner != null &&
                !string.Equals(
                    WebAuthnClient.SerializeOrigin(origin),
                    WebAuthnClient.SerializeOrigin(_parentRealmOwner.TryResolveCurrentOrigin(null)),
                    StringComparison.OrdinalIgnoreCase))
            {
                Settle(WebAuthnResult.Failure("NotAllowedError", "The 'publickey-credentials' feature is not enabled in this document."));
                return promise;
            }

            Task<WebAuthnResult> ceremony;
            try
            {
                if (string.Equals(kind, "create", StringComparison.Ordinal))
                {
                    var request = JsonSerializer.Deserialize<WebAuthnCreateRequest>(json, WebAuthnJson) ?? new WebAuthnCreateRequest();
                    request.Origin = origin;
                    ceremony = authenticator.MakeCredentialAsync(request, CancellationToken.None);
                }
                else
                {
                    var request = JsonSerializer.Deserialize<WebAuthnGetRequest>(json, WebAuthnJson) ?? new WebAuthnGetRequest();
                    request.Origin = origin;
                    ceremony = authenticator.GetAssertionAsync(request, CancellationToken.None);
                }
            }
            catch (JsonException ex)
            {
                Settle(WebAuthnResult.Failure("TypeError", ex.Message));
                return promise;
            }

            _ = ceremony.ContinueWith(task =>
            {
                var result = task.Status == TaskStatus.RanToCompletion && task.Result != null
                    ? task.Result
                    : WebAuthnResult.NotAllowed();
                QueueMediaTask(document, () => Settle(result));
            }, TaskScheduler.Default);

            return promise;
        });

        void Native(string name, int length, Func<IReadOnlyList<JsValue>, JsValue> body) =>
            _interpreter.RegisterGlobalValue(name, _interpreter.AllocateNativeFunction(name, (_, args) => body(args), length: length));
    }
}
