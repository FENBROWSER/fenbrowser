using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Central manager for Service Workers.
    /// Handles registration, lifecycle, and runtime management.
    /// Singleton to allow access from various parts of the engine.
    /// </summary>
    public class ServiceWorkerManager
    {
        private static ServiceWorkerManager _instance;
        public static ServiceWorkerManager Instance => _instance ??= new ServiceWorkerManager();

        // Scope URL -> Registration
        private readonly ConcurrentDictionary<string, ServiceWorkerRegistration> _registrations = new();
        
        // Script URL -> Runtime
        // Note: multiple scopes could use same script, but typically 1:1 active
        // Better: Registration Scope -> Active Worker Runtime
        // For simplicity: We track runtimes by scope for now.
        private readonly ConcurrentDictionary<string, WorkerRuntime> _activeRuntimes = new();
        
        private FenBrowser.FenEngine.Storage.IStorageBackend _storageBackend;
        private Func<Uri, Task<string>> _scriptFetcher;
        private Func<Uri, bool> _scriptUriAllowed;

        private ServiceWorkerManager() { }
        
        public void Initialize(
            FenBrowser.FenEngine.Storage.IStorageBackend storageBackend,
            Func<Uri, Task<string>> scriptFetcher = null,
            Func<Uri, bool> scriptUriAllowed = null)
        {
            _storageBackend = storageBackend;
            _scriptFetcher = scriptFetcher;
            _scriptUriAllowed = scriptUriAllowed;
        }

        public async Task<ServiceWorkerRegistration> Register(string scriptUrl, string scope)
        {
            FenBrowser.Core.FenLogger.Debug($"[ServiceWorkerManager] Registering {scriptUrl} for scope {scope}", LogCategory.ServiceWorker);

            // 1. Check existing registration
            if (!_registrations.ContainsKey(scope))
            {
                _registrations[scope] = new ServiceWorkerRegistration(scope);
            }
            var registration = _registrations[scope];

            // 2. Lifecycle: "Installing"
            var sw = new ServiceWorker(scriptUrl, scope, "installing");
            registration.SetInstalling(sw);

            // 3. Spin up runtime (simplified)
            // In real browser: Download script -> byte-for-byte check -> spin up
            await Task.Run(() => StartWorkerRuntime(scriptUrl, scope, sw, registration));

            return registration;
        }

        public ServiceWorkerRegistration GetRegistration(string scope)
        {
            if (_registrations.TryGetValue(scope, out var reg)) return reg;
            // TODO: Partial match for nested scopes
            return null;
        }
        
        /// <summary>
        /// Get the active controller for a given document URL
        /// </summary>
        public ServiceWorker GetController(string documentUrl)
        {
            // Naive scope matching: Longest matching prefix
            string matchedScope = null;
            int maxLen = -1;

            foreach (var scope in _registrations.Keys)
            {
                if (documentUrl.StartsWith(scope) && scope.Length > maxLen)
                {
                    // Must have an ACTIVE worker
                    if (_registrations[scope].Active != null)
                    {
                        matchedScope = scope;
                        maxLen = scope.Length;
                    }
                }
            }

            return matchedScope != null ? _registrations[matchedScope].Active : null;
        }

        public void PostMessageToWorker(string scope, object message)
        {
            if (_activeRuntimes.TryGetValue(scope, out var runtime))
            {
                // runtime.PostMessage(message); // Needed: Dispatch 'message' event on worker global scope
                FenBrowser.Core.FenLogger.Debug($"[ServiceWorkerManager] PostMessage to scope {scope}: {message}", LogCategory.ServiceWorker);
            }
            else
            {
               FenBrowser.Core.FenLogger.Debug($"[ServiceWorkerManager] No active runtime found for scope {scope}", LogCategory.ServiceWorker);
            }
        }

        private void StartWorkerRuntime(string scriptUrl, string scope, ServiceWorker sw, ServiceWorkerRegistration reg)
        {
            // Simulate lifecycle
            try
            {
                // Installing
                FenBrowser.Core.FenLogger.Debug($"[ServiceWorkerManager] Installing {scope}", LogCategory.ServiceWorker);
                
                // Fire 'install' event in runtime (Mock)
                // If successful -> Waiting
                sw.UpdateState("installed");
                reg.SetInstalling(null);
                reg.SetWaiting(sw);

                // For testing/simplicity: Auto-Activate if no existing active worker
                if (reg.Active  == null)
                {
                    // Create actual runtime for events
                    var scriptUri = ResolveScriptUri(scriptUrl, scope);
                    if (scriptUri == null)
                        throw new InvalidOperationException($"Invalid service worker script URL: {scriptUrl}");

                    if (_scriptUriAllowed != null && !_scriptUriAllowed(scriptUri))
                        throw new UnauthorizedAccessException($"Service worker script blocked by policy: {scriptUri}");

                    var runtime = new WorkerRuntime(
                        scriptUri.ToString(),
                        scope,
                        _storageBackend,
                        _scriptFetcher,
                        _scriptUriAllowed,
                        isServiceWorker: true);
                    _activeRuntimes[scope] = runtime;
                    
                    ActivateWorker(scope, reg, sw);
                }
            }
            catch (Exception ex)
            {
                FenBrowser.Core.FenLogger.Error($"[ServiceWorkerManager] Runtime start failed: {ex.Message}", LogCategory.ServiceWorker);
                sw.UpdateState("redundant");
            }
        }

        public async Task<bool> DispatchFetchEvent(ServiceWorker sw, FenBrowser.FenEngine.WebAPIs.FetchEvent fetchEvt)
        {
            if (sw  == null) return false;
            
            // Lookup runtime by scope directly
            if (_activeRuntimes.TryGetValue(sw.Scope, out var runtime))
            {
                FenBrowser.Core.FenLogger.Debug($"[ServiceWorkerManager] Dispatch FetchEvent to {sw.Scope}", LogCategory.ServiceWorker);
                return await runtime.DispatchServiceWorkerFetchAsync(fetchEvt).ConfigureAwait(false);
            }
            return false;
        }

        private static Uri ResolveScriptUri(string scriptUrl, string scope)
        {
            if (string.IsNullOrWhiteSpace(scriptUrl))
                return null;

            if (Uri.TryCreate(scriptUrl, UriKind.Absolute, out var absolute))
                return IsHttpScheme(absolute) ? absolute : null;

            if (Uri.TryCreate(scope, UriKind.Absolute, out var scopeUri) &&
                Uri.TryCreate(scopeUri, scriptUrl, out var relative))
            {
                return IsHttpScheme(relative) ? relative : null;
            }

            return null;
        }

        private static bool IsHttpScheme(Uri uri)
        {
            if (uri == null) return false;
            return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        private void ActivateWorker(string scope, ServiceWorkerRegistration reg, ServiceWorker sw)
        {
            FenBrowser.Core.FenLogger.Debug($"[ServiceWorkerManager] Activating {scope}", LogCategory.ServiceWorker);
            sw.UpdateState("activating");
            
            // Fire 'activate' event
            // ...

            sw.UpdateState("activated");
            reg.SetWaiting(null);
            reg.SetActive(sw);
        }
    }
}
