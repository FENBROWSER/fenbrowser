using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;
using FenBrowser.Core.Network.Handlers;
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
            BinaryDataApi.Register(context);

            // Register constructors
            var requestCtor = new FenFunction("Request", (args, thisVal) => JsRequest.Constructor(args, thisVal, context));
            global.Set("Request", FenValue.FromFunction(requestCtor));
            global.Set("Headers", FenValue.FromFunction(new FenFunction("Headers", JsHeaders.Constructor)));
            var responseCtor = new FenFunction("Response", (args, thisVal) => JsResponse.Constructor(args, thisVal, context));
            responseCtor.Set("redirect", FenValue.FromFunction(new FenFunction("redirect", (args, thisVal) =>
                JsResponse.Redirect(args, context))));
            global.Set("Response", FenValue.FromFunction(responseCtor));
        }


        internal static Task RunDetachedAsync(Func<Task> operation)
        {
            return Task.Factory.StartNew(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FenLogger.Warn($"[FetchApi] Detached async operation failed: {ex.Message}", LogCategory.JavaScript);
                }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        private static FenValue Fetch(FenValue[] args, FenValue thisVal, IExecutionContext context, Func<HttpRequestMessage, Task<HttpResponseMessage>> fetchHandler)
        {
            if (args.Length < 1) return FenValue.Undefined;

            var input = args[0];
            var init = args.Length > 1 ? args[1] : FenValue.Undefined;
            
            JsRequest request;
            if (input is FenValue fv && fv.IsObject && fv.AsObject() is JsRequest jsReq)
            {
                request = (init.IsUndefined || init.IsNull) ? jsReq : new JsRequest(jsReq, init, context);
            }
            else
            {
                request = new JsRequest(input.ToString(), init, context);
            }
            
            

             // Return a Promise
             return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(new FenFunction("executor", (execArgs, execThis) => 
             {
                 var resolve = execArgs[0].AsFunction();
                 var reject = execArgs[1].AsFunction();
                 var signal = request.Signal;
                 int settled = 0;

                 void RejectAbort(FenValue reason)
                 {
                     if (Interlocked.Exchange(ref settled, 1) != 0)
                     {
                         return;
                     }

                     var message = reason.IsUndefined || reason.IsNull ? "AbortError" : reason.ToString();
                     reject.Invoke(new[] { FenValue.FromString(message) }, context);
                 }

                 if (signal.IsObject)
                 {
                     var signalObject = signal.AsObject();
                     if (signalObject.Get("aborted", context).ToBoolean())
                     {
                         RejectAbort(signalObject.Get("reason", context));
                         return FenValue.Undefined;
                     }

                     var addEventListener = signalObject.Get("addEventListener", context);
                     if (addEventListener.IsFunction)
                     {
                         var abortListener = new FenFunction("fetchAbortListener", (listenerArgs, listenerThis) =>
                         {
                             RejectAbort(signalObject.Get("reason", context));
                             return FenValue.Undefined;
                         });

                         addEventListener.AsFunction().Invoke(new[]
                         {
                             FenValue.FromString("abort"),
                             FenValue.FromFunction(abortListener)
                         }, context, signal);
                     }
                 }

                 _ = FetchApi.RunDetachedAsync(async () => 
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
                                    if (Interlocked.Exchange(ref settled, 1) == 0)
                                    {
                                        resolve.Invoke(new FenValue[] { FenValue.FromObject(swResponse) }, context);
                                    }
                                    return;
                                }

                                if (swSettlement.IsRejected)
                                {
                                    var rejection = swSettlement.Value.IsUndefined ? "respondWith() rejected" : swSettlement.Value.ToString();
                                    throw new InvalidOperationException($"ServiceWorker respondWith rejection: {rejection}");
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

                        if (BinaryDataApi.TryResolveBlobUrl(request.Url, out var blobResponse))
                        {
                            if (signal.IsObject && signal.AsObject().Get("aborted", context).ToBoolean())
                            {
                                RejectAbort(signal.AsObject().Get("reason", context));
                                return;
                            }

                            var blobJsResponse = new JsResponse(blobResponse, context);
                            if (Interlocked.Exchange(ref settled, 1) == 0)
                            {
                                resolve.Invoke(new FenValue[] { FenValue.FromObject(blobJsResponse) }, context);
                            }
                            return;
                        }

                        // 2. Network Fetch via Handler
                        if (fetchHandler  == null) throw new InvalidOperationException("Fetch handler missing");

                        // SECURITY: Validate URL (scheme + private-IP block) and method
                        ValidateFetchUrl(request.Url);

                        var req = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
                        if (request.Body != null)
                        {
                            req.Content = new StringContent(request.Body);
                            var ct = request.Headers.GetHeader("content-type");
                            if (!string.IsNullOrEmpty(ct)) 
                            {
                                try
                                {
                                    req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ct);
                                }
                                catch (Exception ex)
                                {
                                    FenLogger.Warn($"[Fetch] Invalid content-type header '{ct}': {ex.Message}", LogCategory.JavaScript);
                                }
                            }
                        }

                        foreach(var kv in request.Headers.GetHeaders())
                        {
                            if (kv.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)) continue;
                            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                        }

                        ApplyExecutionOriginHeader(req, context?.CurrentUrl);

                        var resp = await fetchHandler(req);
                        DiagnosticPaths.AppendRootText("js_debug.log", $"[Fetch] Response: {resp.StatusCode} for {request.Url}\n");
                        if (signal.IsObject && signal.AsObject().Get("aborted", context).ToBoolean())
                        {
                            RejectAbort(signal.AsObject().Get("reason", context));
                            return;
                        }
                        
                        var jsResp = new JsResponse(resp, context);
                        if (Interlocked.Exchange(ref settled, 1) == 0)
                        {
                            resolve.Invoke(new FenValue[] { FenValue.FromObject(jsResp) }, context);
                        }
                     }
                     catch (Exception ex)
                     {
                         DiagnosticPaths.AppendRootText("js_debug.log", $"[Fetch] Error: {ex.Message} for {request.Url}\n");
                         if (Interlocked.Exchange(ref settled, 1) == 0)
                         {
                             reject.Invoke(new FenValue[] { FenValue.FromString(ex.Message) }, context);
                         }
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

        internal static void TryCopyHeaders(IObject headerObject, HttpResponseMessage response)
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

        // SECURITY: Only http/https permitted â€” blocks file://, data:, ftp:, javascript:, etc.
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

        // SECURITY: Whitelist valid HTTP methods â€” prevents CRLF/request-smuggling injection.
        private static readonly HashSet<string> _allowedHttpMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "GET", "POST", "PUT", "DELETE", "HEAD", "OPTIONS", "PATCH" };

        internal static string ValidateMethod(string method)
        {
            var normalizedInput = (method ?? "GET").Trim();
            var upper = normalizedInput.ToUpperInvariant();
            if (!_allowedHttpMethods.Contains(upper))
                throw new InvalidOperationException($"Failed to fetch: '{method}' is not an allowed HTTP method.");

            return upper switch
            {
                "GET" => "GET",
                "HEAD" => "HEAD",
                "POST" => "POST",
                "PUT" => "PUT",
                "DELETE" => "DELETE",
                "OPTIONS" => "OPTIONS",
                _ => normalizedInput
            };
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

        internal static Uri TryGetExecutionBaseUri(IExecutionContext context)
        {
            if (context != null && Uri.TryCreate(context.CurrentUrl, UriKind.Absolute, out var current))
            {
                return current;
            }

            FenValue locationValue = FenValue.Undefined;
            if (context?.Environment != null)
            {
                locationValue = context.Environment.Get("location");
            }

            if ((locationValue.IsObject || locationValue.IsFunction) && locationValue.AsObject() != null)
            {
                var href = locationValue.AsObject().Get("href", context);
                if (href.IsString && Uri.TryCreate(href.ToString(), UriKind.Absolute, out var locationUri))
                {
                    return locationUri;
                }
            }

            return null;
        }

        internal static string ResolveUrl(string url, IExecutionContext context)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return TryGetExecutionBaseUri(context)?.AbsoluteUri ?? string.Empty;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            {
                return absolute.AbsoluteUri;
            }

            var baseUri = TryGetExecutionBaseUri(context);
            if (baseUri != null && Uri.TryCreate(baseUri, url, out var resolved))
            {
                return resolved.AbsoluteUri;
            }

            return url;
        }

        private static void ApplyExecutionOriginHeader(HttpRequestMessage request, string currentUrl)
        {
            if (request?.RequestUri == null || string.IsNullOrWhiteSpace(currentUrl))
            {
                return;
            }

            if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var executionUri))
            {
                return;
            }

            var originUri = new UriBuilder(executionUri.Scheme, executionUri.Host, executionUri.IsDefaultPort ? -1 : executionUri.Port).Uri;
            if (CorsHandler.IsSameOrigin(request.RequestUri, originUri))
            {
                return;
            }

            var originHeader = CorsHandler.SerializeOrigin(originUri);
            if (!string.IsNullOrWhiteSpace(originHeader))
            {
                request.Headers.TryAddWithoutValidation("Origin", originHeader);
            }
        }

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
            public FenValue Signal { get; private set; } = FenValue.Undefined;
            public string Referrer { get; private set; } = "about:client";
            public string ReferrerPolicy { get; private set; } = string.Empty;
            public string Mode { get; private set; } = "cors";
            public string Credentials { get; private set; } = "same-origin";
            public string Cache { get; private set; } = "default";
            public string Redirect { get; private set; } = "follow";
            public string Integrity { get; private set; } = string.Empty;
            public FenValue Window { get; private set; } = FenValue.Undefined;
            private bool _bodyUsed;

            public JsRequest(string url, FenValue options = default, IExecutionContext context = null)
            {
                Url = FetchApi.ResolveUrl(url, context);
                ApplyOptions(options, context);
                PublishState();
            }

            public JsRequest(JsRequest source, FenValue options = default, IExecutionContext context = null)
            {
                Url = source?.Url ?? string.Empty;
                Method = source?.Method ?? "GET";
                Headers = source != null ? new JsHeaders(FenValue.FromObject(source.Headers)) : new JsHeaders();
                Body = source?.Body;
                Signal = source?.Signal ?? FenValue.Undefined;
                Referrer = source?.Referrer ?? "about:client";
                ReferrerPolicy = source?.ReferrerPolicy ?? string.Empty;
                Mode = source?.Mode ?? "cors";
                Credentials = source?.Credentials ?? "same-origin";
                Cache = source?.Cache ?? "default";
                Redirect = source?.Redirect ?? "follow";
                Integrity = source?.Integrity ?? string.Empty;
                Window = source?.Window ?? FenValue.Undefined;
                ApplyOptions(options, context);
                PublishState();
            }

            private void ApplyOptions(FenValue options, IExecutionContext context)
            {
                if (!options.IsNull && options.IsObject)
                {
                    var opts = options.AsObject();
                    // SECURITY: Whitelist method to prevent HTTP method/CRLF injection
                    if (opts.Has("method")) Method = FetchApi.ValidateMethod(opts.Get("method").ToString());
                    if (opts.Has("body")) Body = opts.Get("body").ToString();
                    if (opts.Has("signal")) Signal = opts.Get("signal");
                    if (opts.Has("referrer")) Referrer = NormalizeReferrer(opts.Get("referrer"), context);
                    if (opts.Has("referrerPolicy")) ReferrerPolicy = opts.Get("referrerPolicy").ToString();
                    if (opts.Has("mode")) Mode = opts.Get("mode").ToString();
                    if (opts.Has("credentials")) Credentials = opts.Get("credentials").ToString();
                    if (opts.Has("cache")) Cache = opts.Get("cache").ToString();
                    if (opts.Has("redirect")) Redirect = opts.Get("redirect").ToString();
                    if (opts.Has("integrity")) Integrity = opts.Get("integrity").ToString();
                    if (opts.Has("window"))
                    {
                        var windowValue = opts.Get("window");
                        Window = windowValue.IsNull ? FenValue.Undefined : windowValue;
                    }
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
            }

            private void PublishState()
            {
                Set("url", FenValue.FromString(Url));
                Set("method", FenValue.FromString(Method));
                Set("headers", FenValue.FromObject(Headers));
                Set("signal", Signal.IsUndefined ? FenValue.Null : Signal);
                Set("referrer", FenValue.FromString(Referrer ?? string.Empty));
                Set("referrerPolicy", FenValue.FromString(ReferrerPolicy ?? string.Empty));
                Set("mode", FenValue.FromString(Mode ?? string.Empty));
                Set("credentials", FenValue.FromString(Credentials ?? string.Empty));
                Set("cache", FenValue.FromString(Cache ?? string.Empty));
                Set("redirect", FenValue.FromString(Redirect ?? string.Empty));
                Set("integrity", FenValue.FromString(Integrity ?? string.Empty));
                Set("window", Window.IsUndefined ? FenValue.Undefined : Window);
                Set("bodyUsed", FenValue.FromBoolean(_bodyUsed));
                Set("clone", FenValue.FromFunction(new FenFunction("clone", Clone)));
                Set("text", FenValue.FromFunction(new FenFunction("text", Text)));
                Set("json", FenValue.FromFunction(new FenFunction("json", Json)));
                Set("arrayBuffer", FenValue.FromFunction(new FenFunction("arrayBuffer", ArrayBuffer)));
            }

            public static FenValue Constructor(FenValue[] args, FenValue thisVal, IExecutionContext context = null)
            {
                if (args.Length < 1) return FenValue.Undefined;
                FenValue init = args.Length > 1 ? args[1] : FenValue.Undefined;
                if (args[0].IsObject && args[0].AsObject() is JsRequest sourceRequest)
                {
                    return FenValue.FromObject(new JsRequest(sourceRequest, init, context));
                }

                string url = args[0].ToString();
                return FenValue.FromObject(new JsRequest(url, init, context));
            }

            private static string NormalizeReferrer(FenValue referrerValue, IExecutionContext context)
            {
                var raw = referrerValue.ToString();
                if (string.IsNullOrEmpty(raw))
                {
                    return string.Empty;
                }

                if (string.Equals(raw, "about:client", StringComparison.OrdinalIgnoreCase))
                {
                    return "about:client";
                }

                var baseUri = FetchApi.TryGetExecutionBaseUri(context);
                if (Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
                {
                    if (baseUri != null && !CorsHandler.IsSameOrigin(absolute, baseUri))
                    {
                        return "about:client";
                    }

                    return absolute.AbsoluteUri;
                }

                if (baseUri != null && Uri.TryCreate(baseUri, raw, out var resolved))
                {
                    return resolved.AbsoluteUri;
                }

                return "about:client";
            }

            private FenValue Clone(FenValue[] args, FenValue thisVal)
            {
                if (_bodyUsed)
                {
                    return FenValue.FromError("TypeError: Request body is already used.");
                }

                return FenValue.FromObject(new JsRequest(this));
            }

            private FenValue Text(FenValue[] args, FenValue thisVal)
            {
                return CreateBodyPromise(() => FenValue.FromString(Body ?? string.Empty));
            }

            private FenValue Json(FenValue[] args, FenValue thisVal)
            {
                return CreateBodyPromise(() =>
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(Body ?? string.Empty))
                    {
                        return ConvertJsonElement(doc.RootElement);
                    }
                });
            }

            private FenValue ArrayBuffer(FenValue[] args, FenValue thisVal)
            {
                return CreateBodyPromise(() =>
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(Body ?? string.Empty);
                    var buffer = new FenBrowser.FenEngine.Core.Types.JsArrayBuffer(bytes.Length);
                    Array.Copy(bytes, buffer.Data, bytes.Length);
                    return FenValue.FromObject(buffer);
                });
            }

            private FenValue CreateBodyPromise(Func<FenValue> factory)
            {
                var executor = new FenFunction("executor", (executorArgs, executorThis) =>
                {
                    var resolve = executorArgs[0].AsFunction();
                    var reject = executorArgs[1].AsFunction();

                    _ = FetchApi.RunDetachedAsync(() =>
                    {
                        try
                        {
                            if (_bodyUsed)
                            {
                                throw new InvalidOperationException("TypeError: Request body is already used.");
                            }

                            _bodyUsed = true;
                            Set("bodyUsed", FenValue.FromBoolean(true));
                            resolve.Invoke(new[] { factory() }, null);
                        }
                        catch (Exception ex)
                        {
                            reject.Invoke(new[] { FenValue.FromString(ex.Message) }, null);
                        }

                        return Task.CompletedTask;
                    });

                    return FenValue.Undefined;
                });

                return FenValue.FromObject(new FenBrowser.FenEngine.Core.Types.JsPromise(FenValue.FromFunction(executor), null));
            }

            private static FenValue ConvertJsonElement(System.Text.Json.JsonElement element)
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

    public class JsHeaders : FenObject
    {
        private readonly Dictionary<string, string> _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public JsHeaders(FenValue init = default)
        {
            Set("append", FenValue.FromFunction(new FenFunction("append", Append)));
            Set("get", FenValue.FromFunction(new FenFunction("get", Get)));
            Set("has", FenValue.FromFunction(new FenFunction("has", Has)));
            Set("set", FenValue.FromFunction(new FenFunction("set", SetHeaderBinding)));
            Set("delete", FenValue.FromFunction(new FenFunction("delete", Delete)));
            Set("forEach", FenValue.FromFunction(new FenFunction("forEach", ForEach)));

            if (!init.IsUndefined && !init.IsNull)
            {
                ApplyInit(init);
            }
        }

        public static FenValue Constructor(FenValue[] args, FenValue thisVal)
        {
              var init = args.Length > 0 ? args[0] : FenValue.Undefined;
              return FenValue.FromObject(new JsHeaders(init));
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

        private FenValue Delete(FenValue[] args, FenValue thisVal)
        {
             if (args.Length < 1) return FenValue.Undefined;
             var key = args[0].ToString().ToLowerInvariant();
             _headers.Remove(key);
             return FenValue.Undefined;
        }

        private FenValue ForEach(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1 || !args[0].IsFunction)
            {
                return FenValue.Undefined;
            }

            var callback = args[0].AsFunction();
            var thisArg = args.Length > 1 ? args[1] : FenValue.Undefined;
            foreach (var header in _headers)
            {
                callback.Invoke(new[]
                {
                    FenValue.FromString(header.Value),
                    FenValue.FromString(header.Key),
                    FenValue.FromObject(this)
                }, null, thisArg);
            }

            return FenValue.Undefined;
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

        private void ApplyInit(FenValue init)
        {
            if (!init.IsObject)
            {
                return;
            }

            if (init.AsObject() is JsHeaders existingHeaders)
            {
                foreach (var header in existingHeaders.GetHeaders())
                {
                    SetHeader(header.Key, header.Value);
                }
                return;
            }

            var initObject = init.AsObject();
            var lengthValue = initObject.Get("length");
            if (lengthValue.IsNumber)
            {
                var length = Math.Max(0, (int)lengthValue.ToNumber());
                for (var i = 0; i < length; i++)
                {
                    var pair = initObject.Get(i.ToString());
                    if (!pair.IsObject)
                    {
                        continue;
                    }

                    var pairObject = pair.AsObject();
                    var pairLength = pairObject.Get("length");
                    if (!pairLength.IsNumber || pairLength.ToNumber() < 2)
                    {
                        continue;
                    }

                    var key = pairObject.Get("0").ToString();
                    var value = pairObject.Get("1").ToString();
                    SetHeader(key, value);
                }
                return;
            }

            foreach (var key in initObject.Keys())
            {
                SetHeader(key, initObject.Get(key).ToString());
            }
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
        internal HttpResponseMessage ResponseMessage => _response;
        private bool _bodyUsed;

        public JsResponse(HttpResponseMessage response, IExecutionContext context = null)
        {
            _response = response;
            _context = context;
            
            Set("ok", FenValue.FromBoolean(response.IsSuccessStatusCode));
            Set("status", FenValue.FromNumber((int)response.StatusCode));
            Set("statusText", FenValue.FromString(response.ReasonPhrase));
            Set("url", FenValue.FromString(response.RequestMessage?.RequestUri?.ToString() ?? string.Empty));
            Set("bodyUsed", FenValue.FromBoolean(false));
            
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
            Set("arrayBuffer", FenValue.FromFunction(new FenFunction("arrayBuffer", ArrayBuffer)));
            Set("blob", FenValue.FromFunction(new FenFunction("blob", Blob)));
            Set("formData", FenValue.FromFunction(new FenFunction("formData", FormData)));
            Set("clone", FenValue.FromFunction(new FenFunction("clone", Clone)));
        }

        public static FenValue Constructor(FenValue[] args, FenValue thisVal, IExecutionContext context = null)
        {
            var bodyValue = args.Length > 0 ? args[0] : FenValue.Undefined;
            var init = args.Length > 1 && args[1].IsObject ? args[1].AsObject() : null;
            var response = new HttpResponseMessage();

            if (init != null)
            {
                if (init.Has("status") && init.Get("status").IsNumber)
                {
                    var status = (int)init.Get("status").ToNumber();
                    if (status >= 200 && status <= 599)
                    {
                        response.StatusCode = (System.Net.HttpStatusCode)status;
                    }
                }

                if (init.Has("statusText"))
                {
                    response.ReasonPhrase = init.Get("statusText").ToString();
                }
            }

            if (BinaryDataApi.TryCreateHttpContent(bodyValue, out var content))
            {
                response.Content = content;
            }

            if (init != null && init.Has("headers"))
            {
                var headersValue = init.Get("headers");
                if (headersValue.IsObject)
                {
                    FetchApi.TryCopyHeaders(headersValue.AsObject(), response);
                }
            }

            return FenValue.FromObject(new JsResponse(response, context));
        }

        public static FenValue Redirect(FenValue[] args, IExecutionContext context = null)
        {
            if (args.Length == 0)
            {
                throw new InvalidOperationException("Response.redirect requires a URL.");
            }

            var targetUrl = FetchApi.ResolveUrl(args[0].ToString(), context);
            var status = args.Length > 1 && args[1].IsNumber ? (int)args[1].ToNumber() : 302;
            if (status != 301 && status != 302 && status != 303 && status != 307 && status != 308)
            {
                status = 302;
            }

            var response = new HttpResponseMessage((System.Net.HttpStatusCode)status)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, targetUrl)
            };
            response.Headers.Location = new Uri(targetUrl, UriKind.Absolute);
            return FenValue.FromObject(new JsResponse(response, context));
        }

        private FenValue Clone(FenValue[] args, FenValue thisVal)
        {
            if (_bodyUsed)
            {
                return FenValue.FromError("TypeError: Response body is already used.");
            }

            var clone = new HttpResponseMessage(_response.StatusCode)
            {
                ReasonPhrase = _response.ReasonPhrase,
                Version = _response.Version
            };

            foreach (var header in _response.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (_response.Content != null)
            {
                var body = _response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                clone.Content = new StringContent(body);
                foreach (var header in _response.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return FenValue.FromObject(new JsResponse(clone, _context));
        }

        private FenValue Text(FenValue[] args, FenValue thisVal)
        {
            return CreateBodyPromise(async () =>
            {
                var content = _response.Content == null
                    ? string.Empty
                    : await _response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return FenValue.FromString(content);
            });
        }

        private FenValue Json(FenValue[] args, FenValue thisVal)
        {
            return CreateBodyPromise(async () =>
            {
                var content = _response.Content == null
                    ? string.Empty
                    : await _response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using (var doc = System.Text.Json.JsonDocument.Parse(content))
                {
                    return ConvertJsonElement(doc.RootElement);
                }
            });
        }

        private FenValue ArrayBuffer(FenValue[] args, FenValue thisVal)
        {
            return CreateBodyPromise(async () =>
            {
                var bytes = _response.Content == null
                    ? Array.Empty<byte>()
                    : await _response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var buffer = new FenBrowser.FenEngine.Core.Types.JsArrayBuffer(bytes.Length);
                Array.Copy(bytes, buffer.Data, bytes.Length);
                return FenValue.FromObject(buffer);
            });
        }

        private FenValue Blob(FenValue[] args, FenValue thisVal)
        {
            return CreateBodyPromise(async () =>
            {
                var bytes = _response.Content == null
                    ? Array.Empty<byte>()
                    : await _response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var contentType = _response.Content?.Headers?.ContentType?.ToString() ?? string.Empty;
                return FenValue.FromObject(BinaryDataApi.CreateBlob(bytes, contentType));
            });
        }

        private FenValue FormData(FenValue[] args, FenValue thisVal)
        {
            return CreateBodyPromise(async () =>
            {
                var bytes = _response.Content == null
                    ? Array.Empty<byte>()
                    : await _response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                var contentType = _response.Content?.Headers?.ContentType?.ToString() ?? string.Empty;
                return FenValue.FromObject(BinaryDataApi.ParseFormData(bytes, contentType));
            });
        }

        private FenValue CreateBodyPromise(Func<Task<FenValue>> factory)
        {
            var executor = new FenFunction("executor", (executorArgs, executorThis) =>
            {
                var resolve = executorArgs[0].AsFunction();
                var reject = executorArgs[1].AsFunction();

                _ = FetchApi.RunDetachedAsync(async () =>
                {
                    try
                    {
                        if (_bodyUsed)
                        {
                            throw new InvalidOperationException("TypeError: Response body is already used.");
                        }

                        _bodyUsed = true;
                        Set("bodyUsed", FenValue.FromBoolean(true));
                        var result = await factory().ConfigureAwait(false);
                        resolve.Invoke(new[] { result }, _context);
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



