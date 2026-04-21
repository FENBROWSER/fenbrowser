using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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

        // Registration Scope -> Active Worker Runtime
        private readonly ConcurrentDictionary<string, WorkerRuntime> _activeRuntimes = new();

        // Tracked ServiceWorkerContainers (navigator.serviceWorker instances) for controllerchange notifications
        private readonly List<WeakReference<ServiceWorkerContainer>> _containers = new();
        private readonly object _containersLock = new();

        // Scopes where skipWaiting was called — worker activates immediately without waiting
        private readonly ConcurrentDictionary<string, bool> _skipWaitingScopes = new();

        // SW §8.4: Byte-for-byte script comparison — SHA-256 hash of last-installed script per scope
        private readonly ConcurrentDictionary<string, string> _scriptHashes = new();

        // Persistent registration storage key prefix
        private const string SW_REGISTRATION_STORE = "__sw_registrations";

        private FenBrowser.FenEngine.Storage.IStorageBackend _storageBackend;
        private Func<Uri, Task<string>> _scriptFetcher;
        private Func<Uri, bool> _scriptUriAllowed;

        private ServiceWorkerManager() { }

        /// <summary>
        /// Register a ServiceWorkerContainer to receive controllerchange notifications.
        /// Uses WeakReference to avoid preventing GC of containers.
        /// </summary>
        internal void RegisterContainer(ServiceWorkerContainer container)
        {
            if (container == null) return;
            lock (_containersLock)
            {
                // Clean up dead references while we're here
                _containers.RemoveAll(wr => !wr.TryGetTarget(out _));
                _containers.Add(new WeakReference<ServiceWorkerContainer>(container));
            }
        }
        
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
            FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] Registering {scriptUrl} for scope {scope}", LogCategory.ServiceWorker);

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

            // 2. SW §8.4: Byte-for-byte script comparison
            // Fetch the script and compute SHA-256 hash; skip install if identical to current version
            string scriptContent = null;
            if (_scriptFetcher != null)
            {
                try
                {
                    scriptContent = await _scriptFetcher(scriptUri).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FenBrowser.Core.EngineLogCompat.Warn($"[ServiceWorkerManager] Script fetch failed: {ex.Message}", LogCategory.ServiceWorker);
                }
            }

            if (scriptContent != null)
            {
                var newHash = ComputeScriptHash(scriptContent);
                if (_scriptHashes.TryGetValue(normalizedScope, out var existingHash) && existingHash == newHash)
                {
                    // Script unchanged — no update needed (SW §8.4 byte-for-byte check)
                    FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] Script unchanged for {normalizedScope}, skipping install", LogCategory.ServiceWorker);
                    return registration;
                }
                _scriptHashes[normalizedScope] = newHash;
            }

            // 3. Lifecycle: "Installing"
            var sw = new ServiceWorker(scriptUri.AbsoluteUri, normalizedScope, "installing");
            registration.SetInstalling(sw);

            // 4. Spin up runtime
            await RunBackground(() => StartWorkerRuntime(scriptUri.AbsoluteUri, normalizedScope, sw, registration)).ConfigureAwait(false);

            // 5. Persist registration to storage backend
            await PersistRegistrationAsync(normalizedScope, scriptUri.AbsoluteUri).ConfigureAwait(false);

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

        /// <summary>
        /// Returns all registrations whose scope matches the given origin.
        /// </summary>
        public List<ServiceWorkerRegistration> GetRegistrationsForOrigin(string origin)
        {
            var results = new List<ServiceWorkerRegistration>();
            var normalizedOrigin = NormalizeUrlForMatch(origin);
            if (normalizedOrigin == null) return results;

            foreach (var kvp in _registrations)
            {
                if (kvp.Key.StartsWith(normalizedOrigin, StringComparison.OrdinalIgnoreCase) ||
                    normalizedOrigin.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(kvp.Value);
                }
            }
            return results;
        }

        /// <summary>
        /// SW §4.5.2: Called by skipWaiting() inside a service worker.
        /// Causes the waiting worker to become the active worker immediately.
        /// </summary>
        internal void SkipWaiting(string scope)
        {
            var normalizedScope = NormalizeScope(scope);
            if (normalizedScope == null) return;
            _skipWaitingScopes[normalizedScope] = true;
            FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] skipWaiting for scope {normalizedScope}", LogCategory.ServiceWorker);
        }

        /// <summary>
        /// SW §4.5.3: Called by clients.claim() inside a service worker.
        /// Sets this worker as the controller for all in-scope clients and fires controllerchange.
        /// </summary>
        internal void ClaimClients(string scope)
        {
            var normalizedScope = NormalizeScope(scope);
            if (normalizedScope == null) return;

            if (!_registrations.TryGetValue(normalizedScope, out var reg) || reg.Active == null)
                return;

            NotifyControllersChanged(normalizedScope, reg.Active);
            FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] clients.claim for scope {normalizedScope}", LogCategory.ServiceWorker);
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
                catch (Exception ex) { FenBrowser.Core.EngineLogCompat.Warn($"[ServiceWorkerManager] Runtime disposal failed: {ex.Message}", LogCategory.ServiceWorker); }
            }

            removed.Installing?.UpdateState("redundant");
            removed.Waiting?.UpdateState("redundant");
            removed.Active?.UpdateState("redundant");
            removed.SetInstalling(null);
            removed.SetWaiting(null);
            removed.SetActive(null);

            // Remove persisted registration and script hash
            _scriptHashes.TryRemove(key, out _);
            if (_storageBackend != null)
            {
                try { _ = _storageBackend.Delete("__system", SW_REGISTRATION_STORE, "registrations", key); }
                catch { /* best effort */ }
            }

            FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] Unregistered scope {key}", LogCategory.ServiceWorker);
            return Task.FromResult(true);
        }

        /// <summary>
        /// SW §4.1: Deliver a message to the active service worker for the given scope.
        /// Uses StructuredClone via WorkerRuntime.PostMessage.
        /// </summary>
        public void PostMessageToWorker(string scope, object message)
        {
            if (_activeRuntimes.TryGetValue(scope, out var runtime))
            {
                runtime.PostMessage(message);
                FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] PostMessage to scope {scope}", LogCategory.ServiceWorker);
            }
            else
            {
               FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] No active runtime found for scope {scope}", LogCategory.ServiceWorker);
            }
        }

        private static Task RunBackground(Action operation)
        {
            return Task.Factory.StartNew(operation, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        /// <summary>
        /// SW §4.4: Service Worker lifecycle — install → wait → activate.
        /// Creates a WorkerRuntime and dispatches ExtendableEvents for install and activate.
        /// </summary>
        private void StartWorkerRuntime(string scriptUrl, string scope, ServiceWorker sw, ServiceWorkerRegistration reg)
        {
            try
            {
                var scriptUri = ResolveScriptUri(scriptUrl, scope);
                if (scriptUri == null)
                    throw new InvalidOperationException($"Invalid service worker script URL: {scriptUrl}");

                if (_scriptUriAllowed != null && !_scriptUriAllowed(scriptUri))
                    throw new UnauthorizedAccessException($"Service worker script blocked by policy: {scriptUri}");

                // Create runtime
                var runtime = new WorkerRuntime(
                    scriptUri.AbsoluteUri,
                    scope,
                    _storageBackend,
                    _scriptFetcher,
                    _scriptUriAllowed,
                    isServiceWorker: true);

                // SW §4.4.2: Installing — fire 'install' ExtendableEvent
                FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] Installing {scope}", LogCategory.ServiceWorker);
                DispatchLifecycleEvent(runtime, "install");

                sw.UpdateState("installed");
                reg.SetInstalling(null);
                reg.SetWaiting(sw);

                // SW §4.4.4: Auto-activate (skipWaiting semantics or no existing active worker)
                ActivateWorker(scope, reg, sw, runtime);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Error($"[ServiceWorkerManager] Runtime start failed: {ex.Message}", LogCategory.ServiceWorker);
                sw.UpdateState("redundant");
            }
        }

        /// <summary>
        /// Dispatches a lifecycle ExtendableEvent (install/activate) to the service worker runtime.
        /// SW §4.4: The event is dispatched to the ServiceWorkerGlobalScope.
        /// </summary>
        private static void DispatchLifecycleEvent(WorkerRuntime runtime, string eventType)
        {
            try
            {
                var evt = new FenBrowser.FenEngine.Core.FenObject();
                evt.Set("type", FenBrowser.FenEngine.Core.FenValue.FromString(eventType));

                // ExtendableEvent.waitUntil() — tracks promises that must settle before the event completes
                var waitUntilPromises = new System.Collections.Generic.List<FenBrowser.FenEngine.Core.FenValue>();
                evt.Set("waitUntil", FenBrowser.FenEngine.Core.FenValue.FromFunction(
                    new FenBrowser.FenEngine.Core.FenFunction("waitUntil", (args, thisVal) =>
                    {
                        if (args.Length > 0)
                            waitUntilPromises.Add(args[0]);
                        return FenBrowser.FenEngine.Core.FenValue.Undefined;
                    })));

                // Dispatch to the globalScope via the task queue so the worker thread processes it
                runtime.QueueTask(() =>
                {
                    runtime.DispatchGlobalEvent(eventType, FenBrowser.FenEngine.Core.FenValue.FromObject(evt));
                }, $"ServiceWorker.{eventType}");

                FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] Dispatched '{eventType}' event", LogCategory.ServiceWorker);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn($"[ServiceWorkerManager] Lifecycle event '{eventType}' dispatch failed: {ex.Message}", LogCategory.ServiceWorker);
            }
        }

        public async Task<bool> DispatchFetchEvent(ServiceWorker sw, FenBrowser.FenEngine.WebAPIs.FetchEvent fetchEvt)
        {
            if (sw  == null) return false;
            
            // Lookup runtime by scope directly
            if (_activeRuntimes.TryGetValue(sw.Scope, out var runtime))
            {
                FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] Dispatch FetchEvent to {sw.Scope}", LogCategory.ServiceWorker);
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
            FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] Activating {scope}", LogCategory.ServiceWorker);
            sw.UpdateState("activating");

            WorkerRuntime oldRuntime = null;
            if (_activeRuntimes.TryGetValue(scope, out var existing))
            {
                oldRuntime = existing;
            }
            _activeRuntimes[scope] = runtime;

            // SW §4.4.5: Fire 'activate' ExtendableEvent
            DispatchLifecycleEvent(runtime, "activate");

            sw.UpdateState("activated");
            reg.SetWaiting(null);
            reg.SetActive(sw);

            // SW §4.3: Notify all in-scope containers of the controller change
            NotifyControllersChanged(scope, sw);

            if (oldRuntime != null && !ReferenceEquals(oldRuntime, runtime))
            {
                try
                {
                    oldRuntime.Terminate();
                    oldRuntime.Dispose();
                }
                catch (Exception ex) { FenBrowser.Core.EngineLogCompat.Warn($"[ServiceWorkerManager] Old runtime disposal failed: {ex.Message}", LogCategory.ServiceWorker); }
            }
        }

        /// <summary>
        /// Notify all tracked ServiceWorkerContainers whose origin falls within the given scope
        /// that the controller has changed. Fires controllerchange event and resolves ready promise.
        /// </summary>
        private void NotifyControllersChanged(string scope, ServiceWorker activeWorker)
        {
            lock (_containersLock)
            {
                _containers.RemoveAll(wr => !wr.TryGetTarget(out _));
                foreach (var wr in _containers)
                {
                    if (!wr.TryGetTarget(out var container)) continue;
                    var normalizedOrigin = NormalizeUrlForMatch(container.Origin);
                    if (normalizedOrigin != null && normalizedOrigin.StartsWith(scope, StringComparison.OrdinalIgnoreCase))
                    {
                        container.UpdateController(activeWorker);
                        var reg = GetRegistration(scope);
                        if (reg != null) container.ResolveReady(reg);
                    }
                }
            }
        }

        /// <summary>
        /// SW §8.4: Compute SHA-256 hash of script content for byte-for-byte comparison.
        /// </summary>
        private static string ComputeScriptHash(string scriptContent)
        {
            var bytes = Encoding.UTF8.GetBytes(scriptContent);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// Persist a service worker registration to the storage backend so it survives
        /// browser restarts (SW §6.3 persistent registration).
        /// </summary>
        private async Task PersistRegistrationAsync(string scope, string scriptUrl)
        {
            if (_storageBackend == null) return;
            try
            {
                var data = new Dictionary<string, object>
                {
                    ["scope"] = scope,
                    ["scriptUrl"] = scriptUrl,
                    ["registeredAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                await _storageBackend.Put("__system", SW_REGISTRATION_STORE, "registrations", scope, data).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] Failed to persist registration: {ex.Message}", LogCategory.ServiceWorker);
            }
        }

        /// <summary>
        /// Load persisted registrations from the storage backend on startup.
        /// Call after Initialize() to restore registrations from a previous session.
        /// </summary>
        public async Task LoadPersistedRegistrationsAsync()
        {
            if (_storageBackend == null) return;
            try
            {
                var allKeys = await _storageBackend.GetAllKeys("__system", SW_REGISTRATION_STORE, "registrations").ConfigureAwait(false);
                if (allKeys == null) return;

                foreach (var key in allKeys)
                {
                    var data = await _storageBackend.Get("__system", SW_REGISTRATION_STORE, "registrations", key?.ToString()).ConfigureAwait(false);
                    if (data is Dictionary<string, object> dict &&
                        dict.TryGetValue("scope", out var scopeObj) &&
                        dict.TryGetValue("scriptUrl", out var urlObj))
                    {
                        var scope = scopeObj?.ToString();
                        var scriptUrl = urlObj?.ToString();
                        if (!string.IsNullOrEmpty(scope) && !string.IsNullOrEmpty(scriptUrl))
                        {
                            try
                            {
                                await Register(scriptUrl, scope).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] Failed to restore registration {scope}: {ex.Message}", LogCategory.ServiceWorker);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Debug($"[ServiceWorkerManager] Failed to load persisted registrations: {ex.Message}", LogCategory.ServiceWorker);
            }
        }
    }
}


