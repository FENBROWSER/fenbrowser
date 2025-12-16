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
}
