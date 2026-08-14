using System;
using System.Collections.Generic;
using System.Linq;

namespace FenBrowser.FenEngine.Security
{
    /// <summary>
    /// Permission manager implementation with audit logging.
    /// Enforces deny-by-default security model.
    /// </summary>
    public class PermissionManager : IPermissionManager
    {
        // This mask represents embedder/engine capabilities granted explicitly through
        // the constructor or Grant(). It must never be populated from a website's
        // persistent/user permission decision because those decisions are origin scoped.
        private JsPermissions _grantedPermissions;
        private readonly List<SecurityViolation> _violations = new List<SecurityViolation>();
        private readonly object _lock = new object();

        public Func<string, JsPermissions, System.Threading.Tasks.Task<bool>> PermissionRequestedHandler { get; set; }

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

        public async System.Threading.Tasks.Task<bool> RequestPermissionAsync(JsPermissions permission, string origin)
        {
            // Explicit embedder capabilities remain global by design. Website grants,
            // however, are always evaluated below against the requesting origin.
            if (Check(permission))
                return true;

            var normalizedOrigin = PermissionStore.NormalizeOrigin(origin);
            if (normalizedOrigin == null)
                return false;

            var storeState = PermissionStore.Instance.GetState(normalizedOrigin, permission);
            if (storeState == PermissionState.Granted)
                return true;

            if (storeState == PermissionState.Denied)
                return false;

            var handler = PermissionRequestedHandler;
            if (handler == null)
                return false;

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
            lock (_lock)
            {
                _violations.Add(new SecurityViolation
                {
                    Timestamp = DateTime.UtcNow,
                    Permission = permission,
                    Operation = operation,
                    Details = details
                });

                // Limit violation log size
                if (_violations.Count > 1000)
                {
                    _violations.RemoveRange(0, 100); // Remove oldest 100
                }
            }

            // Log to console for debugging
            Console.WriteLine($"[Security] Permission denied: {permission} for operation: {operation}");
        }

        public IReadOnlyList<SecurityViolation> GetViolations()
        {
            lock (_lock)
            {
                return _violations.ToList();
            }
        }
    }
}
