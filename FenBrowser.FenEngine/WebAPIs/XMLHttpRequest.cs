using System;
using System.Net.Http;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.WebAPIs
{
    public class XMLHttpRequest : FenObject
    {
        private const int UNSENT = 0;
        private const int OPENED = 1;
        private const int HEADERS_RECEIVED = 2;
        private const int LOADING = 3;
        private const int DONE = 4;

        private int _readyState = UNSENT;
        private string _method;
        private string _url;
        private bool _async = true;
        private readonly Dictionary<string, string> _requestHeaders = new Dictionary<string, string>();
        private HttpResponseMessage _response;
        private readonly IExecutionContext _context;
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _networkFetch;
        
        public XMLHttpRequest(
            IExecutionContext context,
            Func<HttpRequestMessage, Task<HttpResponseMessage>> networkFetch = null)
        {
            _context = context;
            _networkFetch = networkFetch;
            Set("UNSENT", FenValue.FromNumber(UNSENT));
            Set("OPENED", FenValue.FromNumber(OPENED));
            Set("HEADERS_RECEIVED", FenValue.FromNumber(HEADERS_RECEIVED));
            Set("LOADING", FenValue.FromNumber(LOADING));
            Set("DONE", FenValue.FromNumber(DONE));
            
            Set("readyState", FenValue.FromNumber(_readyState));
            Set("status", FenValue.FromNumber(0));
            Set("statusText", FenValue.FromString(""));
            Set("responseText", FenValue.FromString(""));
            Set("responseURL", FenValue.FromString(""));
            
            // Methods
            Set("open", FenValue.FromFunction(new FenFunction("open", Open)));
            Set("send", FenValue.FromFunction(new FenFunction("send", Send)));
            Set("setRequestHeader", FenValue.FromFunction(new FenFunction("setRequestHeader", SetRequestHeader)));
            Set("getAllResponseHeaders", FenValue.FromFunction(new FenFunction("getAllResponseHeaders", GetAllResponseHeaders)));
            Set("getResponseHeader", FenValue.FromFunction(new FenFunction("getResponseHeader", GetResponseHeader)));
        }

        private void SetReadyState(int state)
        {
            _readyState = state;
            Set("readyState", FenValue.FromNumber(_readyState));
            
            // Dispatch onreadystatechange
            if (Get("onreadystatechange") is FenValue fn && fn.IsFunction)
            {
                 _context.ScheduleCallback(() => {
                     try { fn.AsFunction().Invoke(Array.Empty<FenValue>(), _context); } catch (Exception ex) { FenLogger.Warn($"[XMLHttpRequest] onreadystatechange callback failed: {ex.Message}", LogCategory.JavaScript); }
                 }, 0);
            }
        }

        // SECURITY: Whitelist of allowed HTTP methods (prevents CRLF / request-smuggling)
        private static readonly HashSet<string> _allowedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "GET", "POST", "PUT", "DELETE", "HEAD", "OPTIONS", "PATCH" };

        // SECURITY: WHATWG forbidden request headers - must never be set by scripts
        private static readonly HashSet<string> _forbiddenHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "accept-charset", "accept-encoding", "access-control-request-headers",
            "access-control-request-method", "connection", "content-length",
            "cookie", "cookie2", "date", "dnt", "expect", "host", "keep-alive",
            "origin", "referer", "te", "trailer", "transfer-encoding", "upgrade", "via"
        };

        // SECURITY: Strip CR/LF to prevent header injection attacks
        private static string SanitizeHeaderValue(string value) =>
            value?.Replace("\r", "").Replace("\n", "") ?? "";

        private FenValue Open(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2) return FenValue.FromError("TypeError: Not enough arguments");

            // SECURITY: Validate method against whitelist
            var rawMethod = args[0].ToString().Trim().ToUpperInvariant();
            if (!_allowedMethods.Contains(rawMethod))
                return FenValue.FromError($"SecurityError: '{rawMethod}' is not an allowed HTTP method.");
            _method = rawMethod;

            // SECURITY: Validate URL scheme - only http/https permitted
            _url = args[1].ToString();
            if (!Uri.TryCreate(_url, UriKind.Absolute, out var parsedUri) ||
                (parsedUri.Scheme != "http" && parsedUri.Scheme != "https"))
                return FenValue.FromError($"SecurityError: URL scheme not allowed for '{_url}'.");

            _async = args.Length > 2 ? args[2].ToBoolean() : true;
            SetReadyState(OPENED);
            return FenValue.Undefined;
        }

        private FenValue SetRequestHeader(FenValue[] args, FenValue thisVal)
        {
            if (_readyState != OPENED) return FenValue.FromError("InvalidStateError");
            if (args.Length < 2) return FenValue.Undefined;

            var name  = SanitizeHeaderValue(args[0].ToString()).Trim();
            var value = SanitizeHeaderValue(args[1].ToString());

            // SECURITY: Block WHATWG forbidden headers and Proxy-/Sec- prefixed headers
            if (_forbiddenHeaders.Contains(name) ||
                name.StartsWith("proxy-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("sec-", StringComparison.OrdinalIgnoreCase))
                return FenValue.FromError($"NotAllowedError: '{name}' is a forbidden header name.");

            _requestHeaders[name] = value;
            return FenValue.Undefined;
        }

        private static Task RunDetachedAsync(Func<Task> operation)
        {
            return Task.Factory.StartNew(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FenLogger.Warn($"[XMLHttpRequest] Detached async operation failed: {ex.Message}", LogCategory.JavaScript);
                }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }
        private FenValue Send(FenValue[] args, FenValue thisVal)
        {
            if (_readyState != OPENED) return FenValue.FromError("InvalidStateError");
            
            string body = args.Length > 0 ? args[0].ToString() : null;
            
            // Execute request
            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    using (var req = new HttpRequestMessage(new HttpMethod(_method), _url))
                    {
                        foreach(var h in _requestHeaders)
                        {
                            req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                        }
                        
                        if (body != null && (_method == "POST" || _method == "PUT"))
                        {
                            req.Content = new StringContent(body); 
                            if (_requestHeaders.TryGetValue("Content-Type", out var ct))
                            {
                                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ct);
                            }
                        }

                        var fetch = _networkFetch;
                        if (fetch == null)
                            throw new InvalidOperationException("XMLHttpRequest network handler not configured");

                        // Send through centralized browser network pipeline.
                        _response = await fetch(req).ConfigureAwait(false);
                        
                        _context.ScheduleCallback(() => {
                            Set("status", FenValue.FromNumber((int)_response.StatusCode));
                            Set("statusText", FenValue.FromString(_response.ReasonPhrase));
                            Set("responseURL", FenValue.FromString(_response.RequestMessage.RequestUri.ToString()));
                            SetReadyState(HEADERS_RECEIVED);
                        }, 0);

                        var content = await _response.Content.ReadAsStringAsync();
                        
                        _context.ScheduleCallback(() => {
                             SetReadyState(LOADING); 
                             Set("responseText", FenValue.FromString(content));
                             SetReadyState(DONE);
                             
                             // onload
                             if (Get("onload") is FenValue fn && fn.IsFunction)
                                 fn.AsFunction().Invoke(Array.Empty<FenValue>(), _context);
                        }, 0);
                    }
                }
                catch (Exception ex)
                {
                     _context.ScheduleCallback(() => {
                         // Error handling
                         if (Get("onerror") is FenValue fn && fn.IsFunction)
                             fn.AsFunction().Invoke(Array.Empty<FenValue>(), _context);
                     }, 0);
                }
            });

            return FenValue.Undefined;
        }
        
        private FenValue GetAllResponseHeaders(FenValue[] args, FenValue thisVal)
        {
            if (_readyState < HEADERS_RECEIVED) return FenValue.FromString("");
            return FenValue.FromString(_response?.Headers.ToString() ?? "");
        }
        
        private FenValue GetResponseHeader(FenValue[] args, FenValue thisVal)
        {
             if (_readyState < HEADERS_RECEIVED || args.Length < 1) return FenValue.Null;
             var name = args[0].ToString();
             if (_response != null && _response.Headers.TryGetValues(name, out var vals))
             {
                 return FenValue.FromString(string.Join(", ", vals));
             }
             return FenValue.Null;
        }
    }
}


