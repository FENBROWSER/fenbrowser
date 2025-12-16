using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace FenBrowser.FenEngine.Security
{
    public enum PermissionState
    {
        Prompt = 0,
        Granted = 1,
        Denied = 2
    }

    public class PermissionStore
    {
        private static PermissionStore _instance;
        public static PermissionStore Instance => _instance ?? (_instance = new PermissionStore());

        private Dictionary<string, Dictionary<string, PermissionState>> _store;
        private readonly string _filePath;
        private readonly object _lock = new object();

        public PermissionStore()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var fenDir = Path.Combine(appData, "FenBrowser");
            if (!Directory.Exists(fenDir)) Directory.CreateDirectory(fenDir);
            _filePath = Path.Combine(fenDir, "permissions.json");
            Load();
        }

        private void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_filePath))
                    {
                        var json = File.ReadAllText(_filePath);
                        _store = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, PermissionState>>>(json);
                    }
                }
                catch
                {
                    // Ignore load errors, start fresh
                }

                if (_store == null)
                    _store = new Dictionary<string, Dictionary<string, PermissionState>>();
            }
        }

        private void Save()
        {
            lock (_lock)
            {
                try
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var json = JsonSerializer.Serialize(_store, options);
                    File.WriteAllText(_filePath, json);
                }
                catch
                {
                    // Ignore save errors
                }
            }
        }

        public PermissionState GetState(string origin, JsPermissions permission)
        {
            if (string.IsNullOrEmpty(origin)) return PermissionState.Denied;

            lock (_lock)
            {
                if (_store.TryGetValue(origin, out var perms))
                {
                    var key = permission.ToString();
                    if (perms.TryGetValue(key, out var state))
                    {
                        return state;
                    }
                }
            }
            return PermissionState.Prompt;
        }

        public void SetState(string origin, JsPermissions permission, PermissionState state)
        {
            if (string.IsNullOrEmpty(origin)) return;

            lock (_lock)
            {
                if (!_store.ContainsKey(origin))
                    _store[origin] = new Dictionary<string, PermissionState>();

                _store[origin][permission.ToString()] = state;
                Save();
            }
        }
    }
}
