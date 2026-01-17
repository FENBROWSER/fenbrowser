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

            // Register 'Headers' class
            global.Set("Headers", FenValue.FromFunction(new FenFunction("Headers", JsHeaders.Constructor)));

            // Register 'Response' class
            global.Set("Response", FenValue.FromFunction(new FenFunction("Response", JsResponse.Constructor)));
        }

        private static IValue Fetch(IValue[] args, IValue thisVal, IExecutionContext context, Func<HttpRequestMessage, Task<HttpResponseMessage>> fetchHandler)
        {
            if (args.Length < 1) return FenValue.Undefined;

            var input = args[0];
            var init = args.Length > 1 ? args[1] : FenValue.Undefined;
            
            string url = "";
            if (input.IsString) url = input.ToString();
            // TODO: Handle Request object as input
            
            // Parse Options
            string method = "GET";
            var headers = new JsHeaders();
            string body = null;
            
            if (init.IsObject)
            {
                var opts = init.AsObject();
                if (opts.Has("method")) method = opts.Get("method").ToString().ToUpperInvariant();
                if (opts.Has("body")) body = opts.Get("body").ToString();
                if (opts.Has("headers"))
                {
                    var hObj = opts.Get("headers");
                    // If it's a JsHeaders object
                    if (hObj is JsHeaders jsh) 
                    { 
                         foreach(var kv in jsh.GetHeaders()) headers.SetHeader(kv.Key, kv.Value);
                    }
                    // If it's a plain dict
                    else if (hObj.IsObject)
                    {
                        var dict = hObj.AsObject();
                        foreach(var k in dict.Keys()) headers.SetHeader(k, dict.Get(k).ToString());
                    }
                }
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
                        try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\js_debug.log", $"[Fetch] Start: {method} {url}\n"); } catch {}
                        
                        // 1. Service Worker Interception
                        var sw = FenBrowser.FenEngine.Workers.ServiceWorkerManager.Instance.GetController(url);
                        if (sw != null)
                        {
                             // ... (omitted for brevity)
                        }

                        // 2. Network Fetch via Handler
                        if (fetchHandler == null) throw new Exception("FetchHandler missing");

                        var req = new HttpRequestMessage(new HttpMethod(method), url);
                        if (body != null)
                        {
                            req.Content = new StringContent(body);
                             var ct = headers.GetHeader("content-type");
                            if (!string.IsNullOrEmpty(ct)) 
                            {
                                try { req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ct); } catch {}
                            }
                        }

                        foreach(var kv in headers.GetHeaders())
                        {
                            if (kv.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)) continue;
                             req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                        }

                        var resp = await fetchHandler(req);
                        try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\js_debug.log", $"[Fetch] Response: {resp.StatusCode} for {url}\n"); } catch {}
                        
                        var jsResp = new JsResponse(resp, context);
                        
                        resolve.Invoke(new IValue[] { FenValue.FromObject(jsResp) }, context);
                     }
                     catch (Exception ex)
                     {
                         try { System.IO.File.AppendAllText(@"C:\Users\udayk\Videos\FENBROWSER\js_debug.log", $"[Fetch] Error: {ex.Message} for {url}\n"); } catch {}
                         reject.Invoke(new IValue[] { FenValue.FromString(ex.Message) }, context);
                     }
                 });
                 return FenValue.Undefined;
             })), context));
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

        public static IValue Constructor(IValue[] args, IValue thisVal)
        {
              return FenValue.FromObject(new JsHeaders());
        }

        private IValue Append(IValue[] args, IValue thisVal)
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

        private IValue Get(IValue[] args, IValue thisVal)
        {
             if (args.Length < 1) return FenValue.Null;
             var key = args[0].ToString().ToLowerInvariant();
             return _headers.ContainsKey(key) ? FenValue.FromString(_headers[key]) : FenValue.Null;
        }

        private IValue Has(IValue[] args, IValue thisVal)
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

        private IValue SetHeaderBinding(IValue[] args, IValue thisVal)
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
            Set("headers", FenValue.FromObject(new JsHeaders())); 
            
            Set("text", FenValue.FromFunction(new FenFunction("text", Text)));
            Set("json", FenValue.FromFunction(new FenFunction("json", Json)));
        }

        public static IValue Constructor(IValue[] args, IValue thisVal)
        {
            return FenValue.FromObject(new JsResponse(new HttpResponseMessage()));
        }

        private IValue Text(IValue[] args, IValue thisVal)
        {
            var promise = new FenObject();
             Task.Run(async () =>
             {
                 try 
                 {
                     var content = await _response.Content.ReadAsStringAsync();
                     ResolvePromise(promise, FenValue.FromString(content));
                 }
                 catch (Exception ex)
                 {
                      RejectPromise(promise, ex.Message);
                 }
             });

             SetupPromiseThen(promise);

             return FenValue.FromObject(promise);
        }

        private IValue Json(IValue[] args, IValue thisVal)
        {
             return Text(args, thisVal);
        }

        // --- Duplicated Helpers for JsResponse (simplified) ---
        // Ideally refactor into PromiseUtil class
        
        private void ResolvePromise(FenObject promise, IValue result)
        {
             if (promise.Has("onFulfilled"))
             {
                 var cb = promise.Get("onFulfilled").AsFunction();
                 cb?.Invoke(new[] { result }, null);
             }
             else
             {
                 promise.Set("__result", result);
                 promise.Set("__state", FenValue.FromString("fulfilled"));
             }
        }

        private void RejectPromise(FenObject promise, string error)
        {
             if (promise.Has("onRejected"))
             {
                 var cb = promise.Get("onRejected").AsFunction();
                 cb?.Invoke(new[] { FenValue.FromString(error) }, null);
             }
             else
             {
                 promise.Set("__reason", FenValue.FromString(error));
                 promise.Set("__state", FenValue.FromString("rejected"));
             }
        }

        private void SetupPromiseThen(FenObject promise)
        {
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (thenArgs, thenThis) =>
            {
                if (thenArgs.Length > 0) promise.Set("onFulfilled", thenArgs[0]);
                if (thenArgs.Length > 1) promise.Set("onRejected", thenArgs[1]);

                var state = promise.Get("__state")?.ToString();
                if (state == "fulfilled")
                {
                    var res = promise.Get("__result");
                    thenArgs[0]?.AsFunction()?.Invoke(new[] { res }, _context);
                }
                else if (state == "rejected")
                {
                     var reason = promise.Get("__reason");
                     thenArgs[1]?.AsFunction()?.Invoke(new[] { reason }, _context);
                }
                return FenValue.FromObject(promise);
            })));
        }
    }
}
