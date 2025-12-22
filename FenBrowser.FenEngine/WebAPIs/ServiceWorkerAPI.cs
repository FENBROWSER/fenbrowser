using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Service Workers API implementation
    /// Provides offline capabilities and background sync
    /// </summary>
    public class ServiceWorkerAPI
    {
        private static readonly Dictionary<string, ServiceWorkerRegistration> _registrations = new();
        private static ServiceWorkerContainer _container;

        /// <summary>
        /// Creates the navigator.serviceWorker object
        /// </summary>
        public static FenObject CreateServiceWorkerContainer()
        {
            _container = new ServiceWorkerContainer();
            var container = new FenObject();

            // navigator.serviceWorker.register(scriptURL, options)
            container.Set("register", FenValue.FromFunction(new FenFunction("register", (args, thisVal) =>
            {
                if (args.Length < 1) return FenValue.Undefined;
                var scriptUrl = args[0].ToString();
                var scope = "/";
                
                if (args.Length > 1 && args[1].IsObject)
                {
                    var options = args[1].AsObject();
                    var scopeVal = options.Get("scope");
                    if (scopeVal != null && scopeVal.IsString)
                        scope = scopeVal.ToString();
                }

                FenLogger.Debug($"[ServiceWorker] Registering: {scriptUrl} with scope: {scope}", LogCategory.JavaScript);
                
                // Create registration
                var registration = new ServiceWorkerRegistration(scriptUrl, scope);
                _registrations[scope] = registration;

                // Return a Promise-like thenable
                var thenable = new FenObject();
                thenable.Set("then", FenValue.FromFunction(new FenFunction("then", (thenArgs, thenThis) =>
                {
                    if (thenArgs.Length > 0 && thenArgs[0].IsFunction)
                    {
                        var callback = thenArgs[0].AsFunction();
                        // Schedule callback with registration object
                        Task.Run(() =>
                        {
                            try
                            {
                                callback.Invoke(new IValue[] { FenValue.FromObject(registration.ToFenObject()) }, null);
                            }
                            catch (Exception ex)
                            {
                                FenLogger.Debug($"[ServiceWorker] Register callback error: {ex.Message}", LogCategory.Errors);
                            }
                        });
                    }
                    return FenValue.FromObject(thenable);
                })));
                thenable.Set("catch", FenValue.FromFunction(new FenFunction("catch", (catchArgs, catchThis) => FenValue.FromObject(thenable))));

                return FenValue.FromObject(thenable);
            })));

            // navigator.serviceWorker.ready (Promise that resolves when SW is active)
            container.Set("ready", FenValue.FromFunction(new FenFunction("ready", (args, thisVal) =>
            {
                var thenable = new FenObject();
                thenable.Set("then", FenValue.FromFunction(new FenFunction("then", (thenArgs, thenThis) =>
                {
                    if (thenArgs.Length > 0 && _registrations.Count > 0)
                    {
                        var callback = thenArgs[0].AsFunction();
                        var firstReg = new List<ServiceWorkerRegistration>(_registrations.Values)[0];
                        callback?.Invoke(new IValue[] { FenValue.FromObject(firstReg.ToFenObject()) }, null);
                    }
                    return FenValue.FromObject(thenable);
                })));
                return FenValue.FromObject(thenable);
            })));

            // navigator.serviceWorker.controller
            container.Set("controller", FenValue.Null);

            // navigator.serviceWorker.getRegistration(scope)
            container.Set("getRegistration", FenValue.FromFunction(new FenFunction("getRegistration", (args, thisVal) =>
            {
                var scope = args.Length > 0 ? args[0].ToString() : "/";
                if (_registrations.TryGetValue(scope, out var reg))
                    return FenValue.FromObject(reg.ToFenObject());
                return FenValue.Undefined;
            })));

            // navigator.serviceWorker.getRegistrations()
            container.Set("getRegistrations", FenValue.FromFunction(new FenFunction("getRegistrations", (args, thisVal) =>
            {
                var arr = new FenObject();
                int i = 0;
                foreach (var reg in _registrations.Values)
                {
                    arr.Set(i.ToString(), FenValue.FromObject(reg.ToFenObject()));
                    i++;
                }
                arr.Set("length", FenValue.FromNumber(i));
                return FenValue.FromObject(arr);
            })));

            return container;
        }
    }

    /// <summary>
    /// Service Worker Registration
    /// </summary>
    public class ServiceWorkerRegistration
    {
        public string ScriptUrl { get; }
        public string Scope { get; }
        public string State { get; private set; } = "installing";
        public DateTime RegisteredAt { get; } = DateTime.UtcNow;

        public ServiceWorkerRegistration(string scriptUrl, string scope)
        {
            ScriptUrl = scriptUrl;
            Scope = scope;
            // Simulate activation
            Task.Run(async () =>
            {
                await Task.Delay(100);
                State = "installed";
                await Task.Delay(100);
                State = "activated";
            });
        }

        public FenObject ToFenObject()
        {
            var obj = new FenObject();
            obj.Set("scope", FenValue.FromString(Scope));
            obj.Set("updateViaCache", FenValue.FromString("imports"));
            
            // active ServiceWorker
            var activeWorker = new FenObject();
            activeWorker.Set("scriptURL", FenValue.FromString(ScriptUrl));
            activeWorker.Set("state", FenValue.FromString(State));
            obj.Set("active", FenValue.FromObject(activeWorker));
            
            // installing/waiting (null for now)
            obj.Set("installing", FenValue.Null);
            obj.Set("waiting", FenValue.Null);

            // Methods
            obj.Set("update", FenValue.FromFunction(new FenFunction("update", (args, thisVal) => 
            {
                FenLogger.Debug("[ServiceWorker] update() called", LogCategory.JavaScript);
                return FenValue.Undefined;
            })));

            obj.Set("unregister", FenValue.FromFunction(new FenFunction("unregister", (args, thisVal) =>
            {
                FenLogger.Debug("[ServiceWorker] unregister() called", LogCategory.JavaScript);
                return FenValue.FromBoolean(true);
            })));

            return obj;
        }
    }

    /// <summary>
    /// Service Worker Container (internal)
    /// </summary>
    public class ServiceWorkerContainer
    {
        public ServiceWorkerRegistration Controller { get; set; }
    }

    /// <summary>
    /// FetchEvent - dispatched when a controlled page makes a network request
    /// </summary>
    public class FetchEvent : FenObject
    {
        public FenObject Request { get; }
        public bool DefaultPrevented { get; private set; }
        public IValue ResponseValue { get; private set; }
        private readonly TaskCompletionSource<IValue> _responseSource = new();

        public FetchEvent(string url, string method = "GET")
        {
            // Build Request object
            Request = new FenObject();
            Request.Set("url", FenValue.FromString(url));
            Request.Set("method", FenValue.FromString(method));
            Request.Set("headers", FenValue.FromObject(new FenObject())); // Empty headers

            // Expose request property
            Set("request", FenValue.FromObject(Request));

            // respondWith(response) - allows SW to provide custom response
            Set("respondWith", FenValue.FromFunction(new FenFunction("respondWith", (args, thisVal) =>
            {
                if (args.Length > 0)
                {
                    DefaultPrevented = true;
                    ResponseValue = args[0];
                    _responseSource.TrySetResult(args[0]);
                    FenLogger.Debug($"[ServiceWorker] FetchEvent.respondWith called", LogCategory.JavaScript);
                }
                return FenValue.Undefined;
            })));

            // waitUntil(promise) - extend lifetime (stub for now)
            Set("waitUntil", FenValue.FromFunction(new FenFunction("waitUntil", (args, thisVal) =>
            {
                // No-op for now, just log
                FenLogger.Debug("[ServiceWorker] FetchEvent.waitUntil called", LogCategory.JavaScript);
                return FenValue.Undefined;
            })));
        }

        /// <summary>
        /// Wait for respondWith to be called (with timeout)
        /// </summary>
        public async Task<IValue> WaitForResponseAsync(int timeoutMs = 5000)
        {
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(_responseSource.Task, timeoutTask);
            if (completedTask == timeoutTask)
            {
                return null; // Timeout, proceed to network
            }
            return await _responseSource.Task;
        }
    }

    /// <summary>
    /// ServiceWorkerGlobalScope - the execution context for a Service Worker script
    /// </summary>
    public class ServiceWorkerGlobalScope
    {
        private readonly ServiceWorkerRegistration _registration;
        private readonly List<FenFunction> _fetchListeners = new();
        private FenObject _self;

        public ServiceWorkerGlobalScope(ServiceWorkerRegistration registration)
        {
            _registration = registration;
            _self = new FenObject();

            // self.addEventListener('fetch', handler)
            _self.Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
            {
                if (args.Length >= 2 && args[0].ToString() == "fetch" && args[1].IsFunction)
                {
                    _fetchListeners.Add(args[1].AsFunction());
                    FenLogger.Debug("[ServiceWorker] Registered 'fetch' event listener", LogCategory.JavaScript);
                }
                return FenValue.Undefined;
            })));

            // self.registration
            _self.Set("registration", FenValue.FromObject(registration.ToFenObject()));

            // self.skipWaiting()
            _self.Set("skipWaiting", FenValue.FromFunction(new FenFunction("skipWaiting", (args, thisVal) =>
            {
                FenLogger.Debug("[ServiceWorker] skipWaiting() called", LogCategory.JavaScript);
                return FenValue.Undefined;
            })));

            // self.clients
            var clients = new FenObject();
            clients.Set("claim", FenValue.FromFunction(new FenFunction("claim", (args, thisVal) =>
            {
                FenLogger.Debug("[ServiceWorker] clients.claim() called", LogCategory.JavaScript);
                return FenValue.Undefined;
            })));
            _self.Set("clients", FenValue.FromObject(clients));
        }

        public FenObject Self => _self;

        /// <summary>
        /// Dispatch a FetchEvent to all registered listeners
        /// </summary>
        public async Task<IValue> DispatchFetchEventAsync(FetchEvent evt)
        {
            foreach (var listener in _fetchListeners)
            {
                try
                {
                    listener.Invoke(new IValue[] { FenValue.FromObject(evt) }, null);
                }
                catch (Exception ex)
                {
                    FenLogger.Debug($"[ServiceWorker] Fetch listener error: {ex.Message}", LogCategory.Errors);
                }
            }

            // Wait for respondWith
            if (_fetchListeners.Count > 0)
            {
                return await evt.WaitForResponseAsync(100); // Short timeout for read-only
            }
            return null;
        }
    }

    /// <summary>
    /// ServiceWorkerInterceptor - hooks into network layer
    /// </summary>
    public static class ServiceWorkerInterceptor
    {
        private static readonly Dictionary<string, ServiceWorkerGlobalScope> _activeScopes = new();

        /// <summary>
        /// Register an active scope for a given origin/scope path
        /// </summary>
        public static void RegisterScope(string scope, ServiceWorkerGlobalScope globalScope)
        {
            _activeScopes[scope] = globalScope;
            FenLogger.Debug($"[ServiceWorker] Scope registered: {scope}", LogCategory.JavaScript);
        }

        /// <summary>
        /// Check if URL is controlled by a ServiceWorker and dispatch FetchEvent
        /// </summary>
        public static async Task<IValue> InterceptAsync(string url)
        {
            // Find matching scope (simple prefix match)
            foreach (var kvp in _activeScopes)
            {
                if (url.StartsWith(kvp.Key) || kvp.Key == "/")
                {
                    var evt = new FetchEvent(url);
                    var response = await kvp.Value.DispatchFetchEventAsync(evt);
                    if (response != null)
                    {
                        FenLogger.Debug($"[ServiceWorker] Intercepted: {url}", LogCategory.JavaScript);
                        return response;
                    }
                }
            }
            return null; // No interception, proceed to network
        }

        /// <summary>
        /// Clear all registered scopes (for testing)
        /// </summary>
        public static void ClearScopes()
        {
            _activeScopes.Clear();
        }
    }
}
