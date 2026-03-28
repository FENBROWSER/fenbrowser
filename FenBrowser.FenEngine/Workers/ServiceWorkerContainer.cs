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

        // Captured resolve for the ready promise - called when a SW activates for this origin.
        private FenValue _readyResolve = FenValue.Undefined;
        private bool _readyResolved;

        // SW §4.2.2: live controller - always reads from ServiceWorkerManager.
        private ServiceWorker _cachedController;

        public ServiceWorkerContainer(string origin, IExecutionContext context = null)
        {
            _origin = origin;
            _context = context;
            InitializeInterface();

            // Register this container with the manager so it receives controllerchange notifications.
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

            // SW §4.2.2: controller is a live getter that reads from the manager.
            _cachedController = ServiceWorkerManager.Instance.GetController(_origin);
            Set("controller", _cachedController != null ? FenValue.FromObject(_cachedController) : FenValue.Null);

            Set("oncontrollerchange", FenValue.Null);

            // SW §4.2: ready is always a real promise that resolves with the first active registration.
            var readyPromise = WorkerPromise.CreatePending(_context, "ServiceWorkerContainer.ready");
            _readyResolve = readyPromise.Resolve;
            Set("ready", FenValue.FromObject(readyPromise.Promise));

            // If there's already an active registration for this origin, resolve immediately.
            if (_cachedController != null)
            {
                ResolveReady(ServiceWorkerManager.Instance.GetRegistration(_origin));
            }
        }

        /// <summary>
        /// Called when a service worker activates - resolves the ready promise.
        /// </summary>
        internal void ResolveReady(ServiceWorkerRegistration reg)
        {
            if (_readyResolved || !_readyResolve.IsFunction || reg == null)
            {
                return;
            }

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

            // SW §4.3: Fire controllerchange event if the controller actually changed.
            if (!ReferenceEquals(oldController, worker))
            {
                DispatchControllerChangeEvent();
            }
        }

        private void DispatchControllerChangeEvent()
        {
            var evt = new FenObject();
            evt.Set("type", FenValue.FromString("controllerchange"));

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
            if (args.Length < 1)
            {
                return FenValue.Undefined;
            }

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

            return FenValue.FromObject(CreatePromise(async () =>
            {
                var reg = ServiceWorkerManager.Instance.GetRegistration(scope);
                return reg != null ? FenValue.FromObject(reg) : FenValue.Undefined;
            }));
        }

        private FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            return WorkerPromise.FromTask(valueFactory, _context, nameof(ServiceWorkerContainer));
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
            if (uri == null)
            {
                return false;
            }

            return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameOrigin(Uri left, Uri right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
                   left.Port == right.Port;
        }
    }
}
