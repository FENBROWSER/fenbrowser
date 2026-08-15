using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Security
{
    /// <summary>
    /// Permission manager implementation with audit logging.
    /// Enforces deny-by-default security model.
    /// </summary>
    public class PermissionManager : IPermissionManager
    {
        private const int MaxViolationEntries = 1000;
        private const int ViolationTrimBatch = 100;
        private const int MaxOperationChars = 512;
        private const int MaxDetailChars = 2048;

        private readonly record struct PermissionRequestKey(string Origin, JsPermissions Permission);

        // This mask represents embedder/engine capabilities granted explicitly through
        // the constructor or Grant(). It must never be populated from a website's
        // persistent/user permission decision because those decisions are origin scoped.
        private JsPermissions _grantedPermissions;
        private readonly List<SecurityViolation> _violations = new();
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<PermissionRequestKey, Lazy<Task<bool>>> _inflightPermissionRequests = new();

        public Func<string, JsPermissions, Task<bool>> PermissionRequestedHandler { get; set; }

        public PermissionManager(JsPermissions initialPermissions = JsPermissions.None)
        {
            _grantedPermissions = initialPermissions;
        }

        public bool Check(JsPermissions permission)
        {
            lock (_lock)
            {
                return (_grantedPermissions & permission) == permission;
            }
        }

        public bool CheckAndLog(JsPermissions permission, string operation)
        {
            if (Check(permission))
                return true;

            LogViolation(permission, operation);
            return false;
        }

        public void Grant(JsPermissions permission)
        {
            lock (_lock)
            {
                _grantedPermissions |= permission;
            }
        }

        public void Revoke(JsPermissions permission)
        {
            lock (_lock)
            {
                _grantedPermissions &= ~permission;
            }
        }

        public Task<bool> RequestPermissionAsync(JsPermissions permission, string origin)
        {
            // Explicit embedder capabilities remain global by design. Website grants,
            // however, are always evaluated below against the requesting origin.
            if (Check(permission))
                return Task.FromResult(true);

            var normalizedOrigin = PermissionStore.NormalizeOrigin(origin);
            if (normalizedOrigin == null)
                return Task.FromResult(false);

            var storeState = PermissionStore.Instance.GetState(normalizedOrigin, permission);
            if (storeState == PermissionState.Granted)
                return Task.FromResult(true);

            if (storeState == PermissionState.Denied)
                return Task.FromResult(false);

            var handler = PermissionRequestedHandler;
            if (handler == null)
                return Task.FromResult(false);

            // Multiple JS callers can request the same capability concurrently. A
            // browser must not open duplicate prompts and let whichever callback
            // finishes last overwrite the user's decision. Share one in-flight prompt
            // per canonical origin/permission pair, then make later calls re-check the
            // persisted state after that request completes.
            var key = new PermissionRequestKey(normalizedOrigin, permission);
            var lazy = _inflightPermissionRequests.GetOrAdd(
                key,
                _ => new Lazy<Task<bool>>(
                    () => PromptAndPersistAsync(normalizedOrigin, permission, handler),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            return AwaitPermissionRequestAsync(key, lazy);
        }

        private async Task<bool> AwaitPermissionRequestAsync(
            PermissionRequestKey key,
            Lazy<Task<bool>> request)
        {
            try
            {
                return await request.Value.ConfigureAwait(false);
            }
            finally
            {
                // Removal is deliberately conditional. A completed request may be
                // replaced by a new request after state is externally reset; an older
                // waiter must not remove that newer entry.
                _inflightPermissionRequests.TryRemove(
                    new KeyValuePair<PermissionRequestKey, Lazy<Task<bool>>>(key, request));
            }
        }

        private static async Task<bool> PromptAndPersistAsync(
            string normalizedOrigin,
            JsPermissions permission,
            Func<string, JsPermissions, Task<bool>> handler)
        {
            bool granted;
            try
            {
                // Pass only a canonical serialized origin to UI. Paths, query strings,
                // fragments, and credentials must never become part of the permission
                // identity or prompt text.
                granted = await handler(normalizedOrigin, permission).ConfigureAwait(false);
            }
            catch
            {
                // Permission prompts fail closed. UI/host failures must not become grants.
                return false;
            }

            PermissionStore.Instance.SetState(
                normalizedOrigin,
                permission,
                granted ? PermissionState.Granted : PermissionState.Denied);

            // Do not call Grant(permission) here. Doing so would turn one origin's
            // decision into a process-wide permission for every subsequently loaded site.
            return granted;
        }

        public void LogViolation(JsPermissions permission, string operation, string details = null)
        {
            var boundedOperation = BoundDiagnostic(operation, MaxOperationChars);
            var boundedDetails = BoundDiagnostic(details, MaxDetailChars);

            lock (_lock)
            {
                _violations.Add(new SecurityViolation
                {
                    Timestamp = DateTime.UtcNow,
                    Permission = permission,
                    Operation = boundedOperation,
                    Details = boundedDetails
                });

                if (_violations.Count > MaxViolationEntries)
                {
                    _violations.RemoveRange(
                        0,
                        Math.Min(ViolationTrimBatch, _violations.Count));
                }
            }

            // Security diagnostics can contain page/extension supplied operation text.
            // Route through the engine logger rather than raw Console.WriteLine so
            // control-character handling and headless-host behavior are centralized.
            EngineLogCompat.Warn(
                $"[Security] Permission denied: {permission} for operation: {NormalizeLogField(boundedOperation)}",
                LogCategory.Security);
        }

        public IReadOnlyList<SecurityViolation> GetViolations()
        {
            lock (_lock)
            {
                // Return detached entries. A List copy alone still exposed the same
                // mutable SecurityViolation objects to callers.
                return _violations.Select(static violation => new SecurityViolation
                {
                    Timestamp = violation.Timestamp,
                    Permission = violation.Permission,
                    Operation = violation.Operation,
                    Details = violation.Details
                }).ToArray();
            }
        }

        private static string BoundDiagnostic(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            return value.Length <= maxChars
                ? value
                : value[..maxChars] + "…";
        }

        private static string NormalizeLogField(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\0", "\\0", StringComparison.Ordinal);
        }
    }
}
