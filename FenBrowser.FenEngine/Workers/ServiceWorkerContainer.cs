using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Represents the ServiceWorkerContainer interface (navigator.serviceWorker).
    /// Spec: https://w3c.github.io/ServiceWorker/#serviceworkercontainer
    /// </summary>
    public class ServiceWorkerContainer : FenObject
    {
        private readonly string _origin;
        private readonly IExecutionContext _context;

        // Captured resolve for the ready promise — called when a SW activates for this origin
        private FenValue _readyResolve = FenValue.Undefined;
        private bool _readyResolved;

        // SW §4.2.2: live controller — always reads from ServiceWorkerManager
        private ServiceWorker _cachedController;

        public ServiceWorkerContainer(string origin, IExecutionContext context = null)
        {
            _origin = origin;
            _context = context;
            InitializeInterface();

            // Register this container with the manager so it receives controllerchange notifications
            ServiceWorkerManager.Instance.RegisterContainer(this);
        }

        /// <summary>
        /// The origin this container is scoped to, used by ServiceWorkerManager for notifications.
        /// </summary>
        internal string Origin => _origin;

        private void InitializeInterface()
        {
            Set("register", FenValue.FromFunction(new FenFunction("register", Register)));
            Set("getRegistration", FenValue.FromFunction(new FenFunction("getRegistration", GetRegistration)));
            Set("getRegistrations", FenValue.FromFunction(new FenFunction("getRegistrations", GetRegistrations)));

            // SW §4.2.2: controller is a live getter that reads from the manager
            _cachedController = ServiceWorkerManager.Instance.GetController(_origin);
            Set("controller", _cachedController != null ? FenValue.FromObject(_cachedController) : FenValue.Null);

            Set("oncontrollerchange", FenValue.Null);

            // SW §4.2: ready is a promise that resolves with the first active registration
            if (_context != null)
            {
                FenValue capturedResolve = FenValue.Undefined;
                var executor = new FenFunction("executor", (args, thisVal) =>
                {
                    capturedResolve = args.Length > 0 ? args[0] : FenValue.Undefined;
                    return FenValue.Undefined;
                });
                var readyPromise = new JsPromise(FenValue.FromFunction(executor), _context);
                _readyResolve = capturedResolve;
                Set("ready", FenValue.FromObject(readyPromise));

                // If there's already an active registration for this origin, resolve immediately
                if (_cachedController != null)
                {
                    ResolveReady(ServiceWorkerManager.Instance.GetRegistration(_origin));
                }
            }
            else
            {
                Set("ready", FenValue.FromObject(new FenObject()));
            }
        }

        /// <summary>
        /// Called when a service worker activates — resolves the ready promise.
        /// </summary>
        internal void ResolveReady(ServiceWorkerRegistration reg)
        {
            if (_readyResolved || !_readyResolve.IsFunction || reg == null) return;
            _readyResolved = true;
            _readyResolve.AsFunction().Invoke(new[] { FenValue.FromObject(reg) }, _context);
        }

        /// <summary>
        /// SW §4.2.2: Called by ServiceWorkerManager when the active worker changes for this origin.
        /// Updates the live controller property and fires controllerchange.
        /// </summary>
        public void UpdateController(ServiceWorker worker)
        {
            var oldController = _cachedController;
            _cachedController = worker;
            Set("controller", worker != null ? FenValue.FromObject(worker) : FenValue.Null);

            // SW §4.3: Fire controllerchange event if the controller actually changed
            if (!ReferenceEquals(oldController, worker))
            {
                DispatchControllerChangeEvent();
            }
        }

        private void DispatchControllerChangeEvent()
        {
            var evt = new FenObject();
            evt.Set("type", FenValue.FromString("controllerchange"));

            // Fire oncontrollerchange property handler
            var handler = Get("oncontrollerchange");
            if (handler.IsFunction)
            {
                handler.AsFunction().Invoke(new[] { FenValue.FromObject(evt) }, _context);
            }
        }

        private FenValue GetRegistrations(FenValue[] args, FenValue thisVal)
        {
            return FenValue.FromObject(CreatePromise(() =>
            {
                var registrations = ServiceWorkerManager.Instance.GetRegistrationsForOrigin(_origin);
                var arr = new FenObject();
                arr.InternalClass = "Array";
                for (int i = 0; i < registrations.Count; i++)
                {
                    arr.Set(i.ToString(), FenValue.FromObject(registrations[i]));
                }
                arr.Set("length", FenValue.FromNumber(registrations.Count));
                return Task.FromResult(FenValue.FromObject(arr));
            }));
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
                    FenBrowser.Core.FenLogger.Warn($"[ServiceWorker] Detached async operation failed: {ex.Message}",
                        FenBrowser.Core.Logging.LogCategory.ServiceWorker);
                }
            }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        /// <summary>
        /// Creates a spec-compliant JsPromise when IExecutionContext is available,
        /// falling back to a hand-rolled thenable for standalone/test contexts.
        /// </summary>
        private FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            if (_context != null)
            {
                FenValue capturedResolve = FenValue.Undefined;
                FenValue capturedReject = FenValue.Undefined;
                var executor = new FenFunction("executor", (args, thisVal) =>
                {
                    capturedResolve = args.Length > 0 ? args[0] : FenValue.Undefined;
                    capturedReject = args.Length > 1 ? args[1] : FenValue.Undefined;
                    return FenValue.Undefined;
                });
                var jsPromise = new JsPromise(FenValue.FromFunction(executor), _context);
                _ = RunDetachedAsync(async () =>
                {
                    try
                    {
                        var value = await valueFactory().ConfigureAwait(false);
                        if (capturedResolve.IsFunction)
                            capturedResolve.AsFunction().Invoke(new[] { value }, _context);
                    }
                    catch (Exception ex)
                    {
                        if (capturedReject.IsFunction)
                            capturedReject.AsFunction().Invoke(new[] { FenValue.FromString(ex.Message) }, _context);
                    }
                });
                return jsPromise;
            }

            // Fallback: hand-rolled promise for standalone/test contexts
            var promise = new FenObject();
            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    var result = await valueFactory();
                    promise.Set("__result", result);
                    promise.Set("__state", FenValue.FromString("fulfilled"));
                    var onFulfilled = promise.Get("onFulfilled");
                    if (onFulfilled.IsFunction)
                        onFulfilled.AsFunction().Invoke(new[] { result }, null);
                }
                catch (Exception ex)
                {
                    promise.Set("__reason", FenValue.FromString(ex.Message));
                    promise.Set("__state", FenValue.FromString("rejected"));
                    var onRejected = promise.Get("onRejected");
                    if (onRejected.IsFunction)
                        onRejected.AsFunction().Invoke(new[] { FenValue.FromString(ex.Message) }, null);
                }
            });

            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, _) =>
            {
                if (args.Length > 0 && args[0].IsFunction) promise.Set("onFulfilled", args[0]);
                if (args.Length > 1 && args[1].IsFunction) promise.Set("onRejected", args[1]);
                var stateVal = promise.Get("__state");
                var state = !stateVal.IsUndefined ? stateVal.ToString() : null;
                if (state == "fulfilled" && args.Length > 0 && args[0].IsFunction)
                    args[0].AsFunction().Invoke(new[] { promise.Get("__result") }, null);
                else if (state == "rejected" && args.Length > 1 && args[1].IsFunction)
                    args[1].AsFunction().Invoke(new[] { promise.Get("__reason") }, null);
                return FenValue.FromObject(promise);
            })));
            promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, _) =>
            {
                if (args.Length > 0 && args[0].IsFunction) promise.Set("onRejected", args[0]);
                var stateVal = promise.Get("__state");
                if (!stateVal.IsUndefined && stateVal.ToString() == "rejected" && args[0].IsFunction)
                    args[0].AsFunction().Invoke(new[] { promise.Get("__reason") }, null);
                return FenValue.FromObject(promise);
            })));
            return promise;
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

