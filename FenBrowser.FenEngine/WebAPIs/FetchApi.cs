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
            // Use closure to capture context
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
            
            // We need to store state to handle .then() calls that happen AFTER resolution
            // But for a simple implementation, we can just assume the user calls .then() immediately
            // or we use a proper Promise polyfill structure.
            // Given the pattern in ServiceWorkerAPI, let's make a simple async bridge.
            
            Task.Run(async () => 
            {
                try
                {
                    // Use factory to allow tests to inject mock
                    using (var client = ClientFactory())
                    {
                        var response = await client.GetAsync(url);
                        var jsResponse = new JsResponse(response, context);
                        
                        // If callbacks are registered, invoke them
                        if (promise.Has("onFulfilled"))
                        {
                            var cb = promise.Get("onFulfilled")?.AsFunction();
                            // We need to invoke on the main thread/context? 
                            // Verify thread safety. For now, invoke directly, but in real engine this needs output queue.
                            context.ScheduleCallback(() => cb?.Invoke(new IValue[] { FenValue.FromObject(jsResponse) }, context), 0);
                        }
                        else
                        {
                            // Stash result?
                            promise.Set("__result", FenValue.FromObject(jsResponse));
                            promise.Set("__state", FenValue.FromString("fulfilled"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (promise.Has("onRejected"))
                    {
                        var cb = promise.Get("onRejected")?.AsFunction();
                        context.ScheduleCallback(() => cb?.Invoke(new IValue[] { FenValue.FromString(ex.Message) }, context), 0);
                    }
                    else
                    {
                         promise.Set("__error", FenValue.FromString(ex.Message));
                         promise.Set("__state", FenValue.FromString("rejected"));
                    }
                }
            });

            // Define .then(onFulfilled, onRejected)
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (thenArgs, thenThis) =>
            {
                if (thenArgs.Length > 0 && thenArgs[0].IsFunction)
                {
                    promise.Set("onFulfilled", thenArgs[0]);
                    
                    // If already finished (race condition check)
                    if (promise.Get("__state")?.ToString() == "fulfilled")
                    {
                         var res = promise.Get("__result");
                         thenArgs[0].AsFunction().Invoke(new IValue[] { res }, context);
                    }
                }
                if (thenArgs.Length > 1 && thenArgs[1].IsFunction)
                {
                    promise.Set("onRejected", thenArgs[1]);
                     if (promise.Get("__state")?.ToString() == "rejected")
                    {
                         var err = promise.Get("__error");
                         thenArgs[1].AsFunction().Invoke(new IValue[] { err }, context);
                    }
                }
                return FenValue.FromObject(promise); // Return same promise for chaining (simplified)
            })));

            return FenValue.FromObject(promise);
        }
    }

    public class JsHeaders : FenObject
    {
        private readonly Dictionary<string, string> _headers = new();

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
            
            // Properties
            Set("ok", FenValue.FromBoolean(response.IsSuccessStatusCode));
            Set("status", FenValue.FromNumber((int)response.StatusCode));
            Set("statusText", FenValue.FromString(response.ReasonPhrase));
            Set("headers", FenValue.FromObject(new JsHeaders())); // TODO: Populate headers
            
            // Methods
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
                     if (promise.Has("onFulfilled"))
                     {
                         var cb = promise.Get("onFulfilled").AsFunction();
                         if (_context != null)
                            _context.ScheduleCallback(() => cb.Invoke(new IValue[] { FenValue.FromString(content) }, _context), 0);
                         else
                            cb.Invoke(new IValue[] { FenValue.FromString(content) }, null);
                     }
                     else {
                         promise.Set("__result", FenValue.FromString(content));
                         promise.Set("__state", FenValue.FromString("fulfilled"));
                     }
                 }
                 catch (Exception ex)
                 {
                      if (promise.Has("onRejected"))
                      {
                         var cb = promise.Get("onRejected").AsFunction();
                         if (_context != null)
                             _context.ScheduleCallback(() => cb.Invoke(new IValue[] { FenValue.FromString(ex.Message) }, _context), 0);
                         else
                             cb.Invoke(new IValue[] { FenValue.FromString(ex.Message) }, null);
                      }
                 }
             });

             promise.Set("then", FenValue.FromFunction(new FenFunction("then", (thenArgs, thenThis) =>
             {
                 if (thenArgs.Length > 0 && thenArgs[0].IsFunction)
                 {
                     promise.Set("onFulfilled", thenArgs[0]);
                     if (promise.Get("__state")?.ToString() == "fulfilled")
                     {
                        var res = promise.Get("__result");
                        thenArgs[0].AsFunction().Invoke(new IValue[] { res }, _context);
                     }
                 }
                 return FenValue.FromObject(promise);
             })));

             return FenValue.FromObject(promise);
        }

        private IValue Json(IValue[] args, IValue thisVal)
        {
             // Same pattern as Text but returning string for now as JSON parsing is complex
             return Text(args, thisVal);
        }
    }
}
