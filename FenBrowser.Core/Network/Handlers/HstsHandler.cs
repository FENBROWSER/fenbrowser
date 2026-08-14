using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;

namespace FenBrowser.Core.Network.Handlers
{
    public class HstsHandler : INetworkHandler
    {
        private sealed class HstsEntry
        {
            public DateTimeOffset Expiry;
            public bool IncludeSub;
        }

        private readonly Dictionary<string, HstsEntry> _hsts =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly string _storePath;
        private readonly object _hstsLock = new();
        private readonly SemaphoreSlim _persistGate = new(1, 1);

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
                if (!File.Exists(_storePath))
                    return;

                var now = DateTimeOffset.UtcNow;
                foreach (var line in File.ReadLines(_storePath))
                {
                    var parts = line.Split('|');
                    if (parts.Length < 3)
                        continue;

                    var host = NormalizeHost(parts[0]);
                    if (host == null || IsIpLiteral(host))
                        continue;

                    if (DateTimeOffset.TryParse(
                            parts[1],
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var expiry) &&
                        expiry > now &&
                        bool.TryParse(parts[2], out var includeSub))
                    {
                        lock (_hstsLock)
                        {
                            _hsts[host] = new HstsEntry
                            {
                                Expiry = expiry,
                                IncludeSub = includeSub
                            };
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[HSTS] Load failed: {ex.Message}", LogCategory.Security);
            }
        }

        private async Task SaveHstsAsync()
        {
            if (_storePath == null)
                return;

            await _persistGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var sb = new StringBuilder();
                lock (_hstsLock)
                {
                    RemoveExpiredEntriesLocked(now);
                    foreach (var kv in _hsts)
                    {
                        sb.Append(kv.Key)
                          .Append('|')
                          .Append(kv.Value.Expiry.ToString("o", CultureInfo.InvariantCulture))
                          .Append('|')
                          .Append(kv.Value.IncludeSub ? "true" : "false")
                          .Append('\n');
                    }
                }

                var directory = Path.GetDirectoryName(_storePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var tempPath = _storePath + ".tmp";
                await File.WriteAllTextAsync(tempPath, sb.ToString(), Encoding.UTF8).ConfigureAwait(false);
                File.Move(tempPath, _storePath, overwrite: true);
            }
            catch (Exception ex)
            {
                EngineLogCompat.Debug($"[HSTS] Save failed: {ex.Message}", LogCategory.Security);
                try
                {
                    var tempPath = _storePath + ".tmp";
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Cleanup failure must not affect network processing.
                }
            }
            finally
            {
                _persistGate.Release();
            }
        }

        private Uri UpgradeIfHsts(Uri uri)
        {
            if (uri == null || !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                return uri;

            var host = NormalizeHost(uri.Host);
            if (host == null || IsIpLiteral(host))
                return uri;

            var now = DateTimeOffset.UtcNow;
            lock (_hstsLock)
            {
                if (TryGetLiveEntryLocked(host, now, out _))
                    return UpgradeToHttps(uri);

                var dot = host.IndexOf('.');
                while (dot >= 0 && dot + 1 < host.Length)
                {
                    var parent = host[(dot + 1)..];
                    if (TryGetLiveEntryLocked(parent, now, out var parentEntry) && parentEntry.IncludeSub)
                        return UpgradeToHttps(uri);

                    dot = host.IndexOf('.', dot + 1);
                }
            }

            return uri;
        }

        public async Task HandleAsync(NetworkContext context, Func<Task> next, CancellationToken ct)
        {
            if (context?.Request?.RequestUri != null)
                context.Request.RequestUri = UpgradeIfHsts(context.Request.RequestUri);

            await next().ConfigureAwait(false);

            var response = context?.Response;
            var responseUri = response?.RequestMessage?.RequestUri;
            if (response == null ||
                responseUri == null ||
                !responseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!response.Headers.TryGetValues("Strict-Transport-Security", out var values))
                return;

            string firstValue = null;
            foreach (var value in values)
            {
                firstValue = value;
                break;
            }

            if (!TryParsePolicy(firstValue, out var maxAgeSeconds, out var includeSubDomains))
                return;

            var host = NormalizeHost(responseUri.Host);
            if (host == null || IsIpLiteral(host))
                return;

            lock (_hstsLock)
            {
                if (maxAgeSeconds == 0)
                {
                    _hsts.Remove(host);
                }
                else
                {
                    _hsts[host] = new HstsEntry
                    {
                        Expiry = ComputeExpiry(maxAgeSeconds),
                        IncludeSub = includeSubDomains
                    };
                }
            }

            await SaveHstsAsync().ConfigureAwait(false);
        }

        private bool TryGetLiveEntryLocked(string host, DateTimeOffset now, out HstsEntry entry)
        {
            if (_hsts.TryGetValue(host, out entry))
            {
                if (entry.Expiry > now)
                    return true;

                _hsts.Remove(host);
            }

            entry = null;
            return false;
        }

        private void RemoveExpiredEntriesLocked(DateTimeOffset now)
        {
            List<string> expired = null;
            foreach (var kv in _hsts)
            {
                if (kv.Value.Expiry <= now)
                {
                    expired ??= new List<string>();
                    expired.Add(kv.Key);
                }
            }

            if (expired == null)
                return;

            foreach (var host in expired)
                _hsts.Remove(host);
        }

        private static bool TryParsePolicy(
            string headerValue,
            out ulong maxAgeSeconds,
            out bool includeSubDomains)
        {
            maxAgeSeconds = 0;
            includeSubDomains = false;
            if (string.IsNullOrWhiteSpace(headerValue))
                return false;

            var sawMaxAge = false;
            var sawIncludeSubDomains = false;
            var directives = headerValue.Split(';');

            foreach (var rawDirective in directives)
            {
                var directive = rawDirective.Trim();
                if (directive.Length == 0)
                    continue;

                var equals = directive.IndexOf('=');
                var name = (equals >= 0 ? directive[..equals] : directive).Trim();
                var value = equals >= 0 ? directive[(equals + 1)..].Trim() : null;
                if (!IsToken(name))
                    return false;

                if (name.Equals("max-age", StringComparison.OrdinalIgnoreCase))
                {
                    if (sawMaxAge || value == null)
                        return false;

                    sawMaxAge = true;
                    value = Unquote(value);
                    if (!TryParseDeltaSeconds(value, out maxAgeSeconds))
                        return false;
                }
                else if (name.Equals("includesubdomains", StringComparison.OrdinalIgnoreCase))
                {
                    if (sawIncludeSubDomains || value != null)
                        return false;

                    sawIncludeSubDomains = true;
                    includeSubDomains = true;
                }
                else if (value != null && !IsToken(value) && !IsQuotedString(value))
                {
                    return false;
                }
            }

            return sawMaxAge;
        }

        private static bool TryParseDeltaSeconds(string value, out ulong seconds)
        {
            seconds = 0;
            if (string.IsNullOrEmpty(value))
                return false;

            foreach (var c in value)
            {
                if (c < '0' || c > '9')
                    return false;

                var digit = (uint)(c - '0');
                if (seconds > (ulong.MaxValue - digit) / 10)
                {
                    seconds = ulong.MaxValue;
                    continue;
                }

                seconds = seconds * 10 + digit;
            }

            return true;
        }

        private static DateTimeOffset ComputeExpiry(ulong seconds)
        {
            var now = DateTimeOffset.UtcNow;
            var maxSeconds = (DateTimeOffset.MaxValue - now).TotalSeconds;
            if (seconds >= maxSeconds)
                return DateTimeOffset.MaxValue;

            return now.AddSeconds(seconds);
        }

        private static Uri UpgradeToHttps(Uri uri)
        {
            return new UriBuilder(uri)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = uri.Port == 80 || uri.IsDefaultPort ? -1 : uri.Port
            }.Uri;
        }

        private static string NormalizeHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return null;

            var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
            if (normalized.Length == 0 || normalized.IndexOfAny(new[] { '|', '\r', '\n' }) >= 0)
                return null;

            return normalized;
        }

        private static bool IsIpLiteral(string host)
        {
            var kind = Uri.CheckHostName(host);
            return kind == UriHostNameType.IPv4 || kind == UriHostNameType.IPv6;
        }

        private static string Unquote(string value)
        {
            if (IsQuotedString(value))
                return value[1..^1];
            return value;
        }

        private static bool IsQuotedString(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 2 || value[0] != '"' || value[^1] != '"')
                return false;

            for (var i = 1; i < value.Length - 1; i++)
            {
                var c = value[i];
                if (c == '\r' || c == '\n' || c == '\0')
                    return false;
                if (c == '\\')
                {
                    if (++i >= value.Length - 1)
                        return false;
                }
            }

            return true;
        }

        private static bool IsToken(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            foreach (var c in value)
            {
                if (char.IsLetterOrDigit(c))
                    continue;

                if (c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~')
                    continue;

                return false;
            }

            return true;
        }
    }
}
