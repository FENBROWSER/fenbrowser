using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        private const int MaxPermissionStoreBytes = 4 * 1024 * 1024;
        private const int MaxJsonDepth = 32;

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

                    var json = ReadUtf8FileBounded(_filePath, MaxPermissionStoreBytes);
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, PermissionState>>>(
                        json,
                        new JsonSerializerOptions { MaxDepth = MaxJsonDepth });
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
                            // Persistent state is a security boundary. Unknown enum
                            // values or invented permission names from a corrupt/older
                            // file must never become an authorization decision.
                            if (!Enum.IsDefined(permissionEntry.Value) ||
                                !Enum.TryParse<JsPermissions>(permissionEntry.Key, out var parsedPermission) ||
                                parsedPermission == JsPermissions.None)
                            {
                                continue;
                            }

                            permissions[parsedPermission.ToString()] = permissionEntry.Value;
                        }

                        if (permissions.Count == 0)
                            normalizedStore.Remove(normalizedOrigin);
                    }

                    _store = normalizedStore;
                }
                catch
                {
                    // Corrupt, oversized, or unreadable state fails closed to an empty
                    // store. Never keep partially parsed authorization decisions.
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
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    MaxDepth = MaxJsonDepth
                };
                var json = JsonSerializer.Serialize(_store, options);
                if (Encoding.UTF8.GetByteCount(json) > MaxPermissionStoreBytes)
                {
                    // Do not leave an older grant-bearing file on disk when the new
                    // authoritative state cannot be persisted. Removing it fails closed
                    // on restart (Prompt) instead of resurrecting stale permissions.
                    if (File.Exists(_filePath))
                        File.Delete(_filePath);
                    return;
                }

                File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
            if (normalizedOrigin == null || permission == JsPermissions.None || !Enum.IsDefined(state))
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

        private static string ReadUtf8FileBounded(string path, int maxBytes)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                options: FileOptions.SequentialScan);

            if (stream.Length > maxBytes)
                throw new InvalidDataException("Persisted permission store exceeds its admission limit.");

            using var buffer = new MemoryStream(capacity: (int)Math.Min(stream.Length, maxBytes));
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = stream.Read(chunk, 0, chunk.Length);
                if (read == 0)
                    break;

                if (buffer.Length + read > maxBytes)
                    throw new InvalidDataException("Persisted permission store exceeds its admission limit.");

                buffer.Write(chunk, 0, read);
            }

            return new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        }
    }
}
