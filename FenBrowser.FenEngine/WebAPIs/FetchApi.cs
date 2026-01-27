using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
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
                        try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\js_debug.log", $"[Fetch] Start: {request.Method} {request.Url}\n"); } catch {}
                        
                        // 1. Service Worker Interception
                        var sw = FenBrowser.FenEngine.Workers.ServiceWorkerManager.Instance.GetController(request.Url);
                        if (sw != null)
                        {
                             // TODO: Implement SW interception properly
                        }

                        // 2. Network Fetch via Handler
                        if (fetchHandler  == null) throw new Exception("FetchHandler missing");

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
                        try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\js_debug.log", $"[Fetch] Response: {resp.StatusCode} for {request.Url}\n"); } catch {}
                        
                        var jsResp = new JsResponse(resp, context);
                        
                        resolve.Invoke(new FenValue[] { FenValue.FromObject(jsResp) }, context);
                     }
                     catch (Exception ex)
                     {
                         try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\js_debug.log", $"[Fetch] Error: {ex.Message} for {request.Url}\n"); } catch {}
                         reject.Invoke(new FenValue[] { FenValue.FromString(ex.Message) }, context);
                     }
                 });
                 return FenValue.Undefined;
             })), context));
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
                    if (opts.Has("method")) Method = opts.Get("method").ToString().ToUpperInvariant();
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
             var key = args[0].ToString().ToLowerInvariant();
             var val = args[1].ToString();
             
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
             _headers[key.ToLowerInvariant()] = value;
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
