using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// JavaScript Worker constructor exposed to scripts.
    /// Usage: var worker = new Worker('script.js');
    /// </summary>
    public class WorkerConstructor
    {
        private readonly string _baseOrigin;
        private readonly Uri _baseUri;
        private readonly FenBrowser.FenEngine.Storage.IStorageBackend _storageBackend;
        private readonly Func<Uri, Task<string>> _scriptFetcher;
        private readonly Func<Uri, bool> _scriptUriAllowed;
        private readonly List<WorkerRuntime> _activeWorkers = new();

        public WorkerConstructor(
            string baseOrigin,
            FenBrowser.FenEngine.Storage.IStorageBackend storageBackend,
            Uri baseUri = null,
            Func<Uri, Task<string>> scriptFetcher = null,
            Func<Uri, bool> scriptUriAllowed = null)
        {
            _baseOrigin = baseOrigin ?? "null";
            _baseUri = baseUri;
            _storageBackend = storageBackend;
            _scriptFetcher = scriptFetcher;
            _scriptUriAllowed = scriptUriAllowed;
        }

        /// <summary>
        /// Get the Worker constructor function to expose to JS
        /// </summary>
        public FenFunction GetConstructorFunction()
        {
            return new FenFunction("Worker", CreateWorker);
        }

        private FenValue CreateWorker(FenValue[] args, FenValue thisVal)
        {
            if (args.Length == 0 || args[0] == null || args[0].IsUndefined)
            {
                FenLogger.Debug("[WorkerConstructor] Missing script URL", LogCategory.Errors);
                throw new ArgumentException("Worker constructor requires a script URL");
            }

            var scriptUrl = args[0].ToString();
            FenLogger.Debug($"[WorkerConstructor] Creating worker for: {scriptUrl}", LogCategory.JavaScript);

            if (!TryResolveScriptUri(scriptUrl, _baseUri, out var resolvedScriptUri))
            {
                throw new ArgumentException($"Invalid worker script URL: {scriptUrl}");
            }

            if (_scriptUriAllowed != null && !_scriptUriAllowed(resolvedScriptUri))
            {
                throw new UnauthorizedAccessException($"Worker script URL blocked by policy: {resolvedScriptUri}");
            }

            // Create the worker runtime
            var runtime = new WorkerRuntime(
                resolvedScriptUri.ToString(),
                _baseOrigin,
                _storageBackend,
                _scriptFetcher,
                _scriptUriAllowed,
                isServiceWorker: false);
            _activeWorkers.Add(runtime);

            // Create the Worker object exposed to JS
            var workerObj = new FenObject();

            // worker.postMessage(data)
            workerObj.Set("postMessage", FenValue.FromFunction(new FenFunction("postMessage", (pmArgs, pmThis) =>
            {
                if (pmArgs.Length > 0)
                {
                    var data = pmArgs[0].ToNativeObject();
                    runtime.PostMessage(data);
                }
                return FenValue.Undefined;
            })));

            // worker.terminate()
            workerObj.Set("terminate", FenValue.FromFunction(new FenFunction("terminate", (tArgs, tThis) =>
            {
                runtime.Terminate();
                _activeWorkers.Remove(runtime);
                return FenValue.Undefined;
            })));

            // worker.onmessage (initially null)
            workerObj.Set("onmessage", FenValue.Null);

            // worker.onerror (initially null)
            workerObj.Set("onerror", FenValue.Null);

            // Wire up events from runtime to worker object
            runtime.OnMessage += (data) =>
            {
                var onmessage = workerObj.Get("onmessage");
                if (onmessage != null && onmessage.IsFunction)
                {
                    var evt = new FenObject();
                    evt.Set("data", ConvertToFenValue(data));
                    evt.Set("type", FenValue.FromString("message"));

                    var fn = onmessage.AsFunction();
                    if (fn.IsNative && fn.NativeImplementation != null)
                    {
                        fn.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                    }
                }
            };

            runtime.OnError += (ex) =>
            {
                var onerror = workerObj.Get("onerror");
                if (onerror != null && onerror.IsFunction)
                {
                    var evt = new FenObject();
                    evt.Set("message", FenValue.FromString(ex.Message));
                    evt.Set("type", FenValue.FromString("error"));

                    var fn = onerror.AsFunction();
                    if (fn.IsNative && fn.NativeImplementation != null)
                    {
                        fn.NativeImplementation(new FenValue[] { FenValue.FromObject(evt) }, FenValue.Undefined);
                    }
                }
            };

            return FenValue.FromObject(workerObj);
        }

        /// <summary>
        /// Terminate all active workers (for cleanup on navigation)
        /// </summary>
        public void TerminateAll()
        {
            foreach (var worker in _activeWorkers.ToArray())
            {
                worker.Terminate();
                worker.Dispose();
            }
            _activeWorkers.Clear();
            FenLogger.Debug("[WorkerConstructor] All workers terminated", LogCategory.JavaScript);
        }

        /// <summary>
        /// Get count of active workers
        /// </summary>
        public int ActiveWorkerCount => _activeWorkers.Count;

        private static bool TryResolveScriptUri(string scriptUrl, Uri baseUri, out Uri resolved)
        {
            resolved = null;
            if (string.IsNullOrWhiteSpace(scriptUrl))
                return false;

            if (Uri.TryCreate(scriptUrl, UriKind.Absolute, out var absolute))
            {
                resolved = absolute;
                return IsSupportedWorkerScheme(resolved);
            }

            if (baseUri != null && Uri.TryCreate(baseUri, scriptUrl, out var relative))
            {
                resolved = relative;
                return IsSupportedWorkerScheme(resolved);
            }

            return false;
        }

        private static bool IsSupportedWorkerScheme(Uri uri)
        {
            if (uri == null) return false;
            return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
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
            if (obj is IValue v) return (FenValue)v;

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
