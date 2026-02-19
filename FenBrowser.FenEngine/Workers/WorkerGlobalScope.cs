using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Global scope object for a Web Worker.
    /// This is the "self" object inside a worker script.
    /// 
    /// Available APIs:
    /// - self.postMessage(data) - Send message to main thread
    /// - self.onmessage - Handler for incoming messages
    /// - self.onerror - Handler for errors
    /// - self.close() - Terminate the worker from inside
    /// - self.importScripts(urls...) - Load additional scripts (optional)
    /// 
    /// NOT available (no DOM):
    /// - document
    /// - window
    /// - Any DOM APIs
    /// </summary>
    public class WorkerGlobalScope : FenObject
    {
        protected readonly WorkerRuntime _runtime;
        public WorkerRuntime Runtime => _runtime;
        private readonly string _origin;
        private readonly string _name;
        private readonly Dictionary<string, List<FenValue>> _eventListeners = new(StringComparer.OrdinalIgnoreCase);

        public WorkerGlobalScope(WorkerRuntime runtime, string origin, string name = "")
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _origin = origin ?? "null";
            _name = name ?? "";

            InitializeProperties();
        }

        private void InitializeProperties()
        {
            // self reference (points to this)
            Set("self", FenValue.FromObject(this));

            // name property (DedicatedWorkerGlobalScope)
            Set("name", FenValue.FromString(_name));

            // location (simplified)
            var location = new FenObject();
            location.Set("origin", FenValue.FromString(_origin));
            location.Set("href", FenValue.FromString(_origin));
            Set("location", FenValue.FromObject(location));

            // addEventListener(type, callback)
            Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsString || !args[1].IsFunction)
                {
                    return FenValue.Undefined;
                }

                var type = args[0].ToString();
                if (!_eventListeners.TryGetValue(type, out var listeners))
                {
                    listeners = new List<FenValue>();
                    _eventListeners[type] = listeners;
                }

                listeners.Add(args[1]);
                return FenValue.Undefined;
            })));

            // removeEventListener(type, callback)
            Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsString)
                {
                    return FenValue.Undefined;
                }

                var type = args[0].ToString();
                if (_eventListeners.TryGetValue(type, out var listeners))
                {
                    var callback = args[1];
                    listeners.RemoveAll(existing => SameCallback(existing, callback));
                }

                return FenValue.Undefined;
            })));

            // dispatchEvent(event)
            Set("dispatchEvent", FenValue.FromFunction(new FenFunction("dispatchEvent", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsObject)
                {
                    return FenValue.FromBoolean(false);
                }

                var evt = args[0].AsObject();
                var type = evt?.Has("type") == true ? evt.Get("type").ToString() : string.Empty;
                if (string.IsNullOrWhiteSpace(type))
                {
                    return FenValue.FromBoolean(false);
                }

                DispatchEventToHandlers(type, args[0]);
                return FenValue.FromBoolean(true);
            })));

            // postMessage(data)
            Set("postMessage", FenValue.FromFunction(new FenFunction("postMessage", (args, thisVal) =>
            {
                if (args.Length > 0)
                {
                    var data = args[0].ToNativeObject();
                    _runtime.PostMessageToMain(data);
                }
                return FenValue.Undefined;
            })));

            // close() - terminate worker from inside
            Set("close", FenValue.FromFunction(new FenFunction("close", (args, thisVal) =>
            {
                FenLogger.Debug("[WorkerGlobalScope] close() called", LogCategory.JavaScript);
                _runtime.Terminate();
                return FenValue.Undefined;
            })));

            // importScripts(urls...) - fetch and execute additional worker scripts synchronously.
            Set("importScripts", FenValue.FromFunction(new FenFunction("importScripts", (args, thisVal) =>
            {
                if (args.Length == 0)
                {
                    return FenValue.Undefined;
                }

                var urls = new string[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    urls[i] = args[i].ToString();
                }

                _runtime.ImportScripts(urls);
                return FenValue.Undefined;
            })));

            // setTimeout
            Set("setTimeout", FenValue.FromFunction(new FenFunction("setTimeout", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsFunction)
                {
                    var callback = args[0].AsFunction();
                    var delay = args.Length > 1 ? (int)args[1].ToNumber() : 0;
                    
                    var timerId = Guid.NewGuid().GetHashCode();
                    
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(delay);
                        _runtime.QueueMicrotask(() =>
                        {
                            if (callback.IsNative && callback.NativeImplementation != null)
                                callback.NativeImplementation(new FenValue[0], FenValue.Undefined);
                        });
                    });
                    
                    return FenValue.FromNumber(timerId);
                }
                return FenValue.FromNumber(0);
            })));

            // console (basic)
            var console = new FenObject();
            console.Set("log", FenValue.FromFunction(new FenFunction("log", (args, thisVal) =>
            {
                var message = string.Join(" ", Array.ConvertAll(args, a => a.ToString()));
                FenLogger.Debug($"[Worker Console] {message}", LogCategory.JavaScript);
                return FenValue.Undefined;
            })));
            console.Set("error", FenValue.FromFunction(new FenFunction("error", (args, thisVal) =>
            {
                var message = string.Join(" ", Array.ConvertAll(args, a => a.ToString()));
                FenLogger.Debug($"[Worker Console ERROR] {message}", LogCategory.Errors);
                return FenValue.Undefined;
            })));
            Set("console", FenValue.FromObject(console));

            Set("onmessage", FenValue.Null);
            Set("onerror", FenValue.Null);
        }

        /// <summary>
        /// Dispatch a message event to the worker's onmessage handler
        /// </summary>
        public void DispatchMessage(object data)
        {
            var evt = new FenObject();
            evt.Set("data", ConvertToFenValue(data));
            evt.Set("type", FenValue.FromString("message"));
            evt.Set("origin", FenValue.FromString(_origin));
            DispatchEventToHandlers("message", FenValue.FromObject(evt));
        }

        /// <summary>
        /// Dispatch an error event to the worker's onerror handler
        /// </summary>
        public void DispatchError(Exception ex)
        {
            var evt = new FenObject();
            evt.Set("message", FenValue.FromString(ex.Message));
            evt.Set("type", FenValue.FromString("error"));
            DispatchEventToHandlers("error", FenValue.FromObject(evt));
        }

        protected void DispatchEventToHandlers(string eventType, FenValue eventObject)
        {
            if (string.IsNullOrWhiteSpace(eventType))
            {
                return;
            }

            var propertyHandler = Get($"on{eventType}");
            if (propertyHandler.IsFunction)
            {
                propertyHandler.AsFunction().Invoke(new[] { eventObject }, Runtime.Context);
            }

            if (_eventListeners.TryGetValue(eventType, out var listeners))
            {
                var snapshot = listeners.ToArray();
                foreach (var listener in snapshot)
                {
                    if (listener.IsFunction)
                    {
                        listener.AsFunction().Invoke(new[] { eventObject }, Runtime.Context);
                    }
                }
            }
        }

        private static bool SameCallback(FenValue left, FenValue right)
        {
            if (!left.IsFunction || !right.IsFunction)
            {
                return false;
            }

            if (ReferenceEquals(left.AsObject(), right.AsObject()))
            {
                return true;
            }

            return string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal);
        }

        private FenValue ConvertToFenValue(object obj)
        {
            if (obj  == null) return FenValue.Null;
            if (obj is bool b) return FenValue.FromBoolean(b);
            if (obj is string s) return FenValue.FromString(s);
            if (obj is int i) return FenValue.FromNumber(i);
            if (obj is long l) return FenValue.FromNumber(l);
            if (obj is float f) return FenValue.FromNumber(f);
            if (obj is double d) return FenValue.FromNumber(d);
            if (obj is FenValue fv) return fv;
            if (obj is IObject io) return FenValue.FromObject(io);
            if (obj is IValue v) return FenValue.FromString(v.ToString()); // Fallback

            // Complex objects - wrap in FenObject
            if (obj is IDictionary<string, object> dict)
            {
                var fenObj = new FenObject();
                foreach (var kvp in dict)
                {
                    fenObj.Set(kvp.Key, ConvertToFenValue(kvp.Value));
                }
                return FenValue.FromObject(fenObj);
            }

            return FenValue.FromString(obj.ToString());
        }
    }
}
