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
        public static void Register(IExecutionContext context)
        {
            var global = context.Environment;
            
            // Register 'fetch' global function
            global.Set("fetch", FenValue.FromFunction(new FenFunction("fetch", (args, thisVal) => Fetch(args, thisVal, context))));

            // Register 'Headers' class
            global.Set("Headers", FenValue.FromFunction(new FenFunction("Headers", JsHeaders.Constructor)));

            // Register 'Response' class
            global.Set("Response", FenValue.FromFunction(new FenFunction("Response", JsResponse.Constructor)));
        }

        // For testing
        public static Func<HttpClient> ClientFactory { get; set; } = () => new HttpClient();

        private static IValue Fetch(IValue[] args, IValue thisVal, IExecutionContext context)
        {
            if (args.Length < 1) return FenValue.Undefined;

            var url = args[0].ToString();
            // TODO: Parse options from args[1]

            // Return a Promise-like object (Thenable)
            var promise = new FenObject();
            
            Task.Run(async () => 
            {
                try
                {
                    // SERVICE WORKER INTERCEPTION
                    var sw = FenBrowser.FenEngine.Workers.ServiceWorkerManager.Instance.GetController(url);
                    if (sw != null)
                    {
                         // Create Request object
                         var req = new FenObject();
                         req.Set("url", FenValue.FromString(url));
                         req.Set("method", FenValue.FromString("GET"));
                         
                         // Create FetchEvent
                         var fetchEvt = new FetchEvent("fetch", req, context);
                         
                         // Dispatch to worker
                         var handled = await FenBrowser.FenEngine.Workers.ServiceWorkerManager.Instance.DispatchFetchEvent(sw, fetchEvt);
                         
                         if (handled && fetchEvt.RespondWithPromise != null)
                         {
                              var responseVal = await AwaitPromise(fetchEvt.RespondWithPromise);
                              ResolvePromise(promise, responseVal);
                              return;
                         }
                    }

                    // Network Fallback
                    using (var client = ClientFactory())
                    {
                        var response = await client.GetAsync(url);
                        var jsResponse = new JsResponse(response, context);
                        ResolvePromise(promise, FenValue.FromObject(jsResponse));
                    }
                }
                catch (Exception ex)
                {
                    RejectPromise(promise, ex.Message);
                }
            });
            
            // Basic 'then' implementation
            SetupPromiseThen(promise, context);
            
            return FenValue.FromObject(promise);
        }

        // --- Promise Helpers ---

        private static async Task<IValue> AwaitPromise(FenObject promise)
        {
             // Simple poll
             for(int i=0; i<100; i++) 
             {
                 var state = promise.Get("__state")?.ToString();
                 if (state == "fulfilled") return promise.Get("__result");
                 if (state == "rejected") throw new Exception(promise.Get("__reason")?.ToString() ?? "Rejected");
                 await Task.Delay(10);
             }
             throw new TimeoutException("Promise timed out");
        }

        private static void ResolvePromise(FenObject promise, IValue result)
        {
             if (promise.Has("onFulfilled"))
             {
                 var cb = promise.Get("onFulfilled").AsFunction();
                 // TODO: Schedule on main thread
                 cb?.Invoke(new[] { result }, null);
             }
             else
             {
                 promise.Set("__result", result);
                 promise.Set("__state", FenValue.FromString("fulfilled"));
             }
        }

        private static void RejectPromise(FenObject promise, string error)
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

        private static void SetupPromiseThen(FenObject promise, IExecutionContext context)
        {
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, _) =>
            {
                if (args.Length > 0) promise.Set("onFulfilled", args[0]);
                if (args.Length > 1) promise.Set("onRejected", args[1]);

                var state = promise.Get("__state")?.ToString();
                if (state == "fulfilled")
                {
                    var res = promise.Get("__result");
                    args[0]?.AsFunction()?.Invoke(new[] { res }, context);
                }
                else if (state == "rejected")
                {
                     var reason = promise.Get("__reason");
                     args[1]?.AsFunction()?.Invoke(new[] { reason }, context);
                }

                return FenValue.FromObject(promise); 
            })));
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
            Set("set", FenValue.FromFunction(new FenFunction("set", SetHeader)));
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

        private IValue SetHeader(IValue[] args, IValue thisVal)
        {
             if (args.Length < 2) return FenValue.Undefined;
             var key = args[0].ToString().ToLowerInvariant();
             _headers[key] = args[1].ToString();
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
