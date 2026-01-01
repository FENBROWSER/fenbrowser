using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Reflection;
using FenBrowser.Core.Compat;
using FenBrowser.Core.Network;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.Core.Security;
using System.Net.Http.Headers;

namespace FenBrowser.Core
{
    public enum FetchStatus
    {
        Success,
        ConnectionFailed,
        SslError,
        Timeout,
        NotFound,
        UnknownError
    }

    public class FetchResult
    {
        public FetchStatus Status;
        public string Content;
        public string ErrorDetail;
        public int StatusCode;
        public Uri FinalUri;
        public string ContentType;
        public CertificateInfo Certificate;
        public System.Net.Security.SslPolicyErrors SslErrors;
        public HttpResponseHeaders Headers;
    }

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



        private readonly string _cacheRoot;
        private readonly INetworkClient _client;

        public int BlockedRequestCount { get; private set; }
        public event EventHandler<int> BlockedCountChanged;
        
        // DevTools Network Monitoring Events
        public event Action<string, HttpRequestMessage> NetworkRequestStarting;
        public event Action<string, HttpResponseMessage> NetworkRequestCompleted;
        public event Action<string, Exception> NetworkRequestFailed;

        public void ResetBlockedCount()
        {
            BlockedRequestCount = 0;
            BlockedCountChanged?.Invoke(this, 0);
        }

        private readonly bool _isPrivate;

        public CspPolicy ActivePolicy { get; set; }

        public ResourceManager(HttpClient http, bool isPrivate = false)
        {
            _isPrivate = isPrivate;
            if (!_isPrivate)
            {
                _cacheRoot = Path.Combine(AppContext.BaseDirectory, "Cache");
                Directory.CreateDirectory(_cacheRoot);
            }
            else
            {
                // In private mode, use a temp path or just don't use disk at all. 
                // We'll set it to null and check before access.
                _cacheRoot = null; 
            }

            var handlers = new List<INetworkHandler>
            {
                new TrackingPreventionHandler(), // Enhanced Tracking Prevention (Phase 5)
                new AdBlockHandler(() => BrowserSettings.Instance.EnableTrackingPrevention),
                new PrivacyHandler(),
                // Only enable HSTS disk persistence if not private
                new HstsHandler(_isPrivate ? null : Path.Combine(AppContext.BaseDirectory, "Cache")), 
                new HttpHandler(http)
            };
            _client = new NetworkClient(handlers);
        }

        public void ClearCache()
        {
            // Clear memory
            lock (_textMap) { _textMap.Clear(); _textLru.Clear(); }
            lock (_imgMap) { _imgMap.Clear(); _imgLru.Clear(); }

            // Clear disk (skip if private or path null)
            try
            {
                if (!_isPrivate && !string.IsNullOrEmpty(_cacheRoot) && Directory.Exists(_cacheRoot))
                {
                    Directory.Delete(_cacheRoot, true);
                    Directory.CreateDirectory(_cacheRoot);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClearCache] Failed to delete disk cache: {ex.Message}");
            }
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



        // Text with redirect + small disk cache (5m TTL)
        public async Task<string> FetchTextAsync(Uri url, Uri referer = null, string accept = null, string secFetchDest = null)
        {
            if (url == null) return null;

            // Handle data: URI scheme (e.g., data:text/css,.picture%20%7B%20background%3A%20none%3B%20%7D)
            if (string.Equals(url.Scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var dataUri = url.AbsoluteUri;
                    var commaIndex = dataUri.IndexOf(',');
                    if (commaIndex > 0)
                    {
                        var dataMeta = dataUri.Substring(5, commaIndex - 5); // Skip "data:" prefix
                        var content = dataUri.Substring(commaIndex + 1);
                        
                        // Check if base64 encoded
                        bool isBase64 = dataMeta.Contains("base64", StringComparison.OrdinalIgnoreCase);
                        
                        if (isBase64)
                        {
                            // Remove base64 marker and decode
                            var cleanMeta = dataMeta.Replace(";base64", "").Replace("base64", "");
                            // Fix: URL-decode before base64 decode because data URIs can be URL-encoded
                            var decodedContent = Uri.UnescapeDataString(content);
                            var bytes = Convert.FromBase64String(decodedContent);
                            return System.Text.Encoding.UTF8.GetString(bytes);
                        }
                        else
                        {
                            // URL-decode the content
                            return Uri.UnescapeDataString(content);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FetchText] data URI failed: {ex.Message}");
                }
                return null;
            }

            // CSP Check
            if (ActivePolicy != null)
            {
                var dest = secFetchDest ?? ""; 
                var directive = "default-src";
                if (dest == "script") directive = "script-src";
                else if (dest == "style") directive = "style-src";
                else if (dest == "worker") directive = "child-src"; // or worker-src
                else if (dest == "iframe") directive = "frame-src";
                
                if (directive != "default-src" || !string.IsNullOrEmpty(dest))
                {
                    // For fetch/xhr
                    if (string.IsNullOrEmpty(dest)) directive = "connect-src";
                }
                
                // If checking subresources
                if (!ActivePolicy.IsAllowed(directive, url))
                {
                    System.Diagnostics.Debug.WriteLine($"[CSP] Blocked {url} ({directive})");
                    return null;
                }
            }
            
            // Handle file scheme locally
            if (string.Equals(url.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return await File.ReadAllTextAsync(url.LocalPath).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FetchText] file failed: {url} {ex.Message}");
                    return null;
                }
            }

            // url = UpgradeIfHsts(url); // Handled by HstsHandler
            LastTextResponseUri = null;

            string partition = SafePartition(referer != null ? (referer.Host ?? "") : "");
            var folderPath = Path.Combine(_cacheRoot, "cache_" + partition);
            Directory.CreateDirectory(folderPath);
            
            var key = url.AbsoluteUri; var fname = HashForFile(key) + ".txt"; var meta = HashForFile(key) + ".meta";
            var filePath = Path.Combine(folderPath, fname);
            var metaPath = Path.Combine(folderPath, meta);

            try
            {
                // Skip disk cache read if private
                if (!_isPrivate && File.Exists(filePath) && File.Exists(metaPath))
                {
                    try
                    {
                        var metaPayload = await File.ReadAllTextAsync(metaPath).ConfigureAwait(false);
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
                            return await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
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
                    
                    // Add Client Hints for modern detection
                    if (selectedUserAgent == UserAgentType.Chrome || selectedUserAgent == UserAgentType.FenBrowser)
                    {
                        AddHeaderSafe(req, "Sec-CH-UA", "\"Not A(Brand\";v=\"8\", \"Chromium\";v=\"145\", \"Google Chrome\";v=\"145\"");
                        AddHeaderSafe(req, "Sec-CH-UA-Mobile", useMobile ? "?1" : "?0");
                        AddHeaderSafe(req, "Sec-CH-UA-Platform", "\"Windows\"");
                    }
                    
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
                    try { resp = await SendRequestTrackedAsync(req, cts.Token).ConfigureAwait(false); }
                    catch (Exception sendEx)
                    {
                        Console.WriteLine($"[FetchTextError] send failed {current} ex={sendEx.Message}");
                        resp = null;
                    }
                    
                    if (resp != null && resp.StatusCode == System.Net.HttpStatusCode.Forbidden && resp.ReasonPhrase == "Blocked by AdBlock")
                    {
                        BlockedRequestCount++;
                        BlockedCountChanged?.Invoke(this, BlockedRequestCount);
                    }

                    if (resp != null)
                    {
                        var code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            var loc = resp.Headers.Location; if (!loc.IsAbsoluteUri) loc = new Uri(current, loc);
                            previousRequest = current;
                            // current = UpgradeIfHsts(loc); // Handled by HstsHandler on next pass
                            current = loc;
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
                // NoteHsts(resp, finalUri ?? url); // Handled by HstsHandler

                var ct = resp.Content != null && resp.Content.Headers != null && resp.Content.Headers.ContentType != null ? resp.Content.Headers.ContentType.MediaType : null;
                string text = null;
                try { text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
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

                    // disk cache (skip if private)
                    if (!_isPrivate)
                    {
                        try
                        {
                            Directory.CreateDirectory(folderPath); // Ensure dir exists
                            await File.WriteAllTextAsync(filePath, entry.Body).ConfigureAwait(false);
                            var metaPayload = DateTimeOffset.UtcNow.ToString("o") + "|" + (finalUri != null ? finalUri.AbsoluteUri : string.Empty);
                            await File.WriteAllTextAsync(metaPath, metaPayload).ConfigureAwait(false);
                        }
                        catch { }
                    }
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

        public async Task<FetchResult> FetchTextDetailedAsync(Uri url, Uri referer = null, string accept = null, string secFetchDest = null)
        {
            if (url == null) return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "URL is null" };
            
            /* [PERF-REMOVED] */

            // Handle file scheme locally
            if (string.Equals(url.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(url.LocalPath).ConfigureAwait(false);
                    return new FetchResult { Status = FetchStatus.Success, Content = text, FinalUri = url, ContentType = "text/html" };
                }
                catch (FileNotFoundException)
                {
                    return new FetchResult { Status = FetchStatus.NotFound, ErrorDetail = "File not found", FinalUri = url };
                }
                catch (Exception ex)
                {
                    return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = ex.Message, FinalUri = url };
                }
            }

            // url = UpgradeIfHsts(url); // Handled by HstsHandler
            LastTextResponseUri = null;
            
            // ... (Cache logic omitted for brevity in detailed fetch for now, or we can duplicate/refactor. 
            // For this task, let's focus on the network part to get errors right. 
            // Ideally we refactor FetchTextAsync to use this, but to minimize risk I'll implement the network logic here.)

            var refererOriginal = referer;
            Uri previousRequest = null;

            try
            {
                var _startFetch = DateTimeOffset.UtcNow;
                Uri current = url; HttpResponseMessage resp = null; int hops = 0; HttpRequestMessage req = null;
                while (hops < 5)
                {
                    /* [PERF-REMOVED] */
                    req = new HttpRequestMessage(HttpMethod.Get, current);
                    AddHeaderSafe(req, "Accept", string.IsNullOrWhiteSpace(accept) ? "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7" : accept);
                    
                    var destLower = (secFetchDest ?? "").ToLowerInvariant();
                    var useMobile = (destLower == "document" || destLower == "iframe");
                    var selectedUserAgent = BrowserSettings.Instance.SelectedUserAgent;
                    /* [PERF-REMOVED] */
                    var ua = BrowserSettings.GetUserAgentString(selectedUserAgent, useMobile);
                    
                    AddHeaderSafe(req, "User-Agent", ua);

                    // Add Client Hints
                    if (selectedUserAgent == UserAgentType.Chrome || selectedUserAgent == UserAgentType.FenBrowser)
                    {
                        AddHeaderSafe(req, "Sec-CH-UA", "\"Not A(Brand\";v=\"8\", \"Chromium\";v=\"145\", \"Google Chrome\";v=\"145\"");
                        AddHeaderSafe(req, "Sec-CH-UA-Mobile", useMobile ? "?1" : "?0");
                        AddHeaderSafe(req, "Sec-CH-UA-Platform", "\"Windows\"");
                    }

                    AddHeaderSafe(req, "Accept-Language", "en-US,en;q=0.9");
                    
                    var effectiveReferer = refererOriginal ?? previousRequest;
                    if (effectiveReferer != null) AddHeaderSafe(req, "Referer", effectiveReferer.AbsoluteUri);
                    
                    var cts = new System.Threading.CancellationTokenSource();
                    try
                    {
                        int sec = 30;
                        var d = (secFetchDest ?? "").ToLowerInvariant();
                        if (d == "document" || d == "iframe") sec = 60;
                        cts.CancelAfter(System.TimeSpan.FromSeconds(sec));
                    }
                    catch { }

                    try 
                    { 
                        /* [PERF-REMOVED] */
                        resp = await SendRequestTrackedAsync(req, cts.Token).ConfigureAwait(false); 
                    }
                    catch (TaskCanceledException)
                    {
                        return new FetchResult { Status = FetchStatus.Timeout, ErrorDetail = "Connection timed out", FinalUri = current };
                    }
                    catch (HttpRequestException httpEx)
                    {
                        // Try to detect SSL/DNS errors from the message or inner exception
                        var msg = httpEx.Message;
                        if (httpEx.InnerException != null) msg += " " + httpEx.InnerException.Message;
                        
                        if (msg.Contains("SSL") || msg.Contains("cert") || msg.Contains("security"))
                            return new FetchResult { Status = FetchStatus.SslError, ErrorDetail = msg, FinalUri = current };
                        
                        return new FetchResult { Status = FetchStatus.ConnectionFailed, ErrorDetail = msg, FinalUri = current };
                    }
                    catch (Exception sendEx)
                    {
                         return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = sendEx.Message, FinalUri = current };
                    }
                    
                    if (resp != null)
                    {
                        var code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            var loc = resp.Headers.Location; if (!loc.IsAbsoluteUri) loc = new Uri(current, loc);
                            previousRequest = current;
                            // current = UpgradeIfHsts(loc); // Handled by HstsHandler
                            current = loc;
                            hops++;
                            /* [PERF-REMOVED] */
                            continue;
                        }
                    }
                    break;
                }

                if (resp == null)
                {
                     return new FetchResult { Status = FetchStatus.ConnectionFailed, ErrorDetail = "No response received", FinalUri = current };
                }

                var finalUri = resp?.RequestMessage?.RequestUri ?? current ?? url;
                LastTextResponseUri = finalUri;
                // NoteHsts(resp, finalUri ?? url); // Handled by HstsHandler

                var ct = resp.Content != null && resp.Content.Headers != null && resp.Content.Headers.ContentType != null ? resp.Content.Headers.ContentType.MediaType : null;
                
                if (!resp.IsSuccessStatusCode)
                {
                    // 404, 500, etc.
                    string errBody = null;
                    try { errBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch {}
                    
                    FetchStatus status = FetchStatus.UnknownError;
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) status = FetchStatus.NotFound;
                    else if ((int)resp.StatusCode >= 500) status = FetchStatus.ConnectionFailed; // Server error

                    return new FetchResult { 
                        Status = status, 
                        StatusCode = (int)resp.StatusCode, 
                        ErrorDetail = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}", 
                        Content = errBody,
                        FinalUri = finalUri,
                        ContentType = ct
                    };
                }

                string text = null;
                try { text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); }
                catch (Exception bodyEx)
                {
                    return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = "Failed to read body: " + bodyEx.Message, FinalUri = finalUri };
                }

                return new FetchResult { 
                    Status = FetchStatus.Success, 
                    Content = text, 
                    StatusCode = (int)resp.StatusCode, 
                    FinalUri = finalUri,
                    ContentType = ct,
                    Headers = resp.Headers
                };
            }
            catch (Exception ex) {
                return new FetchResult { Status = FetchStatus.UnknownError, ErrorDetail = ex.Message, FinalUri = url };
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
                try { cts.CancelAfter(TimeSpan.FromSeconds(30)); } catch { }
                HttpResponseMessage resp = null;
                try { resp = await SendRequestTrackedAsync(req, cts.Token).ConfigureAwait(false); } catch (Exception sendEx) { try { System.Diagnostics.Debug.WriteLine("[FetchTextOptError] send " + url + " ex=" + sendEx.Message); } catch { } }
                if (resp == null || !resp.IsSuccessStatusCode)
                { try { System.Diagnostics.Debug.WriteLine("[FetchTextOptFail] url=" + url + " status=" + (resp!=null?(int)resp.StatusCode:0)); } catch { } return null; }
                LastTextResponseUri = resp.RequestMessage != null ? resp.RequestMessage.RequestUri : url;
                string text = null; try { text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch (Exception bodyEx) { try { System.Diagnostics.Debug.WriteLine("[FetchTextOptError] body " + url + " ex=" + bodyEx.Message); } catch { } }
                if (string.IsNullOrEmpty(text)) { try { System.Diagnostics.Debug.WriteLine("[FetchTextOptEmpty] url=" + url); } catch { } }
                return text;
            }
            catch { return null; }
        }

        // Image with redirect and memory cache; disk caching optional later
        public async Task<Stream> FetchImageAsync(Uri url, Uri referer = null)
        {
            if (url == null) return null;

            // CSP Check
            if (ActivePolicy != null && !ActivePolicy.IsAllowed("img-src", url))
            {
                 System.Diagnostics.Debug.WriteLine($"[CSP] Blocked image {url}");
                 return null;
            }

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
                            try 
                            { 
                                // Fix: URL-decode before base64 decode
                                var decodedData = Uri.UnescapeDataString(data);
                                bytes = Convert.FromBase64String(decodedData); 
                            }
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

            // url = UpgradeIfHsts(url); // Handled by HstsHandler
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
                    try { cts.CancelAfter(System.TimeSpan.FromSeconds(30)); } catch { }
                    resp = await SendRequestTrackedAsync(req, cts.Token).ConfigureAwait(false);
                    
                    if (resp != null)
                    {
                        var code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            var loc = resp.Headers.Location; if (!loc.IsAbsoluteUri) loc = new Uri(current, loc);
                            previousRequest = current;
                            // current = UpgradeIfHsts(loc); // Handled by HstsHandler
                            current = loc;
                            hops++;
                            continue;
                        }
                    }
                    break;
                }
                if (resp == null || !resp.IsSuccessStatusCode)
                {
                    /* [PERF-REMOVED] */
                    return null;
                }
                // NoteHsts(resp, url); // Handled by HstsHandler

                var buf = await HttpCache.Instance.GetBufferAsync(null, req).ConfigureAwait(false) ?? await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

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
                    System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\debug_log.txt", msg + "\r\n");
                } catch { }
                return null;
            }
        }

        // Generic binary fetcher for fonts and other non-text assets
        public async Task<byte[]> FetchBytesAsync(Uri url, Uri referer = null, string accept = null, string secFetchDest = null)
        {
            if (url == null) return null;
            
            // CSP Check (fonts, media, etc)
            if (ActivePolicy != null)
            {
                var directive = "default-src";
                if (secFetchDest == "font") directive = "font-src";
                else if (secFetchDest == "audio" || secFetchDest == "video") directive = "media-src";
                else if (secFetchDest == "object") directive = "object-src";
                
                if (!ActivePolicy.IsAllowed(directive, url)) return null;
            }

            // url = UpgradeIfHsts(url); // Handled by HstsHandler
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
                        int sec = 30;
                        var d = (secFetchDest ?? "").ToLowerInvariant();
                        if (d == "font") sec = 60; // fonts can be larger
                        cts.CancelAfter(System.TimeSpan.FromSeconds(sec));
                    }
                    catch { }
                    resp = await SendRequestTrackedAsync(req, cts.Token).ConfigureAwait(false);
                    if (resp != null)
                    {
                        var code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400 && resp.Headers.Location != null)
                        {
                            var loc = resp.Headers.Location; if (!loc.IsAbsoluteUri) loc = new Uri(current, loc);
                            var prev = current;
                            // current = UpgradeIfHsts(loc); // Handled by HstsHandler
                            current = loc;
                            referer = prev;
                            hops++;
                            continue;
                        }
                    }
                    break;
                }
                if (resp == null || !resp.IsSuccessStatusCode) return null;
                // NoteHsts(resp, url); // Handled by HstsHandler

                var buf = await HttpCache.Instance.GetBufferAsync(null, req).ConfigureAwait(false) ?? await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return buf;
            }
            catch { return null; }
        }

        private async Task<HttpResponseMessage> SendRequestTrackedAsync(HttpRequestMessage req, CancellationToken token)
        {
            // Generate a tracking ID
            string id = Guid.NewGuid().ToString("N");
            
            try
            {
                /* [PERF-REMOVED] */
                NetworkRequestStarting?.Invoke(id, req);
                
                var resp = await _client.SendAsync(req, token).ConfigureAwait(false);
                
                /* [PERF-REMOVED] */
                try
                {
                    NetworkRequestCompleted?.Invoke(id, resp);
                }
                catch (Exception invokeEx) 
                {
                    /* [PERF-REMOVED] */
                }
                return resp;
            }
            catch (Exception ex)
            {
                /* [PERF-REMOVED] */
                NetworkRequestFailed?.Invoke(id, ex);
                throw;
            }
        }
    }
}

