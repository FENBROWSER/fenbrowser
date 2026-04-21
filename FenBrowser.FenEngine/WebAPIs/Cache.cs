using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Storage;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Represents the Cache interface (Service Worker Cache API).
    /// Stores Request/Response pairs.
    /// </summary>
    public class Cache : FenObject
    {
        private readonly string _cacheName;
        private readonly string _origin;
        private readonly IStorageBackend _storage;
        private readonly Task _initializeTask;
        private volatile bool _invalidated;
        private readonly IExecutionContext _context;

        private const string StoreName = "cache_entries";
        private const int MaxCacheUrlLength = 2048;
        private const int MaxStatusTextLength = 128;
        private const int MaxCachedBodyLength = 1_048_576;
        private const int MaxHeaderCount = 64;

        public Cache(string origin, string cacheName, IStorageBackend storage, IExecutionContext context = null)
        {
            _origin = origin;
            _cacheName = cacheName;
            _storage = storage;
            _context = context;

            InitializeInterface();
            _initializeTask = InitializeStorage();
        }

        internal void Invalidate()
        {
            _invalidated = true;
        }

        private async Task InitializeStorage()
        {
            try
            {
                if (_invalidated)
                {
                    return;
                }

                await _storage.OpenDatabase(_origin, GetDatabaseName(_cacheName), 1).ConfigureAwait(false);

                if (_invalidated)
                {
                    return;
                }

                await _storage.CreateObjectStore(
                    _origin,
                    GetDatabaseName(_cacheName),
                    StoreName,
                    new ObjectStoreOptions { KeyPath = "url" }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Debug($"[Cache] Init: {ex.Message}", LogCategory.Storage);
            }
        }

        private async Task EnsureInitializedAsync()
        {
            if (_invalidated)
            {
                throw new InvalidOperationException("Cache has been deleted.");
            }

            await _initializeTask.ConfigureAwait(false);

            if (_invalidated)
            {
                throw new InvalidOperationException("Cache has been deleted.");
            }
        }

        private void InitializeInterface()
        {
            Set("match", FenValue.FromFunction(new FenFunction("match", Match)));
            Set("put", FenValue.FromFunction(new FenFunction("put", Put)));
            Set("delete", FenValue.FromFunction(new FenFunction("delete", Delete)));
            Set("keys", FenValue.FromFunction(new FenFunction("keys", Keys)));
        }

        private FenValue Match(FenValue[] args, FenValue thisVal)
        {
            if (args == null || args.Length < 1)
            {
                return FenValue.FromObject(CreateRejectedPromise("Cache.match requires a request argument."));
            }

            return FenValue.FromObject(CreatePromise(async () =>
            {
                await EnsureInitializedAsync().ConfigureAwait(false);

                var requestUrl = ResolveRequestUrl(args[0]);
                EnsureAllowedRequestUrl(requestUrl);

                var entry = await TryGetEntryAsync(requestUrl).ConfigureAwait(false);
                return entry == null ? FenValue.Undefined : CreateJsResponse(entry);
            }));
        }

        private FenValue Put(FenValue[] args, FenValue thisVal)
        {
            if (args == null || args.Length < 2)
            {
                return FenValue.FromObject(CreateRejectedPromise("Cache.put requires request and response arguments."));
            }

            if (!args[1].IsObject)
            {
                return FenValue.FromObject(CreateRejectedPromise("Cache.put response must be an object."));
            }

            return FenValue.FromObject(CreatePromise(async () =>
            {
                await EnsureInitializedAsync().ConfigureAwait(false);

                var requestUrl = ResolveRequestUrl(args[0]);
                EnsureAllowedRequestUrl(requestUrl);

                var responseObject = args[1].AsObject();
                var entry = SerializeResponse(requestUrl, responseObject);
                await _storage.Put(_origin, GetDatabaseName(_cacheName), StoreName, requestUrl, entry).ConfigureAwait(false);

                return FenValue.Undefined;
            }));
        }

        private FenValue Delete(FenValue[] args, FenValue thisVal)
        {
            if (args == null || args.Length < 1)
            {
                return FenValue.FromObject(CreatePromise(() => Task.FromResult(FenValue.FromBoolean(false))));
            }

            return FenValue.FromObject(CreatePromise(async () =>
            {
                await EnsureInitializedAsync().ConfigureAwait(false);

                var requestUrl = ResolveRequestUrl(args[0]);
                EnsureAllowedRequestUrl(requestUrl);

                var exists = await _storage.Get(_origin, GetDatabaseName(_cacheName), StoreName, requestUrl).ConfigureAwait(false) != null;
                if (exists)
                {
                    await _storage.Delete(_origin, GetDatabaseName(_cacheName), StoreName, requestUrl).ConfigureAwait(false);
                }

                return FenValue.FromBoolean(exists);
            }));
        }

        private FenValue Keys(FenValue[] args, FenValue thisVal)
        {
            return FenValue.FromObject(CreatePromise(async () =>
            {
                await EnsureInitializedAsync().ConfigureAwait(false);

                var keys = await _storage.GetAllKeys(_origin, GetDatabaseName(_cacheName), StoreName).ConfigureAwait(false);
                var keyList = new List<string>();
                foreach (var key in keys)
                {
                    if (key == null)
                    {
                        continue;
                    }

                    keyList.Add(key.ToString());
                }

                var array = new FenObject();
                array.Set("length", FenValue.FromNumber(keyList.Count));
                for (var i = 0; i < keyList.Count; i++)
                {
                    var request = new FenObject();
                    request.Set("url", FenValue.FromString(keyList[i]));
                    array.Set(i.ToString(), FenValue.FromObject(request));
                }

                return FenValue.FromObject(array);
            }));
        }

        internal async Task<FenValue> MatchRequestAsync(string requestUrl)
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            EnsureAllowedRequestUrl(requestUrl);

            var entry = await TryGetEntryAsync(requestUrl).ConfigureAwait(false);
            return entry == null ? FenValue.Undefined : CreateJsResponse(entry);
        }

        internal static string ResolveRequestUrl(FenValue request)
        {
            if (request.IsString)
            {
                return request.ToString();
            }

            if (request.IsObject)
            {
                var obj = request.AsObject();
                if (obj != null && obj.Has("url"))
                {
                    var urlValue = obj.Get("url");
                    if (!urlValue.IsUndefined && !urlValue.IsNull)
                    {
                        return urlValue.ToString();
                    }
                }
            }

            return request.ToString();
        }

        internal static void EnsureAllowedRequestUrl(string requestUrl)
        {
            if (string.IsNullOrWhiteSpace(requestUrl))
            {
                throw new InvalidOperationException("Cache request URL is empty.");
            }

            if (requestUrl.Length > MaxCacheUrlLength)
            {
                throw new InvalidOperationException($"Cache request URL exceeds max length {MaxCacheUrlLength}.");
            }

            if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException("Cache request URL is invalid.");
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Cache only supports http and https URLs.");
            }
        }

        private async Task<CacheEntry> TryGetEntryAsync(string requestUrl)
        {
            var raw = await _storage.Get(_origin, GetDatabaseName(_cacheName), StoreName, requestUrl).ConfigureAwait(false);
            return DeserializeCacheEntry(raw);
        }

        private static CacheEntry DeserializeCacheEntry(object raw)
        {
            if (raw is CacheEntry entry)
            {
                return entry;
            }

            if (raw is Dictionary<string, object> map)
            {
                var deserialized = new CacheEntry();
                if (map.TryGetValue("Url", out var urlValue)) deserialized.Url = urlValue?.ToString();
                if (map.TryGetValue("Status", out var statusValue) && int.TryParse(statusValue?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var status)) deserialized.Status = status;
                if (map.TryGetValue("StatusText", out var statusTextValue)) deserialized.StatusText = statusTextValue?.ToString();
                if (map.TryGetValue("Body", out var bodyValue)) deserialized.Body = bodyValue?.ToString();
                deserialized.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return deserialized;
            }

            return null;
        }

        private static string GetDatabaseName(string cacheName)
        {
            return $"cache_{cacheName}";
        }

        private CacheEntry SerializeResponse(string url, IObject response)
        {
            var status = 200;
            if (response != null && response.Has("status"))
            {
                var statusValue = response.Get("status");
                if (statusValue.IsNumber)
                {
                    var parsed = (int)Math.Round(statusValue.ToNumber());
                    if (parsed >= 100 && parsed <= 599)
                    {
                        status = parsed;
                    }
                }
            }

            var statusText = "OK";
            if (response != null && response.Has("statusText"))
            {
                var rawStatusText = response.Get("statusText").ToString() ?? string.Empty;
                rawStatusText = StripControlCharacters(rawStatusText).Trim();
                if (rawStatusText.Length > 0)
                {
                    statusText = rawStatusText.Length <= MaxStatusTextLength
                        ? rawStatusText
                        : rawStatusText.Substring(0, MaxStatusTextLength);
                }
            }

            var body = ExtractResponseBody(response);
            var headers = ExtractHeaders(response);

            return new CacheEntry
            {
                Url = url,
                Status = status,
                StatusText = statusText,
                Body = body,
                Headers = headers
            };
        }

        private static string ExtractResponseBody(IObject response)
        {
            if (response == null)
            {
                return string.Empty;
            }

            if (response is JsResponse jsResponse)
            {
                var content = jsResponse.ResponseMessage?.Content;
                if (content == null)
                {
                    return string.Empty;
                }

                var bodyFromJsResponse = content.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                if (bodyFromJsResponse.Length > MaxCachedBodyLength)
                {
                    bodyFromJsResponse = bodyFromJsResponse.Substring(0, MaxCachedBodyLength);
                }

                return bodyFromJsResponse;
            }

            FenValue bodyValue = FenValue.Undefined;
            if (response.Has("body"))
            {
                bodyValue = response.Get("body");
            }
            else if (response.Has("textBody"))
            {
                bodyValue = response.Get("textBody");
            }

            if (bodyValue.IsUndefined || bodyValue.IsNull)
            {
                return string.Empty;
            }

            var body = bodyValue.ToString() ?? string.Empty;
            if (body.Length > MaxCachedBodyLength)
            {
                body = body.Substring(0, MaxCachedBodyLength);
            }

            return body;
        }

        private static Dictionary<string, string> ExtractHeaders(IObject response)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (response == null || !response.Has("headers"))
            {
                return headers;
            }

            var headersValue = response.Get("headers");
            if (!headersValue.IsObject)
            {
                return headers;
            }

            var headerObject = headersValue.AsObject();
            if (headerObject == null)
            {
                return headers;
            }

            if (headerObject is JsHeaders jsHeaders)
            {
                var copied = 0;
                foreach (var header in jsHeaders.GetHeaders())
                {
                    if (copied >= MaxHeaderCount)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(header.Key))
                    {
                        continue;
                    }

                    headers[StripControlCharacters(header.Key).Trim()] = StripControlCharacters(header.Value ?? string.Empty).Trim();
                    copied++;
                }

                return headers;
            }

            var count = 0;
            foreach (var key in headerObject.Keys())
            {
                if (count >= MaxHeaderCount)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var value = headerObject.Get(key).ToString() ?? string.Empty;
                headers[StripControlCharacters(key).Trim()] = StripControlCharacters(value).Trim();
                count++;
            }

            return headers;
        }

        private FenValue CreateJsResponse(CacheEntry entry)
        {
            var httpResponse = new HttpResponseMessage((System.Net.HttpStatusCode)Math.Clamp(entry.Status, 100, 599))
            {
                ReasonPhrase = entry.StatusText ?? string.Empty,
                Content = new StringContent(entry.Body ?? string.Empty)
            };

            if (Uri.TryCreate(entry.Url ?? string.Empty, UriKind.Absolute, out var uri))
            {
                httpResponse.RequestMessage = new HttpRequestMessage(HttpMethod.Get, uri);
            }

            if (entry.Headers != null)
            {
                foreach (var header in entry.Headers)
                {
                    if (!httpResponse.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    {
                        httpResponse.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            var response = new JsResponse(httpResponse);
            response.Set("body", FenValue.FromString(entry.Body ?? string.Empty));
            response.Set("textBody", FenValue.FromString(entry.Body ?? string.Empty));
            return FenValue.FromObject(response);
        }

        private Task<FenValue> ParseJsonBody(string body)
        {
            try
            {
                using var document = JsonDocument.Parse(body ?? string.Empty);
                return Task.FromResult(ConvertJsonElement(document.RootElement));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Cached response body is not valid JSON: {ex.Message}");
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
                    var array = new FenObject();
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        array.Set(index.ToString(CultureInfo.InvariantCulture), ConvertJsonElement(item));
                        index++;
                    }
                    array.Set("length", FenValue.FromNumber(index));
                    return FenValue.FromObject(array);
                case JsonValueKind.String:
                    return FenValue.FromString(element.GetString());
                case JsonValueKind.Number:
                    return FenValue.FromNumber(element.GetDouble());
                case JsonValueKind.True:
                    return FenValue.FromBoolean(true);
                case JsonValueKind.False:
                    return FenValue.FromBoolean(false);
                case JsonValueKind.Null:
                    return FenValue.Null;
                default:
                    return FenValue.Undefined;
            }
        }

        private FenObject CreateRejectedPromise(string message)
        {
            return CreatePromise(() => throw new InvalidOperationException(message));
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
                    FenBrowser.Core.EngineLogCompat.Warn($"[Cache] Detached async operation failed: {ex.Message}", LogCategory.Storage);
                }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        private FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            // When a real IExecutionContext is available, use spec-compliant JsPromise
            // which integrates with the microtask queue for correct promise job ordering.
            if (_context != null)
            {
                FenValue capturedResolve = FenValue.Undefined;
                FenValue capturedReject = FenValue.Undefined;
                var executor = new FenFunction("executor", (args, thisVal) =>
                {
                    capturedResolve = args.Length > 0 ? args[0] : FenValue.Undefined;
                    capturedReject = args.Length > 1 ? args[1] : FenValue.Undefined;
                    return FenValue.Undefined;
                });
                var jsPromise = new Core.Types.JsPromise(FenValue.FromFunction(executor), _context);
                _ = RunDetachedAsync(async () =>
                {
                    try
                    {
                        var value = await valueFactory().ConfigureAwait(false);
                        if (capturedResolve.IsFunction)
                            capturedResolve.AsFunction().Invoke(new[] { value }, _context);
                    }
                    catch (Exception ex)
                    {
                        if (capturedReject.IsFunction)
                            capturedReject.AsFunction().Invoke(new[] { FenValue.FromString(ex.Message) }, _context);
                    }
                });
                return jsPromise;
            }

            // Fallback: hand-rolled promise for standalone/test contexts without IExecutionContext
            var promise = new FenObject();
            var gate = new object();
            var state = "pending";
            var settledValue = FenValue.Undefined;
            var fulfilledHandlers = new List<FenFunction>();
            var rejectedHandlers = new List<FenFunction>();

            promise.Set("__state", FenValue.FromString(state));

            void Settle(string nextState, FenValue value)
            {
                List<FenFunction> handlers;
                lock (gate)
                {
                    if (!string.Equals(state, "pending", StringComparison.Ordinal))
                    {
                        return;
                    }

                    state = nextState;
                    settledValue = value;

                    promise.Set("__state", FenValue.FromString(state));
                    if (string.Equals(state, "fulfilled", StringComparison.Ordinal))
                    {
                        promise.Set("__result", value);
                        handlers = new List<FenFunction>(fulfilledHandlers);
                    }
                    else
                    {
                        promise.Set("__reason", value);
                        handlers = new List<FenFunction>(rejectedHandlers);
                    }
                }

                foreach (var handler in handlers)
                {
                    TryInvokePromiseCallback(handler, value);
                }
            }

            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisValue) =>
            {
                FenFunction onFulfilled = null;
                FenFunction onRejected = null;
                if (args != null && args.Length > 0 && args[0].IsFunction)
                {
                    onFulfilled = args[0].AsFunction();
                }
                if (args != null && args.Length > 1 && args[1].IsFunction)
                {
                    onRejected = args[1].AsFunction();
                }

                string currentState;
                FenValue currentValue;
                lock (gate)
                {
                    if (onFulfilled != null)
                    {
                        fulfilledHandlers.Add(onFulfilled);
                    }
                    if (onRejected != null)
                    {
                        rejectedHandlers.Add(onRejected);
                    }

                    currentState = state;
                    currentValue = settledValue;
                }

                if (string.Equals(currentState, "fulfilled", StringComparison.Ordinal) && onFulfilled != null)
                {
                    TryInvokePromiseCallback(onFulfilled, currentValue);
                }
                else if (string.Equals(currentState, "rejected", StringComparison.Ordinal) && onRejected != null)
                {
                    TryInvokePromiseCallback(onRejected, currentValue);
                }

                return FenValue.FromObject(promise);
            })));

            promise.Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisValue) =>
            {
                FenFunction onRejected = null;
                if (args != null && args.Length > 0 && args[0].IsFunction)
                {
                    onRejected = args[0].AsFunction();
                }

                string currentState;
                FenValue currentValue;
                lock (gate)
                {
                    if (onRejected != null)
                    {
                        rejectedHandlers.Add(onRejected);
                    }

                    currentState = state;
                    currentValue = settledValue;
                }

                if (string.Equals(currentState, "rejected", StringComparison.Ordinal) && onRejected != null)
                {
                    TryInvokePromiseCallback(onRejected, currentValue);
                }

                return FenValue.FromObject(promise);
            })));

            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    var value = await valueFactory().ConfigureAwait(false);
                    Settle("fulfilled", value);
                }
                catch (Exception ex)
                {
                    Settle("rejected", FenValue.FromString(ex.Message));
                }
            });

            return promise;
        }

        private static void TryInvokePromiseCallback(FenFunction callback, FenValue value)
        {
            if (callback == null)
            {
                return;
            }

            try
            {
                callback.Invoke(new[] { value }, null);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn($"[Cache] Promise callback failed: {ex.Message}", LogCategory.Storage);
            }
        }

        private static string StripControlCharacters(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            var chars = input.ToCharArray();
            var kept = 0;
            for (var i = 0; i < chars.Length; i++)
            {
                if (!char.IsControl(chars[i]))
                {
                    chars[kept++] = chars[i];
                }
            }

            return kept == chars.Length ? input : new string(chars, 0, kept);
        }
    }

    public class CacheEntry
    {
        public string Url { get; set; }
        public int Status { get; set; }
        public string StatusText { get; set; }
        public Dictionary<string, string> Headers { get; set; }
        public string Body { get; set; }
    }
}



