using System;
using System.Collections.Generic;
using System.Threading;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.DOM;

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

        // Timer management: monotonically increasing ID, cancellation per timer
        private int _nextTimerId = 1;
        private readonly Dictionary<int, CancellationTokenSource> _pendingTimers = new();
        private readonly object _timerLock = new();

        public WorkerGlobalScope(WorkerRuntime runtime, string origin, string name = "")
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _origin = origin ?? "null";
            _name = name ?? "";

            InitializeProperties();
        }


        private static System.Threading.Tasks.Task RunDetachedAsync(Func<System.Threading.Tasks.Task> operation)
        {
            return System.Threading.Tasks.Task.Factory.StartNew(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Timer cancellation is expected for clearTimeout/clearInterval.
                }
                catch (Exception ex)
                {
                    FenLogger.Debug($"[WorkerGlobalScope] Detached async operation error: {ex.Message}", LogCategory.JavaScript);
                }
            }, CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.DenyChildAttach, System.Threading.Tasks.TaskScheduler.Default).Unwrap();
        }        private void InitializeProperties()
        {
            // self reference (points to this)
            Set("self", FenValue.FromObject(this));

            var fontLoadingBindings = FontLoadingBindings.CreateForWorker(Runtime.Context);
            Set("fonts", FenValue.FromObject(fontLoadingBindings.Fonts));
            Set("FontFace", fontLoadingBindings.FontFaceConstructor);
            Set("FontFaceSetLoadEvent", fontLoadingBindings.FontFaceSetLoadEventConstructor);

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

            // importScripts(urls...) - execute additional worker scripts from prefetched source.
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

            // setTimeout — Per HTML spec, timer callbacks are tasks, not microtasks.
            Set("setTimeout", FenValue.FromFunction(new FenFunction("setTimeout", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction)
                    return FenValue.FromNumber(0);

                var callback = args[0];
                // Collect extra arguments to pass to callback (args[2..])
                var callbackArgs = args.Length > 2
                    ? args[2..]
                    : Array.Empty<FenValue>();
                var delay = Math.Max(0, args.Length > 1 ? (int)args[1].ToNumber() : 0);

                int timerId;
                var cts = new CancellationTokenSource();
                lock (_timerLock)
                {
                    timerId = _nextTimerId++;
                    _pendingTimers[timerId] = cts;
                }

                var capturedId = timerId;
                _ = RunDetachedAsync(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(delay, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return; // clearTimeout was called
                    }

                    lock (_timerLock) { _pendingTimers.Remove(capturedId); }

                    _runtime.QueueTask(() =>
                    {
                        if (callback.IsFunction)
                        {
                            try { callback.AsFunction().Invoke(callbackArgs, Runtime.Context); }
                            catch (Exception ex)
                            { FenLogger.Debug($"[WorkerGlobalScope] setTimeout callback error: {ex.Message}", LogCategory.JavaScript); }
                        }
                    }, $"setTimeout({capturedId})");
                });

                return FenValue.FromNumber(timerId);
            })));

            // clearTimeout
            Set("clearTimeout", FenValue.FromFunction(new FenFunction("clearTimeout", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsNumber)
                {
                    var id = (int)args[0].ToNumber();
                    CancellationTokenSource cts;
                    lock (_timerLock)
                    {
                        if (_pendingTimers.TryGetValue(id, out cts))
                            _pendingTimers.Remove(id);
                    }
                    cts?.Cancel();
                    cts?.Dispose();
                }
                return FenValue.Undefined;
            })));

            // setInterval — repeatedly fires callback at given interval as tasks
            Set("setInterval", FenValue.FromFunction(new FenFunction("setInterval", (args, thisVal) =>
            {
                if (args.Length == 0 || !args[0].IsFunction)
                    return FenValue.FromNumber(0);

                var callback = args[0];
                var callbackArgs = args.Length > 2 ? args[2..] : Array.Empty<FenValue>();
                var interval = Math.Max(4, args.Length > 1 ? (int)args[1].ToNumber() : 0);

                int timerId;
                var cts = new CancellationTokenSource();
                lock (_timerLock)
                {
                    timerId = _nextTimerId++;
                    _pendingTimers[timerId] = cts;
                }

                var capturedId = timerId;
                _ = RunDetachedAsync(async () =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            await System.Threading.Tasks.Task.Delay(interval, cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        if (cts.Token.IsCancellationRequested) return;

                        _runtime.QueueTask(() =>
                        {
                            if (callback.IsFunction)
                            {
                                try { callback.AsFunction().Invoke(callbackArgs, Runtime.Context); }
                                catch (Exception ex)
                                { FenLogger.Debug($"[WorkerGlobalScope] setInterval callback error: {ex.Message}", LogCategory.JavaScript); }
                            }
                        }, $"setInterval({capturedId})");
                    }
                });

                return FenValue.FromNumber(timerId);
            })));

            // clearInterval
            Set("clearInterval", FenValue.FromFunction(new FenFunction("clearInterval", (args, thisVal) =>
            {
                if (args.Length > 0 && args[0].IsNumber)
                {
                    var id = (int)args[0].ToNumber();
                    CancellationTokenSource cts;
                    lock (_timerLock)
                    {
                        if (_pendingTimers.TryGetValue(id, out cts))
                            _pendingTimers.Remove(id);
                    }
                    cts?.Cancel();
                    cts?.Dispose();
                }
                return FenValue.Undefined;
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
        /// Dispatch a message event to the worker's onmessage handler.
        /// Per HTML §2.7.1, message data is StructuredClone'd before delivery.
        /// </summary>
        public void DispatchMessage(object data)
        {
            var clonedData = StructuredClone.Clone(data);
            var evt = new FenObject();
            evt.Set("data", ConvertToFenValue(clonedData));
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

        internal void DispatchEventToHandlers(string eventType, FenValue eventObject)
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


