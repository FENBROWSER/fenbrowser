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

            var normalizedScope = NormalizeScope(scope);
            if (normalizedScope == null)
                throw new ArgumentException($"Invalid service worker scope: {scope}", nameof(scope));

            // 1. Check existing registration
            if (!_registrations.ContainsKey(normalizedScope))
            {
                _registrations[normalizedScope] = new ServiceWorkerRegistration(normalizedScope);
            }
            var registration = _registrations[normalizedScope];

            var scriptUri = ResolveScriptUri(scriptUrl, normalizedScope);
            if (scriptUri == null)
                throw new InvalidOperationException($"Invalid service worker script URL: {scriptUrl}");

            if (!IsSameOrigin(scriptUri, new Uri(normalizedScope, UriKind.Absolute)))
                throw new UnauthorizedAccessException($"Cross-origin service worker script blocked: {scriptUri}");

            // 2. Lifecycle: "Installing"
            var sw = new ServiceWorker(scriptUri.AbsoluteUri, normalizedScope, "installing");
            registration.SetInstalling(sw);

            // 3. Spin up runtime (simplified)
            // In real browser: Download script -> byte-for-byte check -> spin up
            await Task.Run(() => StartWorkerRuntime(scriptUri.AbsoluteUri, normalizedScope, sw, registration));

            return registration;
        }

        public ServiceWorkerRegistration GetRegistration(string scope)
        {
            var normalizedScope = NormalizeScope(scope);
            if (normalizedScope == null)
                return null;

            if (_registrations.TryGetValue(normalizedScope, out var reg))
                return reg;

            // Longest prefix match for nested scopes.
            // Example: query /app/blog/post should match /app/blog/ before /app/.
            ServiceWorkerRegistration matched = null;
            var maxLen = -1;
            foreach (var kvp in _registrations)
            {
                if (!normalizedScope.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (kvp.Key.Length > maxLen)
                {
                    matched = kvp.Value;
                    maxLen = kvp.Key.Length;
                }
            }

            if (matched != null)
                return matched;

            return null;
        }
        
        /// <summary>
        /// Get the active controller for a given document URL
        /// </summary>
        public ServiceWorker GetController(string documentUrl)
        {
            var normalizedDocumentUrl = NormalizeUrlForMatch(documentUrl);
            if (normalizedDocumentUrl == null)
                return null;

            // Naive scope matching: Longest matching prefix
            string matchedScope = null;
            int maxLen = -1;

            foreach (var scope in _registrations.Keys)
            {
                if (normalizedDocumentUrl.StartsWith(scope, StringComparison.OrdinalIgnoreCase) && scope.Length > maxLen)
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

        public async Task<ServiceWorkerRegistration> UpdateRegistrationAsync(string scope)
        {
            var registration = GetRegistration(scope);
            if (registration == null)
                throw new InvalidOperationException($"No service worker registration for scope: {scope}");

            var activeScript = registration.Active?.ScriptURL ?? registration.Waiting?.ScriptURL ?? registration.Installing?.ScriptURL;
            if (string.IsNullOrWhiteSpace(activeScript))
                throw new InvalidOperationException($"No script URL available for registration scope: {registration.Scope}");

            return await Register(activeScript, registration.Scope).ConfigureAwait(false);
        }

        public Task<bool> UnregisterAsync(string scope)
        {
            var registration = GetRegistration(scope);
            if (registration == null)
                return Task.FromResult(false);

            var key = registration.Scope;
            if (!_registrations.TryRemove(key, out var removed))
                return Task.FromResult(false);

            if (_activeRuntimes.TryRemove(key, out var runtime))
            {
                try
                {
                    runtime.Terminate();
                    runtime.Dispose();
                }
                catch (Exception ex) { FenBrowser.Core.FenLogger.Warn($"[ServiceWorkerManager] Runtime disposal failed: {ex.Message}", LogCategory.ServiceWorker); }
            }

            removed.Installing?.UpdateState("redundant");
            removed.Waiting?.UpdateState("redundant");
            removed.Active?.UpdateState("redundant");
            removed.SetInstalling(null);
            removed.SetWaiting(null);
            removed.SetActive(null);

            FenBrowser.Core.FenLogger.Debug($"[ServiceWorkerManager] Unregistered scope {key}", LogCategory.ServiceWorker);
            return Task.FromResult(true);
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
                var scriptUri = ResolveScriptUri(scriptUrl, scope);
                if (scriptUri == null)
                    throw new InvalidOperationException($"Invalid service worker script URL: {scriptUrl}");

                if (_scriptUriAllowed != null && !_scriptUriAllowed(scriptUri))
                    throw new UnauthorizedAccessException($"Service worker script blocked by policy: {scriptUri}");

                if (reg.Active  == null)
                {
                    // Create actual runtime for events
                    var runtime = new WorkerRuntime(
                        scriptUri.ToString(),
                        scope,
                        _storageBackend,
                        _scriptFetcher,
                        _scriptUriAllowed,
                        isServiceWorker: true);
                    ActivateWorker(scope, reg, sw, runtime);
                }
                else
                {
                    // Replace active worker with updated script/runtime.
                    var runtime = new WorkerRuntime(
                        scriptUri.AbsoluteUri,
                        scope,
                        _storageBackend,
                        _scriptFetcher,
                        _scriptUriAllowed,
                        isServiceWorker: true);
                    ActivateWorker(scope, reg, sw, runtime);
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

        private static string NormalizeScope(string scope)
        {
            if (!Uri.TryCreate(scope, UriKind.Absolute, out var scopeUri))
                return null;

            if (!IsHttpScheme(scopeUri))
                return null;

            var builder = new UriBuilder(scopeUri)
            {
                Query = string.Empty,
                Fragment = string.Empty
            };

            var path = builder.Path ?? "/";
            if (!path.EndsWith("/", StringComparison.Ordinal))
            {
                path += "/";
            }

            builder.Path = path;
            return builder.Uri.AbsoluteUri;
        }

        private static string NormalizeUrlForMatch(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;

            if (!IsHttpScheme(uri))
                return null;

            return new UriBuilder(uri)
            {
                Query = string.Empty,
                Fragment = string.Empty
            }.Uri.AbsoluteUri;
        }

        private static bool IsSameOrigin(Uri left, Uri right)
        {
            if (left == null || right == null)
                return false;

            return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
                   left.Port == right.Port;
        }

        private static bool IsHttpScheme(Uri uri)
        {
            if (uri == null) return false;
            return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        private void ActivateWorker(string scope, ServiceWorkerRegistration reg, ServiceWorker sw, WorkerRuntime runtime)
        {
            FenBrowser.Core.FenLogger.Debug($"[ServiceWorkerManager] Activating {scope}", LogCategory.ServiceWorker);
            sw.UpdateState("activating");

            WorkerRuntime oldRuntime = null;
            if (_activeRuntimes.TryGetValue(scope, out var existing))
            {
                oldRuntime = existing;
            }
            _activeRuntimes[scope] = runtime;
             
            // Fire 'activate' event
            // ...

            sw.UpdateState("activated");
            reg.SetWaiting(null);
            reg.SetActive(sw);

            if (oldRuntime != null && !ReferenceEquals(oldRuntime, runtime))
            {
                try
                {
                    oldRuntime.Terminate();
                    oldRuntime.Dispose();
                }
                catch (Exception ex) { FenBrowser.Core.FenLogger.Warn($"[ServiceWorkerManager] Old runtime disposal failed: {ex.Message}", LogCategory.ServiceWorker); }
            }
        }
    }
}

