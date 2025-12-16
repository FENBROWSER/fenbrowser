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
            // 1. Check in-memory grant first
            if (Check(permission)) return true;

            // 2. Check persistent store
            var storeState = PermissionStore.Instance.GetState(origin, permission);
            if (storeState == PermissionState.Granted)
            {
                Grant(permission); // Sync memory
                return true;
            }
            if (storeState == PermissionState.Denied)
            {
                return false;
            }

            // 3. Prompt user if Prompt
            if (PermissionRequestedHandler != null)
            {
                bool granted = await PermissionRequestedHandler(origin, permission);
                
                // Update Store
                PermissionStore.Instance.SetState(origin, permission, granted ? PermissionState.Granted : PermissionState.Denied);

                if (granted)
                {
                    Grant(permission);
                    return true;
                }
            }

            return false;
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
