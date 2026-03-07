using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network.Handlers;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.WebAPIs
{
    public class XMLHttpRequest : FenObject
    {
        private const int UNSENT = 0;
        private const int OPENED = 1;
        private const int HEADERS_RECEIVED = 2;
        private const int LOADING = 3;
        private const int DONE = 4;

        private static readonly HashSet<string> AllowedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GET", "POST", "PUT", "DELETE", "HEAD", "OPTIONS", "PATCH"
        };

        private static readonly HashSet<string> ForbiddenHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "accept-charset", "accept-encoding", "access-control-request-headers",
            "access-control-request-method", "connection", "content-length",
            "cookie", "cookie2", "date", "dnt", "expect", "host", "keep-alive",
            "origin", "referer", "te", "trailer", "transfer-encoding", "upgrade", "via"
        };

        private int _readyState = UNSENT;
        private string _method;
        private string _url;
        private bool _async = true;
        private bool _withCredentials;
        private bool _sendFlag;
        private bool _uploadComplete;
        private int _timeoutMilliseconds;
        private string _responseType = string.Empty;
        private string _overrideMimeType;
        private readonly Dictionary<string, string> _requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private HttpResponseMessage _response;
        private CancellationTokenSource _requestCts;
        private int _requestVersion;
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

            BaseStateSync();

            Set("open", FenValue.FromFunction(new FenFunction("open", Open)));
            Set("send", FenValue.FromFunction(new FenFunction("send", Send)));
            Set("abort", FenValue.FromFunction(new FenFunction("abort", Abort)));
            Set("setRequestHeader", FenValue.FromFunction(new FenFunction("setRequestHeader", SetRequestHeader)));
            Set("getAllResponseHeaders", FenValue.FromFunction(new FenFunction("getAllResponseHeaders", GetAllResponseHeaders)));
            Set("getResponseHeader", FenValue.FromFunction(new FenFunction("getResponseHeader", GetResponseHeader)));
            Set("overrideMimeType", FenValue.FromFunction(new FenFunction("overrideMimeType", OverrideMimeType)));
        }

        public override void Set(string key, FenValue value, IExecutionContext context = null)
        {
            switch (key)
            {
                case "timeout":
                    _timeoutMilliseconds = Math.Max(0, (int)value.ToNumber());
                    base.Set(key, FenValue.FromNumber(_timeoutMilliseconds), context);
                    return;
                case "responseType":
                    if (_readyState == LOADING || _readyState == DONE)
                    {
                        return;
                    }

                    _responseType = NormalizeResponseType(value.ToString());
                    base.Set(key, FenValue.FromString(_responseType), context);
                    return;
                case "withCredentials":
                    _withCredentials = value.ToBoolean();
                    base.Set(key, FenValue.FromBoolean(_withCredentials), context);
                    return;
            }

            base.Set(key, value, context);
        }

        private static string SanitizeHeaderValue(string value)
        {
            return value?.Replace("\r", string.Empty).Replace("\n", string.Empty) ?? string.Empty;
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
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        private void BaseStateSync()
        {
            base.Set("readyState", FenValue.FromNumber(_readyState));
            base.Set("status", FenValue.FromNumber(0));
            base.Set("statusText", FenValue.FromString(string.Empty));
            base.Set("responseText", FenValue.FromString(string.Empty));
            base.Set("responseURL", FenValue.FromString(string.Empty));
            base.Set("response", FenValue.Null);
            base.Set("responseType", FenValue.FromString(_responseType));
            base.Set("timeout", FenValue.FromNumber(_timeoutMilliseconds));
            base.Set("withCredentials", FenValue.FromBoolean(_withCredentials));
        }

        private void ResetForOpen()
        {
            CancelInFlightRequest();
            _requestHeaders.Clear();
            _response = null;
            _sendFlag = false;
            _uploadComplete = false;
            _overrideMimeType = null;
            base.Set("status", FenValue.FromNumber(0));
            base.Set("statusText", FenValue.FromString(string.Empty));
            base.Set("responseText", FenValue.FromString(string.Empty));
            base.Set("responseURL", FenValue.FromString(string.Empty));
            base.Set("response", FenValue.Null);
        }

        private void CancelInFlightRequest()
        {
            Interlocked.Increment(ref _requestVersion);
            var cts = _requestCts;
            _requestCts = null;

            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                cts.Dispose();
            }
        }

        private void SetReadyState(int state, bool scheduleCallbacks)
        {
            _readyState = state;
            base.Set("readyState", FenValue.FromNumber(_readyState));
            DispatchHandler("onreadystatechange", scheduleCallbacks);
        }

        private void DispatchHandler(string propertyName, bool scheduleCallback)
        {
            var handler = Get(propertyName);
            if (!handler.IsFunction)
            {
                return;
            }

            if (scheduleCallback)
            {
                _context.ScheduleCallback(() =>
                {
                    try
                    {
                        handler.AsFunction().Invoke(Array.Empty<FenValue>(), _context);
                    }
                    catch (Exception ex)
                    {
                        FenLogger.Warn($"[XMLHttpRequest] {propertyName} callback failed: {ex.Message}", LogCategory.JavaScript);
                    }
                }, 0);

                return;
            }

            try
            {
                handler.AsFunction().Invoke(Array.Empty<FenValue>(), _context);
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[XMLHttpRequest] {propertyName} callback failed: {ex.Message}", LogCategory.JavaScript);
            }
        }

        private void DispatchLifecycle(string propertyName, bool scheduleCallbacks)
        {
            DispatchHandler(propertyName, scheduleCallbacks);
        }

        private FenValue Open(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2)
            {
                return FenValue.FromError("TypeError: Not enough arguments");
            }

            var rawMethod = args[0].ToString().Trim().ToUpperInvariant();
            if (!AllowedMethods.Contains(rawMethod))
            {
                return FenValue.FromError($"SecurityError: '{rawMethod}' is not an allowed HTTP method.");
            }

            var rawUrl = args[1].ToString();
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsedUri) ||
                (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
            {
                return FenValue.FromError($"SecurityError: URL scheme not allowed for '{rawUrl}'.");
            }

            _method = rawMethod;
            _url = parsedUri.ToString();
            _async = args.Length <= 2 || args[2].ToBoolean();
            ResetForOpen();
            SetReadyState(OPENED, scheduleCallbacks: true);
            return FenValue.Undefined;
        }

        private FenValue SetRequestHeader(FenValue[] args, FenValue thisVal)
        {
            if (_readyState != OPENED || _sendFlag)
            {
                return FenValue.FromError("InvalidStateError");
            }

            if (args.Length < 2)
            {
                return FenValue.Undefined;
            }

            var name = SanitizeHeaderValue(args[0].ToString()).Trim();
            var value = SanitizeHeaderValue(args[1].ToString());

            if (ForbiddenHeaders.Contains(name) ||
                name.StartsWith("proxy-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("sec-", StringComparison.OrdinalIgnoreCase))
            {
                return FenValue.FromError($"NotAllowedError: '{name}' is a forbidden header name.");
            }

            _requestHeaders[name] = value;
            return FenValue.Undefined;
        }

        private FenValue OverrideMimeType(FenValue[] args, FenValue thisVal)
        {
            if (_readyState == LOADING || _readyState == DONE)
            {
                return FenValue.FromError("InvalidStateError");
            }

            _overrideMimeType = args.Length > 0 ? args[0].ToString() : null;
            return FenValue.Undefined;
        }

        private FenValue Abort(FenValue[] args, FenValue thisVal)
        {
            bool hadActiveRequest = _sendFlag || _readyState == HEADERS_RECEIVED || _readyState == LOADING;
            CancelInFlightRequest();
            _sendFlag = false;
            _response = null;
            base.Set("status", FenValue.FromNumber(0));
            base.Set("statusText", FenValue.FromString(string.Empty));
            base.Set("responseText", FenValue.FromString(string.Empty));
            base.Set("responseURL", FenValue.FromString(string.Empty));
            base.Set("response", FenValue.Null);

            if (_readyState != UNSENT && _readyState != DONE)
            {
                SetReadyState(DONE, scheduleCallbacks: true);
            }

            if (hadActiveRequest)
            {
                DispatchLifecycle("onabort", scheduleCallbacks: true);
                DispatchLifecycle("onloadend", scheduleCallbacks: true);
            }

            return FenValue.Undefined;
        }

        private FenValue Send(FenValue[] args, FenValue thisVal)
        {
            if (_readyState != OPENED || _sendFlag)
            {
                return FenValue.FromError("InvalidStateError");
            }

            string body = args.Length > 0 && !args[0].IsUndefined && !args[0].IsNull ? args[0].ToString() : null;
            _sendFlag = true;
            _uploadComplete = body == null;

            var requestId = Interlocked.Increment(ref _requestVersion);
            var requestCts = new CancellationTokenSource();
            if (_timeoutMilliseconds > 0)
            {
                requestCts.CancelAfter(_timeoutMilliseconds);
            }

            _requestCts = requestCts;
            DispatchLifecycle("onloadstart", scheduleCallbacks: _async);

            if (_async)
            {
                _ = RunDetachedAsync(() => ExecuteRequestAsync(body, requestId, requestCts.Token, scheduleCallbacks: true));
                return FenValue.Undefined;
            }

            ExecuteRequestAsync(body, requestId, requestCts.Token, scheduleCallbacks: false).GetAwaiter().GetResult();
            return FenValue.Undefined;
        }

        private async Task ExecuteRequestAsync(string body, int requestId, CancellationToken token, bool scheduleCallbacks)
        {
            try
            {
                using (var request = BuildRequest(body))
                {
                    var fetch = _networkFetch;
                    if (fetch == null)
                    {
                        throw new InvalidOperationException("XMLHttpRequest network handler not configured");
                    }

                    var fetchTask = fetch(request);
                    if (_timeoutMilliseconds > 0)
                    {
                        var completedTask = await Task.WhenAny(fetchTask, Task.Delay(_timeoutMilliseconds, token)).ConfigureAwait(false);
                        if (completedTask != fetchTask)
                        {
                            HandleTerminalFailure(requestId, "ontimeout", "TimeoutError", scheduleCallbacks);
                            return;
                        }
                    }

                    _response = await fetchTask.ConfigureAwait(false);
                    if (!IsActiveRequest(requestId, token))
                    {
                        return;
                    }

                    base.Set("status", FenValue.FromNumber((int)_response.StatusCode));
                    base.Set("statusText", FenValue.FromString(_response.ReasonPhrase ?? string.Empty));
                    base.Set("responseURL", FenValue.FromString(_response.RequestMessage?.RequestUri?.ToString() ?? _url ?? string.Empty));
                    SetReadyState(HEADERS_RECEIVED, scheduleCallbacks);

                    byte[] bytes = await _response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (!IsActiveRequest(requestId, token))
                    {
                        return;
                    }

                    SetReadyState(LOADING, scheduleCallbacks);
                    ApplyResponsePayload(bytes);
                    DispatchLifecycle("onprogress", scheduleCallbacks);

                    _sendFlag = false;
                    _uploadComplete = true;
                    SetReadyState(DONE, scheduleCallbacks);
                    DispatchLifecycle("onload", scheduleCallbacks);
                    DispatchLifecycle("onloadend", scheduleCallbacks);
                }
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested && _timeoutMilliseconds > 0 && IsActiveRequest(requestId, token, allowCancelledToken: true))
                {
                    HandleTerminalFailure(requestId, "ontimeout", "TimeoutError", scheduleCallbacks);
                }
            }
            catch (Exception ex)
            {
                FenLogger.Warn($"[XMLHttpRequest] Request failed: {ex.Message}", LogCategory.JavaScript);
                HandleTerminalFailure(requestId, "onerror", ex.Message, scheduleCallbacks);
            }
        }

        private HttpRequestMessage BuildRequest(string body)
        {
            var request = new HttpRequestMessage(new HttpMethod(_method), _url);
            foreach (var header in _requestHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (body != null && !string.Equals(_method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_method, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                request.Content = new StringContent(body, Encoding.UTF8);
                if (_requestHeaders.TryGetValue("Content-Type", out var contentType))
                {
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                }
            }

            ApplyExecutionOriginHeader(request);

            return request;
        }

        private void ApplyExecutionOriginHeader(HttpRequestMessage request)
        {
            if (request?.RequestUri == null || string.IsNullOrWhiteSpace(_context?.CurrentUrl))
            {
                return;
            }

            if (!Uri.TryCreate(_context.CurrentUrl, UriKind.Absolute, out var executionUri))
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

        private void ApplyResponsePayload(byte[] bytes)
        {
            string text = DecodeResponseText(bytes);
            string normalizedResponseType = NormalizeResponseType(_responseType);

            if (string.IsNullOrEmpty(normalizedResponseType) || string.Equals(normalizedResponseType, "text", StringComparison.Ordinal))
            {
                base.Set("responseText", FenValue.FromString(text));
                base.Set("response", FenValue.FromString(text));
                return;
            }

            if (string.Equals(normalizedResponseType, "json", StringComparison.Ordinal))
            {
                base.Set("responseText", FenValue.FromString(text));
                base.Set("response", ParseJsonResponse(text));
                return;
            }

            if (string.Equals(normalizedResponseType, "arraybuffer", StringComparison.Ordinal) ||
                string.Equals(normalizedResponseType, "blob", StringComparison.Ordinal))
            {
                var buffer = new JsArrayBuffer(bytes.Length);
                Array.Copy(bytes, buffer.Data, bytes.Length);
                base.Set("responseText", FenValue.FromString(string.Empty));
                base.Set("response", FenValue.FromObject(buffer));
                return;
            }

            base.Set("responseText", FenValue.FromString(text));
            base.Set("response", FenValue.FromString(text));
        }

        private string DecodeResponseText(byte[] bytes)
        {
            var charset = _overrideMimeType;
            if (string.IsNullOrWhiteSpace(charset))
            {
                charset = _response?.Content?.Headers?.ContentType?.CharSet;
            }

            if (!string.IsNullOrWhiteSpace(charset))
            {
                try
                {
                    return Encoding.GetEncoding(charset.Trim()).GetString(bytes);
                }
                catch (ArgumentException)
                {
                }
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private FenValue ParseJsonResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return FenValue.Null;
            }

            try
            {
                using (var document = JsonDocument.Parse(text))
                {
                    return ConvertJsonElement(document.RootElement);
                }
            }
            catch (JsonException)
            {
                return FenValue.Null;
            }
        }

        private FenValue ConvertJsonElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var obj = new FenObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        obj.Set(property.Name, ConvertJsonElement(property.Value));
                    }
                    return FenValue.FromObject(obj);
                case JsonValueKind.Array:
                    var array = FenObject.CreateArray();
                    int index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        array.Set(index.ToString(CultureInfo.InvariantCulture), ConvertJsonElement(item));
                        index++;
                    }
                    array.Set("length", FenValue.FromNumber(index));
                    return FenValue.FromObject(array);
                case JsonValueKind.String:
                    return FenValue.FromString(element.GetString() ?? string.Empty);
                case JsonValueKind.Number:
                    return FenValue.FromNumber(element.GetDouble());
                case JsonValueKind.True:
                    return FenValue.FromBoolean(true);
                case JsonValueKind.False:
                    return FenValue.FromBoolean(false);
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                default:
                    return FenValue.Null;
            }
        }

        private void HandleTerminalFailure(int requestId, string callbackName, string statusText, bool scheduleCallbacks)
        {
            if (!IsActiveRequest(requestId, _requestCts?.Token ?? CancellationToken.None, allowCancelledToken: true))
            {
                return;
            }

            _sendFlag = false;
            _response = null;
            base.Set("status", FenValue.FromNumber(0));
            base.Set("statusText", FenValue.FromString(statusText ?? string.Empty));
            base.Set("responseText", FenValue.FromString(string.Empty));
            base.Set("responseURL", FenValue.FromString(string.Empty));
            base.Set("response", FenValue.Null);
            SetReadyState(DONE, scheduleCallbacks);
            DispatchLifecycle(callbackName, scheduleCallbacks);
            DispatchLifecycle("onloadend", scheduleCallbacks);
        }

        private bool IsActiveRequest(int requestId, CancellationToken token, bool allowCancelledToken = false)
        {
            if (requestId != Volatile.Read(ref _requestVersion))
            {
                return false;
            }

            return allowCancelledToken || !token.IsCancellationRequested;
        }

        private static string NormalizeResponseType(string responseType)
        {
            switch ((responseType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "":
                case "text":
                case "json":
                case "arraybuffer":
                case "blob":
                case "document":
                    return (responseType ?? string.Empty).Trim().ToLowerInvariant();
                default:
                    return string.Empty;
            }
        }

        private FenValue GetAllResponseHeaders(FenValue[] args, FenValue thisVal)
        {
            if (_readyState < HEADERS_RECEIVED || _response == null)
            {
                return FenValue.FromString(string.Empty);
            }

            var builder = new StringBuilder();
            foreach (var header in _response.Headers)
            {
                builder.Append(header.Key);
                builder.Append(": ");
                builder.Append(string.Join(", ", header.Value));
                builder.Append("\r\n");
            }

            foreach (var header in _response.Content.Headers)
            {
                builder.Append(header.Key);
                builder.Append(": ");
                builder.Append(string.Join(", ", header.Value));
                builder.Append("\r\n");
            }

            return FenValue.FromString(builder.ToString());
        }

        private FenValue GetResponseHeader(FenValue[] args, FenValue thisVal)
        {
            if (_readyState < HEADERS_RECEIVED || _response == null || args.Length < 1)
            {
                return FenValue.Null;
            }

            var name = args[0].ToString();
            if (_response.Headers.TryGetValues(name, out var headerValues))
            {
                return FenValue.FromString(string.Join(", ", headerValues));
            }

            if (_response.Content.Headers.TryGetValues(name, out var contentHeaderValues))
            {
                return FenValue.FromString(string.Join(", ", contentHeaderValues));
            }

            return FenValue.Null;
        }
    }
}
