using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core.Network.Handlers
{
    public class HstsHandler : INetworkHandler
    {
        private sealed class HstsEntry { public DateTimeOffset Expiry; public bool IncludeSub; }
        private readonly Dictionary<string, HstsEntry> _hsts = new Dictionary<string, HstsEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly string _storePath;

        public HstsHandler(string cacheRoot)
        {
            if (!string.IsNullOrEmpty(cacheRoot))
            {
                _storePath = Path.Combine(cacheRoot, "hsts_store_v1.txt");
                LoadHsts();
            }
        }

        private void LoadHsts()
        {
            try
            {
                if (File.Exists(_storePath))
                {
                    var lines = File.ReadAllLines(_storePath);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length >= 3)
                        {
                            if (DateTimeOffset.TryParse(parts[1], out var exp) && bool.TryParse(parts[2], out var inc))
                                _hsts[parts[0]] = new HstsEntry { Expiry = exp, IncludeSub = inc };
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveHsts()
        {
            try
            {
                if (_storePath == null) return;
                var sb = new StringBuilder();
                foreach (var kv in _hsts)
                {
                    if (kv.Value.Expiry > DateTimeOffset.UtcNow)
                        sb.Append(kv.Key).Append('|').Append(kv.Value.Expiry.ToString("o")).Append('|').Append(kv.Value.IncludeSub ? "true" : "false").Append('\n');
                }
                File.WriteAllText(_storePath, sb.ToString());
            }
            catch { }
        }

        private Uri UpgradeIfHsts(Uri u)
        {
            if (u == null || u.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return u;
            var host = u.Host;
            
            // Check exact match
            if (_hsts.TryGetValue(host, out var entry) && entry.Expiry > DateTimeOffset.UtcNow)
            {
                return new UriBuilder(u) { Scheme = "https", Port = -1 }.Uri;
            }

            // Check subdomains (simplified)
            foreach (var kv in _hsts)
            {
                if (kv.Value.IncludeSub && kv.Value.Expiry > DateTimeOffset.UtcNow && host.EndsWith("." + kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return new UriBuilder(u) { Scheme = "https", Port = -1 }.Uri;
                }
            }
            return u;
        }

        public async Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            // 1. Upgrade Request
            context.Request.RequestUri = UpgradeIfHsts(context.Request.RequestUri);

            await next();

            // 2. Process Response Headers
            var resp = context.Response;
            if (resp != null && resp.RequestMessage?.RequestUri?.Scheme == "https")
            {
                if (resp.Headers.TryGetValues("Strict-Transport-Security", out var values))
                {
                    var v = string.Join(",", values);
                    var max = Regex.Match(v, @"max-age\s*=\s*(?<s>\d+)", RegexOptions.IgnoreCase);
                    if (max.Success && long.TryParse(max.Groups["s"].Value, out var sec) && sec > 0)
                    {
                        bool include = v.IndexOf("includesubdomains", StringComparison.OrdinalIgnoreCase) >= 0;
                        var host = resp.RequestMessage.RequestUri.Host;
                        _hsts[host] = new HstsEntry { Expiry = DateTimeOffset.UtcNow.AddSeconds(sec), IncludeSub = include };
                        SaveHsts(); // Save immediately for simplicity, or debounce in real app
                    }
                }
            }
        }
    }
}
