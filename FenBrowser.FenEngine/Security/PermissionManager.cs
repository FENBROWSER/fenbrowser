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
