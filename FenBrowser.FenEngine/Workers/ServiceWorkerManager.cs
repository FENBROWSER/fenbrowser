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

        private ServiceWorkerManager() { }

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
            var sw = new ServiceWorker(scriptUrl, "installing");
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

        public void PostMessageToWorker(string scriptUrl, object message)
        {
            // Find runtime 
            // HACK: Need better mapping. iterating runtimes.
            // In production map by Registration ID or Version ID.
            
            // For now, logging usage
            FenBrowser.Core.FenLogger.Debug($"[ServiceWorkerManager] PostMessage to {scriptUrl}: {message}", LogCategory.ServiceWorker);
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
                if (reg.Active == null)
                {
                    ActivateWorker(scope, reg, sw);
                }
            }
            catch (Exception ex)
            {
                sw.UpdateState("redundant");
            }
        }

        public async Task<bool> DispatchFetchEvent(ServiceWorker sw, FenBrowser.FenEngine.WebAPIs.FetchEvent fetchEvt)
        {
            // Find runtime for the SW
            // Simplification: We iterate _registrations to find which scope owns this SW, then find runtime?
            // Better: Store runtime reference in SW object or map SW -> Runtime
            
            // For Phase G, we'll iterate active runtimes and match script URL
            foreach(var kvp in _registrations)
            {
                if (kvp.Value.Active == sw)
                {
                    // Found registration. 
                    // Now find runtime. We didn't store runtime in Registration...
                    // HACK: Just look for a runtime with matching script URL in _activeRuntimes?
                    // We don't have _activeRuntimes populated yet.
                    
                    // Let's assume we can get it or we store it.
                    // Implementation: Since we don't have full runtime tracking yet, we'll just log and return false
                    // UNLESS we are in a test environment where we can inject logic.
                    
                    // TODO: Implement actual runtime dispatch
                    // For now, return false to fallback to network
                    return false;
                }
            }
            return false;
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
