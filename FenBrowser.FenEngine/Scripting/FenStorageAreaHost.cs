using System;
using System.Collections.Generic;
using System.Text;
using FenBrowser.Core.Storage;

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// Bridges a PartitionedKeyValueStorage to the JS Storage interface
    /// (localStorage / sessionStorage).
    /// https://html.spec.whatwg.org/multipage/webstorage.html#the-storage-interface
    /// </summary>
    public sealed class FenStorageAreaHost
    {
        private readonly PartitionedKeyValueStorage _storage;
        private readonly StoragePartitionKey _partitionKey;
        private string _origin;

        public FenStorageAreaHost(
            PartitionedKeyValueStorage storage,
            StoragePartitionKey partitionKey,
            string origin)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _partitionKey = partitionKey;
            _origin = origin ?? string.Empty;
        }

        public PartitionedKeyValueStorage Storage => _storage;
        public StoragePartitionKey PartitionKey => _partitionKey;
        public string Origin => _origin;

        public void UpdateOrigin(string origin)
        {
            _origin = origin ?? string.Empty;
        }

        public int Length => _storage.Length(_origin, _partitionKey);

        public string Key(int index)
        {
            var keys = _storage.GetKeys(_origin, _partitionKey);
            if (index < 0 || index >= keys.Count)
                return null;
            return keys[index];
        }

        public string GetItem(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            return _storage.GetItem(_origin, _partitionKey, key);
        }

        /// <summary>Returns null on success, or "QuotaExceededError" on failure.</summary>
        public string SetItem(string key, string value)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            if (!_storage.SetItem(_origin, _partitionKey, key, value ?? string.Empty))
                return "QuotaExceededError";
            return null;
        }

        public void RemoveItem(string key)
        {
            if (string.IsNullOrEmpty(key))
                return;
            _storage.RemoveItem(_origin, _partitionKey, key);
        }

        public void Clear()
        {
            _storage.Clear(_origin, _partitionKey);
        }

        /// <summary>Enumerate all keys (used by Object.keys() etc. if needed).</summary>
        public IReadOnlyList<string> GetKeys() => _storage.GetKeys(_origin, _partitionKey);
    }
}
