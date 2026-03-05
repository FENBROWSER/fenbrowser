using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Represents the ServiceWorkerContainer interface (navigator.serviceWorker).
    /// </summary>
    public class ServiceWorkerContainer : FenObject
    {
        private readonly string _origin;

        public ServiceWorkerContainer(string origin)
        {
            _origin = origin;
            InitializeInterface();
        }

        private void InitializeInterface()
        {
            Set("register", FenValue.FromFunction(new FenFunction("register", Register)));
            Set("getRegistration", FenValue.FromFunction(new FenFunction("getRegistration", GetRegistration)));
            
            // controller property
            // This needs to be a getter that queries the Manager
            // For now, implementing as a method-driven property update or simplified getter-like behavior if possible.
            // Since FenObject stores values, we need to update 'controller' whenever it changes.
            // Alternatively, the JS engine binding layer would handle getters.
            Set("controller", FenValue.Null); // Updated by page logic or event loop
            
            // ready property (Promise)
            var readyPromise = new FenObject();
            Set("ready", FenValue.FromObject(readyPromise));
        }

        public void UpdateController(ServiceWorker worker)
        {
             Set("controller", worker != null ? FenValue.FromObject(worker) : FenValue.Null);
        }

        private FenValue Register(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1) return FenValue.Undefined; // Reject

            var scriptUrl = args[0].ToString();
            var scriptUri = ResolveAndValidateScriptUri(scriptUrl);
            if (scriptUri == null)
            {
                return FenValue.FromObject(CreatePromise(() =>
                    Task.FromException<FenValue>(new InvalidOperationException($"Invalid service worker script URL: {scriptUrl}"))));
            }
             
            var options = args.Length > 1 ? args[1].AsObject() : null;
            var scopeVal = options != null ? options.Get("scope") : FenValue.Undefined;
            var rawScope = !scopeVal.IsUndefined ? scopeVal.ToString() : "./";
            var scope = ResolveAndValidateScope(rawScope, scriptUri);
            if (scope == null)
            {
                return FenValue.FromObject(CreatePromise(() =>
                    Task.FromException<FenValue>(new InvalidOperationException($"Invalid service worker scope: {rawScope}"))));
            }

            // Return Promise
            return FenValue.FromObject(CreatePromise(async () =>
            {
                var reg = await ServiceWorkerManager.Instance.Register(scriptUri.AbsoluteUri, scope).ConfigureAwait(false);
                return FenValue.FromObject(reg);
            }));
        }

        private FenValue GetRegistration(FenValue[] args, FenValue thisVal)
        {
            var rawScope = args.Length > 0 ? args[0].ToString() : "/";
            var scope = ResolveAndValidateScope(rawScope, ResolveOriginRoot());
            if (scope == null)
            {
                return FenValue.FromObject(CreatePromise(() =>
                    Task.FromException<FenValue>(new InvalidOperationException($"Invalid registration scope: {rawScope}"))));
            }
             
            return FenValue.FromObject(CreatePromise(async () => {
                 // In reality async check
                 var reg = ServiceWorkerManager.Instance.GetRegistration(scope);
                 return reg != null ? FenValue.FromObject(reg) : FenValue.Undefined;
            })); 
        }


        private static Task RunDetachedAsync(Func<Task> operation)
        {
            return Task.Factory.StartNew(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ServiceWorker] Detached async operation failed: {ex.Message}");
                }
            }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }        // --- Promise Helper (Reuse) ---
        private FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            var promise = new FenObject();
            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    var result = await valueFactory();
                    ResolvePromise(promise, result);
                }
                catch (Exception ex)
                {
                    RejectPromise(promise, ex.Message);
                }
            });
            SetupPromiseThen(promise);
            return promise;
        }

        private void ResolvePromise(FenObject promise, FenValue result)
        {
             if (promise.Has("onFulfilled"))
             {
                 var cb = promise.Get("onFulfilled").AsFunction();
                 cb?.Invoke(new[] { result }, null);
             }
             else
             {
                 promise.Set("__result", result);
                 promise.Set("__state", FenValue.FromString("fulfilled"));
             }
        }

        private void RejectPromise(FenObject promise, string error)
        {
             if (promise.Has("onRejected"))
             {
                 var cb = promise.Get("onRejected").AsFunction();
                 cb?.Invoke(new[] { FenValue.FromString(error) }, null);
             }
             else
             {
                 promise.Set("__reason", FenValue.FromString(error));
                 promise.Set("__state", FenValue.FromString("rejected"));
             }
        }

        private void SetupPromiseThen(FenObject promise)
        {
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, _) =>
            {
                if (args.Length > 0) promise.Set("onFulfilled", args[0]);
                if (args.Length > 1) promise.Set("onRejected", args[1]);

                var stateVal = promise.Get("__state");
                var state = !stateVal.IsUndefined ? stateVal.ToString() : null;
                if (state == "fulfilled")
                {
                    var res = promise.Get("__result");
                    if (args.Length > 0 && args[0].IsFunction) args[0].AsFunction().Invoke(new[] { res }, null);
                }
                else if (state == "rejected")
                {
                     var reason = promise.Get("__reason");
                     if (args.Length > 1 && args[1].IsFunction) args[1].AsFunction().Invoke(new[] { reason }, null);
                }

                return FenValue.FromObject(promise); 
            })));
        }

        private Uri ResolveOriginRoot()
        {
            if (!Uri.TryCreate(_origin, UriKind.Absolute, out var originUri))
            {
                return null;
            }

            return new UriBuilder(originUri)
            {
                Path = "/",
                Query = string.Empty,
                Fragment = string.Empty
            }.Uri;
        }

        private Uri ResolveAndValidateScriptUri(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            var originRoot = ResolveOriginRoot();
            if (originRoot == null)
            {
                return null;
            }

            if (!Uri.TryCreate(originRoot, candidate, out var resolved))
            {
                return null;
            }

            if (!IsHttpScheme(resolved) || !IsSameOrigin(originRoot, resolved))
            {
                return null;
            }

            return new UriBuilder(resolved)
            {
                Query = string.Empty,
                Fragment = string.Empty
            }.Uri;
        }

        private string ResolveAndValidateScope(string scopeValue, Uri baseUri)
        {
            if (baseUri == null || !IsHttpScheme(baseUri))
            {
                return null;
            }

            var normalizedScopeInput = string.IsNullOrWhiteSpace(scopeValue) ? "./" : scopeValue;
            if (!Uri.TryCreate(baseUri, normalizedScopeInput, out var resolvedScope))
            {
                return null;
            }

            if (!IsHttpScheme(resolvedScope) || !IsSameOrigin(baseUri, resolvedScope))
            {
                return null;
            }

            var builder = new UriBuilder(resolvedScope)
            {
                Query = string.Empty,
                Fragment = string.Empty
            };
            if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
            {
                builder.Path += "/";
            }
            return builder.Uri.AbsoluteUri;
        }

        private static bool IsHttpScheme(Uri uri)
        {
            if (uri == null) return false;
            return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameOrigin(Uri left, Uri right)
        {
            if (left == null || right == null) return false;
            return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
                   left.Port == right.Port;
        }
    }
}

