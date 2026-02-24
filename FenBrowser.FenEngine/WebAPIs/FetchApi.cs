using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    public static class FetchApi
    {
        public static void Register(IExecutionContext context, Func<HttpRequestMessage, Task<HttpResponseMessage>> fetchHandler)
        {
            var global = context.Environment;
            
            // Register 'fetch' global function
            global.Set("fetch", FenValue.FromFunction(new FenFunction("fetch", (args, thisVal) => Fetch(args, thisVal, context, fetchHandler))));

            // Register constructors
            global.Set("Request", FenValue.FromFunction(new FenFunction("Request", JsRequest.Constructor)));
            global.Set("Headers", FenValue.FromFunction(new FenFunction("Headers", JsHeaders.Constructor)));
            global.Set("Response", FenValue.FromFunction(new FenFunction("Response", JsResponse.Constructor)));
        }

        private static FenValue Fetch(FenValue[] args, FenValue thisVal, IExecutionContext context, Func<HttpRequestMessage, Task<HttpResponseMessage>> fetchHandler)
        {
            if (args.Length < 1) return FenValue.Undefined;

            var input = args[0];
            var init = args.Length > 1 ? args[1] : FenValue.Undefined;
            
            JsRequest request;
            if (input is FenValue fv && fv.IsObject && fv.AsObject() is JsRequest jsReq)
            {
                request = jsReq;
            }
            else
            {
                request = new JsRequest(input.ToString(), init);
            }
            
            

             // Return a Promise
             return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("executor", (execArgs, execThis) => 
             {
                 var resolve = execArgs[0].AsFunction();
                 var reject = execArgs[1].AsFunction();

                 Task.Run(async () => 
                 {
                     try
                     {
                        DiagnosticPaths.AppendRootText("js_debug.log", $"[Fetch] Start: {request.Method} {request.Url}\n");
                        
                        // 1. Service Worker Interception
                        var sw = FenBrowser.FenEngine.Workers.ServiceWorkerManager.Instance.GetController(request.Url);
                        if (sw != null)
                        {
                            var fetchEvt = new FetchEvent("fetch", request, context);
                            var handled = await FenBrowser.FenEngine.Workers.ServiceWorkerManager.Instance
                                .DispatchFetchEvent(sw, fetchEvt)
                                .ConfigureAwait(false);
                            if (handled)
                            {
                                DiagnosticPaths.AppendRootText("js_debug.log", $"[Fetch] ServiceWorker handled event for {request.Url}\n");
                                var swSettlement = await fetchEvt
                                    .WaitForRespondWithSettlementAsync(TimeSpan.FromMilliseconds(1000))
                                    .ConfigureAwait(false);

                                if (swSettlement.IsFulfilled &&
                                    TryCreateJsResponseFromServiceWorkerValue(swSettlement.Value, context, out var swResponse))
                                {
                                    resolve.Invoke(new FenValue[] { FenValue.FromObject(swResponse) }, context);
                                    return;
                                }

                                if (swSettlement.IsRejected)
                                {
                                    var rejection = swSettlement.Value.IsUndefined ? "respondWith() rejected" : swSettlement.Value.ToString();
                                    throw new Exception($"ServiceWorker respondWith rejection: {rejection}");
                                }

                                if (swSettlement.IsTimeout)
                                {
                                    DiagnosticPaths.AppendRootText("js_debug.log", $"[Fetch] ServiceWorker respondWith timeout for {request.Url}; falling back to network\n");
                                }
                                else
                                {
                                    DiagnosticPaths.AppendRootText("js_debug.log", $"[Fetch] ServiceWorker provided unrecognized response for {request.Url}; falling back to network\n");
                                }
                            }
                        }

                        // 2. Network Fetch via Handler
                        if (fetchHandler  == null) throw new Exception("FetchHandler missing");

                        // SECURITY: Validate URL (scheme + private-IP block) and method
                        ValidateFetchUrl(request.Url);

                        var req = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
                        if (request.Body != null)
                        {
                            req.Content = new StringContent(request.Body);
                            var ct = request.Headers.GetHeader("content-type");
                            if (!string.IsNullOrEmpty(ct)) 
                            {
                                try { req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ct); } catch {}
                            }
                        }

                        foreach(var kv in request.Headers.GetHeaders())
                        {
                            if (kv.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)) continue;
                            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                        }

                        var resp = await fetchHandler(req);
                        DiagnosticPaths.AppendRootText("js_debug.log", $"[Fetch] Response: {resp.StatusCode} for {request.Url}\n");
                        
                        var jsResp = new JsResponse(resp, context);
                        
                        resolve.Invoke(new FenValue[] { FenValue.FromObject(jsResp) }, context);
                     }
                     catch (Exception ex)
                     {
                         DiagnosticPaths.AppendRootText("js_debug.log", $"[Fetch] Error: {ex.Message} for {request.Url}\n");
                         reject.Invoke(new FenValue[] { FenValue.FromString(ex.Message) }, context);
                     }
                 });
                 return FenValue.Undefined;
             })), context));
        }

        private static bool TryCreateJsResponseFromServiceWorkerValue(FenValue value, IExecutionContext context, out JsResponse jsResponse)
        {
            jsResponse = null;
            if (!value.IsObject)
            {
                return false;
            }

            if (value.AsObject() is JsResponse existingResponse)
            {
                jsResponse = existingResponse;
                return true;
            }

            var obj = value.AsObject();
            if (obj == null)
            {
                return false;
            }

            var status = 200;
            if (obj.Has("status") && obj.Get("status").IsNumber)
            {
                status = (int)obj.Get("status").ToNumber();
            }

            if (!Enum.IsDefined(typeof(System.Net.HttpStatusCode), status))
            {
                status = 200;
            }

            var response = new HttpResponseMessage((System.Net.HttpStatusCode)status);

            if (obj.Has("body"))
            {
                var bodyValue = obj.Get("body");
                if (!bodyValue.IsUndefined && !bodyValue.IsNull)
                {
                    response.Content = new StringContent(bodyValue.ToString());
                }
            }

            if (obj.Has("headers"))
            {
                var headersValue = obj.Get("headers");
                if (headersValue.IsObject)
                {
                    TryCopyHeaders(headersValue.AsObject(), response);
                }
            }

            jsResponse = new JsResponse(response, context);
            return true;
        }

        private static void TryCopyHeaders(IObject headerObject, HttpResponseMessage response)
        {
            if (headerObject == null || response == null)
            {
                return;
            }

            if (headerObject is JsHeaders jsHeaders)
            {
                foreach (var header in jsHeaders.GetHeaders())
                {
                    TryApplyHeader(response, header.Key, header.Value);
                }
                return;
            }

            foreach (var key in headerObject.Keys())
            {
                TryApplyHeader(response, key, headerObject.Get(key).ToString());
            }
        }

        private static void TryApplyHeader(HttpResponseMessage response, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (!response.Headers.TryAddWithoutValidation(key, value))
            {
                response.Content ??= new StringContent(string.Empty);
                response.Content.Headers.TryAddWithoutValidation(key, value);
            }
        }

        // ------------------------------------------------------------------ security helpers

        // SECURITY: Only http/https permitted — blocks file://, data:, ftp:, javascript:, etc.
        internal static void ValidateFetchUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                throw new InvalidOperationException($"Failed to fetch: '{url}' is not a valid URL.");

            var scheme = uri.Scheme?.ToLowerInvariant();
            if (scheme != "http" && scheme != "https")
                throw new InvalidOperationException($"Failed to fetch: URL scheme '{scheme}:' is not allowed.");

            if (IsPrivateOrReservedHost(uri.Host?.ToLowerInvariant()))
                throw new InvalidOperationException("Failed to fetch: Requests to private or internal network addresses are blocked.");
        }

        // SECURITY: Whitelist valid HTTP methods — prevents CRLF/request-smuggling injection.
        private static readonly HashSet<string> _allowedHttpMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "GET", "POST", "PUT", "DELETE", "HEAD", "OPTIONS", "PATCH" };

        internal static string ValidateMethod(string method)
        {
            var upper = (method ?? "GET").Trim().ToUpperInvariant();
            if (!_allowedHttpMethods.Contains(upper))
                throw new InvalidOperationException($"Failed to fetch: '{method}' is not an allowed HTTP method.");
            return upper;
        }

        // SECURITY: WHATWG forbidden request headers
        internal static readonly HashSet<string> _forbiddenRequestHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "accept-charset", "accept-encoding", "access-control-request-headers",
            "access-control-request-method", "connection", "content-length",
            "cookie", "cookie2", "date", "dnt", "expect", "host", "keep-alive",
            "origin", "referer", "te", "trailer", "transfer-encoding", "upgrade", "via"
        };

        // SECURITY: Strip CR/LF from header values to prevent header injection
        internal static string SanitizeHeaderValue(string value) =>
            value?.Replace("\r", "").Replace("\n", "") ?? "";

        // SECURITY: Returns true for loopback, private RFC-1918, link-local, and CGNAT ranges.
        private static bool IsPrivateOrReservedHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return true;
            if (host == "localhost" || host == "ip6-localhost" || host == "ip6-loopback") return true;

            if (!System.Net.IPAddress.TryParse(host, out var ip)) return false;

            var bytes = ip.GetAddressBytes();

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return bytes[0] == 127                                              // 127.0.0.0/8  loopback
                    || bytes[0] == 0                                                // 0.0.0.0/8
                    || bytes[0] == 10                                               // 10.0.0.0/8   RFC-1918
                    || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)       // 172.16.0.0/12 RFC-1918
                    || (bytes[0] == 192 && bytes[1] == 168)                         // 192.168.0.0/16 RFC-1918
                    || (bytes[0] == 169 && bytes[1] == 254)                         // 169.254.0.0/16 link-local / AWS metadata
                    || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);     // 100.64.0.0/10 CGNAT
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return ip.Equals(System.Net.IPAddress.IPv6Loopback)                // ::1
                    || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)            // fe80::/10 link-local
                    || ((bytes[0] & 0xfe) == 0xfc);                               // fc00::/7  unique-local
            }

            return false;
        }
    }

    public class JsRequest : FenObject
    {
            public string Url { get; private set; }
            public string Method { get; private set; } = "GET";
            public JsHeaders Headers { get; private set; } = new JsHeaders();
            public string Body { get; private set; }

            public JsRequest(string url, FenValue options = default)
            {
                Url = url;
                if (!options.IsNull && options.IsObject)
                {
                    var opts = options.AsObject();
                    // SECURITY: Whitelist method to prevent HTTP method/CRLF injection
                    if (opts.Has("method")) Method = FetchApi.ValidateMethod(opts.Get("method").ToString());
                    if (opts.Has("body")) Body = opts.Get("body").ToString();
                    if (opts.Has("headers"))
                    {
                        var hObj = opts.Get("headers");
                        if (hObj.IsObject && hObj.AsObject() is JsHeaders jsh) Headers = jsh;
                        else if (hObj.IsObject)
                        {
                            var dict = hObj.AsObject();
                            foreach (var k in dict.Keys()) Headers.SetHeader(k, dict.Get(k).ToString());
                        }
                    }
                }
                
                Set("url", FenValue.FromString(Url));
                Set("method", FenValue.FromString(Method));
                Set("headers", FenValue.FromObject(Headers));
            }

            public static FenValue Constructor(FenValue[] args, FenValue thisVal)
            {
                if (args.Length < 1) return FenValue.Undefined;
                string url = args[0].ToString();
                FenValue init = args.Length > 1 ? args[1] : FenValue.Undefined;
                return FenValue.FromObject(new JsRequest(url, init));
            }
        }

    public class JsHeaders : FenObject
    {
        private readonly Dictionary<string, string> _headers = new Dictionary<string, string>();

        public JsHeaders()
        {
            Set("append", FenValue.FromFunction(new FenFunction("append", Append)));
            Set("get", FenValue.FromFunction(new FenFunction("get", Get)));
            Set("has", FenValue.FromFunction(new FenFunction("has", Has)));
            Set("set", FenValue.FromFunction(new FenFunction("set", SetHeaderBinding)));
        }

        public static FenValue Constructor(FenValue[] args, FenValue thisVal)
        {
              return FenValue.FromObject(new JsHeaders());
        }

        private FenValue Append(FenValue[] args, FenValue thisVal)
        {
             if (args.Length < 2) return FenValue.Undefined;
             // Route through SetHeader so CRLF sanitization + forbidden-header checks apply
             var key = FetchApi.SanitizeHeaderValue(args[0].ToString())?.Trim().ToLowerInvariant() ?? "";
             var val = FetchApi.SanitizeHeaderValue(args[1].ToString());
             if (string.IsNullOrEmpty(key)) return FenValue.Undefined;
             if (FetchApi._forbiddenRequestHeaders.Contains(key) ||
                 key.StartsWith("proxy-", StringComparison.Ordinal) ||
                 key.StartsWith("sec-", StringComparison.Ordinal))
                 return FenValue.Undefined;

             if (_headers.ContainsKey(key))
                 _headers[key] += ", " + val;
             else
                 _headers[key] = val;

             return FenValue.Undefined;
        }

        private FenValue Get(FenValue[] args, FenValue thisVal)
        {
             if (args.Length < 1) return FenValue.Null;
             var key = args[0].ToString().ToLowerInvariant();
             return _headers.ContainsKey(key) ? FenValue.FromString(_headers[key]) : FenValue.Null;
        }

        private FenValue Has(FenValue[] args, FenValue thisVal)
        {
             if (args.Length < 1) return FenValue.FromBoolean(false);
             var key = args[0].ToString().ToLowerInvariant();
             return FenValue.FromBoolean(_headers.ContainsKey(key));
        }

        public string GetHeader(string key)
        {
            if (_headers.ContainsKey(key)) return _headers[key];
            return null;
        }

        public IEnumerable<KeyValuePair<string, string>> GetHeaders()
        {
            return _headers;
        }

        public void SetHeader(string key, string value)
        {
            // SECURITY: Sanitize to prevent CRLF injection; reject WHATWG forbidden headers
            var normalKey = FetchApi.SanitizeHeaderValue(key)?.Trim().ToLowerInvariant() ?? "";
            if (string.IsNullOrEmpty(normalKey)) return;
            if (FetchApi._forbiddenRequestHeaders.Contains(normalKey) ||
                normalKey.StartsWith("proxy-", StringComparison.Ordinal) ||
                normalKey.StartsWith("sec-", StringComparison.Ordinal))
                return; // Silently ignore forbidden headers per Fetch spec
            _headers[normalKey] = FetchApi.SanitizeHeaderValue(value);
        }

        private FenValue SetHeaderBinding(FenValue[] args, FenValue thisVal)
        {
             if (args.Length < 2) return FenValue.Undefined;
             SetHeader(args[0].ToString(), args[1].ToString());
             return FenValue.Undefined;
        }
    }

    public class JsResponse : FenObject
    {
        private readonly HttpResponseMessage _response;
        private readonly IExecutionContext _context;

        public JsResponse(HttpResponseMessage response, IExecutionContext context = null)
        {
            _response = response;
            _context = context;
            
            Set("ok", FenValue.FromBoolean(response.IsSuccessStatusCode));
            Set("status", FenValue.FromNumber((int)response.StatusCode));
            Set("statusText", FenValue.FromString(response.ReasonPhrase));
            
            var headers = new JsHeaders();
            if (response.Headers != null)
            {
                foreach (var h in response.Headers)
                    headers.SetHeader(h.Key, string.Join(", ", h.Value));
            }
            if (response.Content?.Headers != null)
            {
                foreach (var h in response.Content.Headers)
                    headers.SetHeader(h.Key, string.Join(", ", h.Value));
            }
            Set("headers", FenValue.FromObject(headers)); 
            
            Set("text", FenValue.FromFunction(new FenFunction("text", Text)));
            Set("json", FenValue.FromFunction(new FenFunction("json", Json)));
        }

        public static FenValue Constructor(FenValue[] args, FenValue thisVal)
        {
            return FenValue.FromObject(new JsResponse(new HttpResponseMessage()));
        }

        private FenValue Text(FenValue[] args, FenValue thisVal)
        {
            var executor = new FenFunction("executor", (executorArgs, executorThis) =>
            {
                var resolve = executorArgs[0].AsFunction();
                var reject = executorArgs[1].AsFunction();

                Task.Run(async () =>
                {
                    try 
                    {
                        var content = await _response.Content.ReadAsStringAsync();
                        resolve.Invoke(new[] { FenValue.FromString(content) }, _context);
                    }
                    catch (Exception ex)
                    {
                        reject.Invoke(new[] { FenValue.FromString(ex.Message) }, _context);
                    }
                });
                return FenValue.Undefined;
            });

            return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(executor), _context));
        }

        private FenValue Json(FenValue[] args, FenValue thisVal)
        {
            var executor = new FenFunction("executor", (executorArgs, executorThis) =>
            {
                var resolve = executorArgs[0].AsFunction();
                var reject = executorArgs[1].AsFunction();

                Task.Run(async () =>
                {
                    try 
                    {
                        var content = await _response.Content.ReadAsStringAsync();
                        using (var doc = System.Text.Json.JsonDocument.Parse(content))
                        {
                            var result = ConvertJsonElement(doc.RootElement);
                            resolve.Invoke(new[] { result }, _context);
                        }
                    }
                    catch (Exception ex)
                    {
                        reject.Invoke(new[] { FenValue.FromString(ex.Message) }, _context);
                    }
                });
                return FenValue.Undefined;
            });

            return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(executor), _context));
        }

        private FenValue ConvertJsonElement(System.Text.Json.JsonElement element)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    var obj = new FenObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        obj.Set(prop.Name, ConvertJsonElement(prop.Value));
                    }
                    return FenValue.FromObject(obj);
                case System.Text.Json.JsonValueKind.Array:
                    var arr = new FenObject();
                    int index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        arr.Set(index.ToString(), ConvertJsonElement(item));
                        index++;
                    }
                    arr.Set("length", FenValue.FromNumber(index));
                    return FenValue.FromObject(arr);
                case System.Text.Json.JsonValueKind.String:
                    return FenValue.FromString(element.GetString());
                case System.Text.Json.JsonValueKind.Number:
                    return FenValue.FromNumber(element.GetDouble());
                case System.Text.Json.JsonValueKind.True:
                    return FenValue.FromBoolean(true);
                case System.Text.Json.JsonValueKind.False:
                    return FenValue.FromBoolean(false);
                case System.Text.Json.JsonValueKind.Null:
                    return FenValue.Null;
                default:
                    return FenValue.Undefined;
            }
        }
    }
}
