using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FenBrowser.FenEngine.Security
{
    public enum PermissionState
    {
        Prompt = 0,
        Granted = 1,
        Denied = 2
    }

    public sealed class PermissionStore
    {
        private static readonly Lazy<PermissionStore> LazyInstance =
            new(() => new PermissionStore(), isThreadSafe: true);

        public static PermissionStore Instance => LazyInstance.Value;

        private Dictionary<string, Dictionary<string, PermissionState>> _store;
        private readonly string _filePath;
        private readonly object _lock = new();

        public PermissionStore()
        {
            _store = new Dictionary<string, Dictionary<string, PermissionState>>(StringComparer.Ordinal);

            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrWhiteSpace(appData))
                {
                    var fenDir = Path.Combine(appData, "FenBrowser");
                    Directory.CreateDirectory(fenDir);
                    _filePath = Path.Combine(fenDir, "permissions.json");
                }
            }
            catch
            {
                // Keep an in-memory deny-by-default permission store if persistent
                // application data is unavailable. Permission decisions still work
                // for the process lifetime but are not persisted.
                _filePath = null;
            }

            Load();
        }

        private void Load()
        {
            if (string.IsNullOrEmpty(_filePath))
                return;

            lock (_lock)
            {
                try
                {
                    if (!File.Exists(_filePath))
                        return;

                    var json = File.ReadAllText(_filePath);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, PermissionState>>>(json);
                    if (loaded == null)
                        return;

                    // Rebuild through canonical origin keys rather than trusting raw
                    // persisted dictionary keys from older versions. Non-origin keys
                    // (paths, credentials, malformed values) are ignored and will prompt
                    // again instead of accidentally broadening a permission grant.
                    var normalizedStore = new Dictionary<string, Dictionary<string, PermissionState>>(StringComparer.Ordinal);
                    foreach (var originEntry in loaded)
                    {
                        var normalizedOrigin = NormalizeOrigin(originEntry.Key);
                        if (normalizedOrigin == null || originEntry.Value == null)
                            continue;

                        if (!normalizedStore.TryGetValue(normalizedOrigin, out var permissions))
                        {
                            permissions = new Dictionary<string, PermissionState>(StringComparer.Ordinal);
                            normalizedStore[normalizedOrigin] = permissions;
                        }

                        foreach (var permissionEntry in originEntry.Value)
                        {
                            permissions[permissionEntry.Key] = permissionEntry.Value;
                        }
                    }

                    _store = normalizedStore;
                }
                catch
                {
                    // Corrupt/unreadable state fails closed to the existing empty store.
                    _store = new Dictionary<string, Dictionary<string, PermissionState>>(StringComparer.Ordinal);
                }
            }
        }

        private void SaveLocked()
        {
            if (string.IsNullOrEmpty(_filePath))
                return;

            var tempPath = _filePath + ".tmp";
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_store, options);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch
            {
                // Persistence failure must not change the in-memory permission decision.
                // Never fall back to a more permissive state.
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }

        public PermissionState GetState(string origin, JsPermissions permission)
        {
            var normalizedOrigin = NormalizeOrigin(origin);
            if (normalizedOrigin == null)
                return PermissionState.Denied;

            lock (_lock)
            {
                if (_store.TryGetValue(normalizedOrigin, out var permissions) &&
                    permissions.TryGetValue(permission.ToString(), out var state))
                {
                    return state;
                }
            }

            return PermissionState.Prompt;
        }

        public void SetState(string origin, JsPermissions permission, PermissionState state)
        {
            var normalizedOrigin = NormalizeOrigin(origin);
            if (normalizedOrigin == null)
                return;

            lock (_lock)
            {
                if (!_store.TryGetValue(normalizedOrigin, out var permissions))
                {
                    permissions = new Dictionary<string, PermissionState>(StringComparer.Ordinal);
                    _store[normalizedOrigin] = permissions;
                }

                permissions[permission.ToString()] = state;
                SaveLocked();
            }
        }

        /// <summary>
        /// Canonical serialized origin used as the permission identity. Credentials,
        /// path, query and fragment are never part of a permission key.
        /// Returns null for opaque/relative/malformed origins.
        /// </summary>
        internal static string NormalizeOrigin(string origin)
        {
            if (string.IsNullOrWhiteSpace(origin) ||
                string.Equals(origin.Trim(), "null", StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri) ||
                string.IsNullOrWhiteSpace(uri.Scheme) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                return null;
            }

            var scheme = uri.Scheme.ToLowerInvariant();
            var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            if (host.Length == 0)
                return null;

            if (host.IndexOf(':') >= 0 &&
                !host.StartsWith("[", StringComparison.Ordinal) &&
                !host.EndsWith("]", StringComparison.Ordinal))
            {
                host = "[" + host + "]";
            }

            return uri.IsDefaultPort || uri.Port <= 0
                ? $"{scheme}://{host}"
                : $"{scheme}://{host}:{uri.Port}";
        }
    }
}
