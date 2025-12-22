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

        /// <summary>
        /// Message handler callback
        /// </summary>
        public IValue OnMessage { get; set; }

        /// <summary>
        /// Error handler callback
        /// </summary>
        public IValue OnError { get; set; }

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

            // postMessage(data)
            Set("postMessage", FenValue.FromFunction(new FenFunction("postMessage", (args, thisVal) =>
            {
                if (args.Length > 0)
                {
                    var data = args[0].ToObject();
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

            // importScripts(urls...) - placeholder
            Set("importScripts", FenValue.FromFunction(new FenFunction("importScripts", (args, thisVal) =>
            {
                // In a real implementation, this would fetch and execute scripts
                FenLogger.Debug($"[WorkerGlobalScope] importScripts called with {args.Length} URLs", LogCategory.JavaScript);
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
                                callback.NativeImplementation(Array.Empty<IValue>(), FenValue.Undefined);
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

            // onmessage/onerror as getters/setters
            OnMessage = FenValue.Null;
            OnError = FenValue.Null;
        }

        /// <summary>
        /// Dispatch a message event to the worker's onmessage handler
        /// </summary>
        public void DispatchMessage(object data)
        {
            if (OnMessage == null || OnMessage.IsNull || !OnMessage.IsFunction)
                return;

            var evt = new FenObject();
            evt.Set("data", ConvertToFenValue(data));
            evt.Set("type", FenValue.FromString("message"));
            evt.Set("origin", FenValue.FromString(_origin));

            var fn = OnMessage.AsFunction();
            if (fn.IsNative && fn.NativeImplementation != null)
            {
                fn.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
            }
        }

        /// <summary>
        /// Dispatch an error event to the worker's onerror handler
        /// </summary>
        public void DispatchError(Exception ex)
        {
            if (OnError == null || OnError.IsNull || !OnError.IsFunction)
                return;

            var evt = new FenObject();
            evt.Set("message", FenValue.FromString(ex.Message));
            evt.Set("type", FenValue.FromString("error"));

            var fn = OnError.AsFunction();
            if (fn.IsNative && fn.NativeImplementation != null)
            {
                fn.NativeImplementation(new IValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
            }
        }

        private IValue ConvertToFenValue(object obj)
        {
            if (obj == null) return FenValue.Null;
            if (obj is bool b) return FenValue.FromBoolean(b);
            if (obj is string s) return FenValue.FromString(s);
            if (obj is int i) return FenValue.FromNumber(i);
            if (obj is long l) return FenValue.FromNumber(l);
            if (obj is float f) return FenValue.FromNumber(f);
            if (obj is double d) return FenValue.FromNumber(d);
            if (obj is IValue v) return v;

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
