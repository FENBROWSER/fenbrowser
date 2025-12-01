using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Reflection;
using FenBrowser.Core.Compat;

namespace FenBrowser.Core
{
    public sealed class ResourceManager
    {
        // Optional diagnostic sink; host can assign to surface timings in UI.
        public static System.Action<string> LogSink;
        private readonly HttpClient _http;

        private sealed class TextEntry { public string Body; public string ContentType; }
        private readonly Dictionary<string, LinkedListNode<Tuple<string, TextEntry>>> _textMap = new Dictionary<string, LinkedListNode<Tuple<string, TextEntry>>>(StringComparer.Ordinal);
        private readonly LinkedList<Tuple<string, TextEntry>> _textLru = new LinkedList<Tuple<string, TextEntry>>();
        private readonly int _textCap = 128;

        private sealed class ImgEntry { public byte[] Buffer; public string ContentType; }
        private readonly Dictionary<string, LinkedListNode<Tuple<string, ImgEntry>>> _imgMap = new Dictionary<string, LinkedListNode<Tuple<string, ImgEntry>>>(StringComparer.Ordinal);
        private readonly LinkedList<Tuple<string, ImgEntry>> _imgLru = new LinkedList<Tuple<string, ImgEntry>>();
        private readonly int _imgCap = 64;

        public Uri LastTextResponseUri { get; private set; }

        private sealed class HstsEntry { public DateTimeOffset Expiry; public bool IncludeSub; }
        private readonly Dictionary<string, HstsEntry> _hsts = new Dictionary<string, HstsEntry>(StringComparer.OrdinalIgnoreCase);
        private const string HstsFileName = "hsts_store_v1.txt";
        private readonly string _cacheRoot;

        public ResourceManager(HttpClient http)
        {
            _http = http ?? new HttpClient();
            _cacheRoot = Path.Combine(AppContext.BaseDirectory, "Cache");
            Directory.CreateDirectory(_cacheRoot);
            LoadHsts();
        }

        private void LoadHsts()
        {
            try
            {
                var path = Path.Combine(_cacheRoot, HstsFileName);
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var parts = lines[i].Split('|');
                        if (parts.Length >= 3)
                        {
                            DateTimeOffset exp; bool inc;
                            if (DateTimeOffset.TryParse(parts[1], out exp) && bool.TryParse(parts[2], out inc))
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
                var sb = new StringBuilder();
                foreach (var kv in _hsts)
                    if (kv.Value != null)
                        sb.Append(kv.Key).Append('|').Append(kv.Value.Expiry.ToString("o")).Append('|').Append(kv.Value.IncludeSub ? "true" : "false").Append('\n');
                
                var path = Path.Combine(_cacheRoot, HstsFileName);
                File.WriteAllText(path, sb.ToString());
            }
            catch { }
        }

        private static bool LooksTextual(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType)) return false;
            var ct = contentType.ToLowerInvariant();
            return ct.StartsWith("text/") || ct.Contains("javascript") || ct.Contains("json");
        }

        private static void AddHeaderSafe(HttpRequestMessage req, string name, string value)
        { try { if (!string.IsNullOrWhiteSpace(value)) req.Headers.TryAddWithoutValidation(name, value); } catch { } }

        private static string SafePartition(string origin)
        {
            return string.IsNullOrWhiteSpace(origin) ? "default" : origin.ToLowerInvariant();
        }

        private static string DetermineSecFetchSite(Uri referer, Uri request)
        {
            try
            {
                if (request == null) return "none";
                if (referer == null) return "none";
                var refHost = referer.Host ?? string.Empty;
                var reqHost = request.Host ?? string.Empty;
                if (string.Equals(refHost, reqHost, StringComparison.OrdinalIgnoreCase))
                    return "same-origin";
                if (IsSameSite(refHost, reqHost))
                    return "same-site";
                return "cross-site";
            }
            catch { return "none"; }
        }

        private static bool IsSameSite(string hostA, string hostB)
        {
            if (string.IsNullOrEmpty(hostA) || string.IsNullOrEmpty(hostB)) return false;
            if (hostA.EndsWith("." + hostB, StringComparison.OrdinalIgnoreCase)) return true;
            if (hostB.EndsWith("." + hostA, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string HashForFile(string key)
        {
            try
            {
                // Short, stable 64-bit FNV-1a hex digest
                var text = key ?? string.Empty;
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                unchecked
                {
                    ulong h = 1469598103934665603UL; // FNV-1a 64 offset basis
                    for (int i = 0; i < bytes.Length; i++) { h ^= bytes[i]; h *= 1099511628211UL; }
                    return h.ToString("x16");
                }
            }
            catch { return "0"; }
        }

        private static bool IsHttps(Uri u) => u != null && string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase);

        private Uri UpgradeIfHsts(Uri u)
        {
            try
            {
                if (u == null || IsHttps(u)) return u;
                var host = u.Host ?? string.Empty;
                foreach (var kv in _hsts)
                {
                    var d = kv.Value; if (d == null || d.Expiry <= DateTimeOffset.UtcNow) continue;
                    if (string.Equals(host, kv.Key, StringComparison.OrdinalIgnoreCase) || (d.IncludeSub && host.EndsWith("." + kv.Key, StringComparison.OrdinalIgnoreCase)))
                    {
                        var b = new UriBuilder(u) { Scheme = "https", Port = -1 };
                        return b.Uri;
                    }
                }
            }
            catch { }
            return u;
        }

        private void NoteHsts(HttpResponseMessage resp, Uri finalUri)
        {
            try
            {
                if (!IsHttps(finalUri) || resp == null) return;
                if (resp.Headers.TryGetValues("Strict-Transport-Security", out var values))
                {
                    var v = string.Join(",", values);
                    var max = Regex.Match(v, @"max-age\s*=\s*(?<s>\d+)", RegexOptions.IgnoreCase);
                    long sec = 0; if (!max.Success || !long.TryParse(max.Groups["s"].Value, out sec) || sec <= 0) return;
                    bool include = v.IndexOf("includesubdomains", StringComparison.OrdinalIgnoreCase) >= 0;
                    _hsts[finalUri.Host ?? ""] = new HstsEntry { Expiry = DateTimeOffset.UtcNow.AddSeconds(sec), IncludeSub = include };
                    SaveHsts();
                }
            }
            catch { }
        }

        // Text with redirect + small disk cache (5m TTL)
        public async Task<string> FetchTextAsync(Uri url, Uri referer = null, string accept = null, string secFetchDest = null)
        {
            if (url == null) return null;
            
            // Handle file scheme locally
            if (string.Equals(url.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return await File.ReadAllTextAsync(url.LocalPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FetchText] file failed: {url} {ex.Message}");
                    return null;
                }
            }

            url = UpgradeIfHsts(url);
            LastTextResponseUri = null;
            string partition = SafePartition(referer != null ? (referer.Host ?? "") : "");
            var folderPath = Path.Combine(_cacheRoot, "cache_" + partition);
            Directory.CreateDirectory(folderPath);
            
            var key = url.AbsoluteUri; var fname = HashForFile(key) + ".txt"; var meta = HashForFile(key) + ".meta";
            var filePath = Path.Combine(folderPath, fname);
            var metaPath = Path.Combine(folderPath, meta);

            try
            {
                if (File.Exists(filePath) && File.Exists(metaPath))
                {
                    try
                    {
                        var metaPayload = await File.ReadAllTextAsync(metaPath);
                        var timestampPart = metaPayload;
                        var finalPart = string.Empty;
                        if (!string.IsNullOrEmpty(metaPayload))
                        {
                            var split = metaPayload.IndexOf('|');
                            if (split >= 0)
                            {
                                timestampPart = metaPayload.Substring(0, split);
                                finalPart = metaPayload.Substring(split + 1);
                            }
                        }

                        DateTimeOffset ts;
                        if (DateTimeOffset.TryParse(timestampPart, out ts) && (DateTimeOffset.UtcNow - ts) < TimeSpan.FromMinutes(5))
                        {
                            Uri cachedFinal = null;
                            if (!string.IsNullOrWhiteSpace(finalPart))
                            {
                                try { if (!Uri.TryCreate(finalPart, UriKind.Absolute, out cachedFinal)) cachedFinal = null; } catch { cachedFinal = null; }
                            }
                            LastTextResponseUri = cachedFinal ?? url;
                            return await File.ReadAllTextAsync(filePath);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            var refererOriginal = referer;
            Uri previousRequest = null;

            try
            {
                var _startFetch = DateTimeOffset.UtcNow;
                Uri current = url; HttpResponseMessage resp = null; int hops = 0; HttpRequestMessage req = null;
                while (hops < 5)
                {
                    req = new HttpRequestMessage(HttpMethod.Get, current);
                    AddHeaderSafe(req, "Accept", string.IsNullOrWhiteSpace(accept) ? "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7" : accept);
                    
                    // Get User-Agent from settings
                    var destLower = (secFetchDest ?? "").ToLowerInvariant();
                    var useMobile = (destLower == "document" || destLower == "iframe");
                    var selectedUserAgent = BrowserSettings.Instance.SelectedUserAgent;
                    var ua = BrowserSettings.GetUserAgentString(selectedUserAgent, useMobile);
                    
                    AddHeaderSafe(req, "User-Agent", ua);
                    AddHeaderSafe(req, "Accept-Language", "en-US,en;q=0.9");
                    
                    var effectiveReferer = refererOriginal ?? previousRequest;
                    if (effectiveReferer != null) AddHeaderSafe(req, "Referer", effectiveReferer.AbsoluteUri);
                    
                    var cts = new System.Threading.CancellationTokenSource();
                    try
                    {
                        int sec = 8;
                        var d = (secFetchDest ?? "").ToLowerInvariant();
                        if (d == "document" || d == "iframe") sec = 12;
                        cts.CancelAfter(System.TimeSpan.FromSeconds(sec));
                    }
                    catch { }
                    try { resp = await _http.SendAsync(req, cts.Token); }
                    catch (Exception sendEx)
                    {
                        Console.WriteLine($"[FetchTextError] send failed {current} ex={sendEx.Message}");
                        resp = null;
                    }
                    
                    if (resp != null)
                    {
                        var code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            var loc = resp.Headers.Location; if (!loc.IsAbsoluteUri) loc = new Uri(current, loc);
                            previousRequest = current;
                            current = UpgradeIfHsts(loc);
                            hops++;
                            continue;
                        }
                    }
                    break;
                }
                if (resp == null || !resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[FetchTextFail] url={url} hops={hops} status={(resp!=null?(int)resp.StatusCode:0)}");
                    return null;
                }
                var finalUri = resp?.RequestMessage?.RequestUri ?? current ?? url;
                LastTextResponseUri = finalUri;
                NoteHsts(resp, finalUri ?? url);

                var ct = resp.Content != null && resp.Content.Headers != null && resp.Content.Headers.ContentType != null ? resp.Content.Headers.ContentType.MediaType : null;
                string text = null;
                try { text = await resp.Content.ReadAsStringAsync(); }
                catch (Exception bodyEx)
                {
                    try { System.Diagnostics.Debug.WriteLine("[FetchTextError] body read failed url=" + url + " ex=" + bodyEx.Message); } catch { }
                    text = null;
                }
                try { var _elapsed = DateTimeOffset.UtcNow - _startFetch; var _msg = "[FetchText] " + url + " in " + (int)_elapsed.TotalMilliseconds + "ms"; System.Diagnostics.Debug.WriteLine(_msg); if (LogSink != null) LogSink(_msg); } catch { }

                if (resp != null)
                {
                    Console.WriteLine($"[FetchTextDebug] url={url} status={resp.StatusCode} type={ct} len={text?.Length ?? -1}");
                    foreach (var h in resp.Headers) Console.WriteLine($"[FetchTextHeader] {h.Key}: {string.Join(",", h.Value)}");
                }

                if (LooksTextual(ct))
                {
                    // memory cache
                    var entry = new TextEntry { Body = text ?? string.Empty, ContentType = ct ?? string.Empty };
                    var pair = Tuple.Create(key, entry);
                    var node = new LinkedListNode<Tuple<string, TextEntry>>(pair);
                    _textLru.AddFirst(node); _textMap[key] = node;
                    if (_textLru.Count > _textCap) { var last = _textLru.Last; if (last != null) { _textMap.Remove(last.Value.Item1); _textLru.RemoveLast(); } }

                    // disk cache
                    try
                    {
                        await File.WriteAllTextAsync(filePath, entry.Body);
                        var metaPayload = DateTimeOffset.UtcNow.ToString("o") + "|" + (finalUri != null ? finalUri.AbsoluteUri : string.Empty);
                        await File.WriteAllTextAsync(metaPath, metaPayload);
                    }
                    catch { }
                }
                if (string.IsNullOrEmpty(text))
                {
                    try { System.Diagnostics.Debug.WriteLine("[FetchTextEmpty] url=" + url); } catch { }
                }
                return text;
            }
            catch (Exception ex) {
                try {
                    var msg = $"[FetchTextException] url={url} ex={ex.Message}";
                    System.Diagnostics.Debug.WriteLine(msg);
                    LogSink?.Invoke(msg);
                } catch { }
                // Return a clear error message for text resources
                return $"<!-- Resource load failed: {System.Net.WebUtility.HtmlEncode(url?.ToString() ?? "(null)")} : {System.Net.WebUtility.HtmlEncode(ex.Message)} -->";
            }
        }

        // Extended variant with explicit UA and Accept-Encoding overrides for multi-strategy fallback
        public async Task<string> FetchTextWithOptionsAsync(Uri url, Uri referer, string accept, string secFetchDest, string userAgentOverride, string acceptEncodingOverride)
        {
            if (url == null) return null;
            LastTextResponseUri = null;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddHeaderSafe(req, "Accept", string.IsNullOrWhiteSpace(accept) ? "*/*" : accept);
                AddHeaderSafe(req, "User-Agent", string.IsNullOrWhiteSpace(userAgentOverride) ? "Mozilla/5.0" : userAgentOverride);
                AddHeaderSafe(req, "Accept-Language", "en-US,en;q=0.9");
                AddHeaderSafe(req, "Accept-Encoding", string.IsNullOrWhiteSpace(acceptEncodingOverride) ? "gzip, deflate" : acceptEncodingOverride);
                AddHeaderSafe(req, "Sec-Fetch-Dest", string.IsNullOrWhiteSpace(secFetchDest) ? "empty" : secFetchDest);
                var fetchMode = "cors";
                var destLower = (secFetchDest ?? string.Empty).ToLowerInvariant();
                if (destLower == "document" || destLower == "iframe") fetchMode = "navigate";
                else if (destLower == "style" || destLower == "script" || destLower == "image" || destLower == "font") fetchMode = "no-cors";
                AddHeaderSafe(req, "Sec-Fetch-Mode", fetchMode);
                if (referer != null) AddHeaderSafe(req, "Referer", referer.AbsoluteUri);
                AddHeaderSafe(req, "Sec-Fetch-Site", DetermineSecFetchSite(referer, url));
                var cts = new System.Threading.CancellationTokenSource();
                try { cts.CancelAfter(TimeSpan.FromSeconds(12)); } catch { }
                HttpResponseMessage resp = null;
                try { resp = await _http.SendAsync(req, cts.Token); } catch (Exception sendEx) { try { System.Diagnostics.Debug.WriteLine("[FetchTextOptError] send " + url + " ex=" + sendEx.Message); } catch { } }
                if (resp == null || !resp.IsSuccessStatusCode)
                { try { System.Diagnostics.Debug.WriteLine("[FetchTextOptFail] url=" + url + " status=" + (resp!=null?(int)resp.StatusCode:0)); } catch { } return null; }
                LastTextResponseUri = resp.RequestMessage != null ? resp.RequestMessage.RequestUri : url;
                string text = null; try { text = await resp.Content.ReadAsStringAsync(); } catch (Exception bodyEx) { try { System.Diagnostics.Debug.WriteLine("[FetchTextOptError] body " + url + " ex=" + bodyEx.Message); } catch { } }
                if (string.IsNullOrEmpty(text)) { try { System.Diagnostics.Debug.WriteLine("[FetchTextOptEmpty] url=" + url); } catch { } }
                return text;
            }
            catch { return null; }
        }

        // Image with redirect and memory cache; disk caching optional later
        public async Task<Stream> FetchImageAsync(Uri url, Uri referer = null)
        {
            if (url == null) return null;

            // Handle file scheme locally
            if (string.Equals(url.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return File.OpenRead(url.LocalPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FetchImage] file failed: {url} {ex.Message}");
                    return null;
                }
            }

            // Handle data URIs
            if (string.Equals(url.Scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var dataUri = url.OriginalString;
                    var commaIndex = dataUri.IndexOf(',');
                    if (commaIndex > 5)
                    {
                        var header = dataUri.Substring(5, commaIndex - 5);
                        var data = dataUri.Substring(commaIndex + 1);
                        
                        bool isBase64 = header.IndexOf("base64", StringComparison.OrdinalIgnoreCase) >= 0;
                        byte[] bytes;
                        
                        if (isBase64)
                        {
                            try { bytes = Convert.FromBase64String(data); }
                            catch { return null; }
                        }
                        else
                        {
                            var decoded = Uri.UnescapeDataString(data);
                            bytes = Encoding.UTF8.GetBytes(decoded);
                        }
                        
                        return new MemoryStream(bytes);
                    }
                    return null;
                }
                catch { return null; }
            }

            url = UpgradeIfHsts(url);
            var key = url.AbsoluteUri;

            LinkedListNode<Tuple<string, ImgEntry>> node;
            if (_imgMap.TryGetValue(key, out node) && node != null && node.Value != null && node.Value.Item2 != null && node.Value.Item2.Buffer != null)
            {
                return new MemoryStream(node.Value.Item2.Buffer);
            }

            var refererOriginal = referer;
            Uri previousRequest = null;

            try
            {
                var _startImg = DateTimeOffset.UtcNow;
                Uri current = url; HttpResponseMessage resp = null; int hops = 0; HttpRequestMessage req = null;
                while (hops < 5)
                {
                    req = new HttpRequestMessage(HttpMethod.Get, current);
                    AddHeaderSafe(req, "Accept", "image/apng,image/png,image/jpeg,image/*,*/*;q=0.8");
                    AddHeaderSafe(req, "User-Agent", BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent));
                    AddHeaderSafe(req, "Accept-Language", "en-US,en;q=0.9");
                    AddHeaderSafe(req, "Accept-Encoding", "gzip, deflate");
                    AddHeaderSafe(req, "Sec-Fetch-Dest", "image");
                    AddHeaderSafe(req, "Sec-Fetch-Mode", "no-cors");
                    var effectiveReferer = refererOriginal ?? previousRequest;
                    if (effectiveReferer != null) AddHeaderSafe(req, "Referer", effectiveReferer.AbsoluteUri);
                    var cts = new System.Threading.CancellationTokenSource();
                    try { cts.CancelAfter(System.TimeSpan.FromSeconds(8)); } catch { }
                    resp = await _http.SendAsync(req, cts.Token);
                    
                    if (resp != null)
                    {
                        var code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            var loc = resp.Headers.Location; if (!loc.IsAbsoluteUri) loc = new Uri(current, loc);
                            previousRequest = current;
                            current = UpgradeIfHsts(loc);
                            hops++;
                            continue;
                        }
                    }
                    break;
                }
                if (resp == null || !resp.IsSuccessStatusCode)
                {
                    return null;
                }
                NoteHsts(resp, url);

                var buf = await HttpCache.Instance.GetBufferAsync(_http, req) ?? await resp.Content.ReadAsByteArrayAsync();

                var entry = new ImgEntry { Buffer = buf, ContentType = resp.Content != null && resp.Content.Headers != null && resp.Content.Headers.ContentType != null ? resp.Content.Headers.ContentType.MediaType : null };
                var pair = Tuple.Create(key, entry);
                var n = new LinkedListNode<Tuple<string, ImgEntry>>(pair);
                _imgLru.AddFirst(n); _imgMap[key] = n;
                if (_imgLru.Count > _imgCap) { var last = _imgLru.Last; if (last != null) { _imgMap.Remove(last.Value.Item1); _imgLru.RemoveLast(); } }

                return new MemoryStream(buf);
            }
            catch (Exception ex) {
                try {
                    var msg = $"[FetchImageException] url={url} ex={ex.Message}";
                    System.Diagnostics.Debug.WriteLine(msg);
                    LogSink?.Invoke(msg);
                } catch { }
                return null;
            }
        }

        // Generic binary fetcher for fonts and other non-text assets
        public async Task<byte[]> FetchBytesAsync(Uri url, Uri referer = null, string accept = null, string secFetchDest = null)
        {
            if (url == null) return null;
            url = UpgradeIfHsts(url);
            try
            {
                Uri current = url; HttpResponseMessage resp = null; int hops = 0; HttpRequestMessage req = null;
                while (hops < 10)
                {
                    req = new HttpRequestMessage(HttpMethod.Get, current);
                    AddHeaderSafe(req, "Accept", string.IsNullOrWhiteSpace(accept) ? "*/*" : accept);
                    if (!string.IsNullOrWhiteSpace(secFetchDest)) AddHeaderSafe(req, "Sec-Fetch-Dest", secFetchDest);
                    AddHeaderSafe(req, "Sec-Fetch-Mode", "no-cors");
                    if (referer != null) AddHeaderSafe(req, "Referer", referer.AbsoluteUri);
                    var cts = new System.Threading.CancellationTokenSource();
                    try
                    {
                        int sec = 8;
                        var d = (secFetchDest ?? "").ToLowerInvariant();
                        if (d == "font") sec = 12; // fonts can be larger
                        cts.CancelAfter(System.TimeSpan.FromSeconds(sec));
                    }
                    catch { }
                    resp = await _http.SendAsync(req, cts.Token);
                    if (resp != null)
                    {
                        var code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            var loc = resp.Headers.Location; if (!loc.IsAbsoluteUri) loc = new Uri(current, loc);
                            var prev = current;
                            current = UpgradeIfHsts(loc);
                            referer = prev;
                            hops++;
                            continue;
                        }
                    }
                    break;
                }
                if (resp == null || !resp.IsSuccessStatusCode) return null;
                NoteHsts(resp, url);

                var buf = await HttpCache.Instance.GetBufferAsync(_http, req) ?? await resp.Content.ReadAsByteArrayAsync();
                return buf;
            }
            catch { return null; }
        }
    }
}
