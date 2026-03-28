using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core; // Added for IExecutionContext
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Core.Interfaces; // Added for IExecutionContext

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Represents an isolated Web Worker runtime.
    /// Each worker has its own:
    /// - JavaScript engine instance (FenRuntime)
    /// - Event loop
    /// - Microtask queue
    /// - No DOM/Layout/Renderer access
    /// </summary>
    public class WorkerRuntime : IDisposable
    {
        private readonly string _origin;
        private readonly FenBrowser.FenEngine.Storage.IStorageBackend _storageBackend;
        private readonly string _scriptUrl;
        private readonly Uri _resolvedScriptUri;
        private readonly Func<Uri, Task<string>> _scriptFetcher;
        private readonly Func<Uri, bool> _scriptUriAllowed;
        private readonly bool _isServiceWorker;
        private readonly Dictionary<string, string> _prefetchedScriptCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _prefetchedScriptCacheLock = new();
        private readonly TaskQueue _taskQueue;
        private readonly MicrotaskQueue _microtaskQueue;
        private readonly CancellationTokenSource _cts;
        private readonly AutoResetEvent _taskSignal;
        private readonly Thread _workerThread;
        private readonly Task<string> _bootstrapScriptLoadTask;
        private bool _isRunning;
        private bool _bootstrapCompleted;
        private bool _isDisposed;
        private FenRuntime _runtime;
        private WorkerGlobalScope _globalScope;

        /// <summary>
        /// Event fired when the worker sends a message to the main thread
        /// </summary>
        public event Action<object> OnMessage;

        /// <summary>
        /// Event fired when the worker encounters an error
        /// </summary>
        public event Action<Exception> OnError;

        public IExecutionContext Context { get; private set; } // Added for Phase G

        /// <summary>
        /// Creates a new worker runtime with isolated execution context
        /// </summary>
        /// <param name="scriptUrl">URL of the worker script</param>
        /// <param name="origin">Origin for security context</param>
        public WorkerRuntime(
            string scriptUrl,
            string origin,
            FenBrowser.FenEngine.Storage.IStorageBackend storageBackend = null,
            Func<Uri, Task<string>> scriptFetcher = null,
            Func<Uri, bool> scriptUriAllowed = null,
            bool isServiceWorker = false)
        {
            _scriptUrl = scriptUrl ?? throw new ArgumentNullException(nameof(scriptUrl));
            _origin = origin ?? "null";
            _storageBackend = storageBackend ?? new FenBrowser.FenEngine.Storage.InMemoryStorageBackend();
            _scriptFetcher = scriptFetcher;
            _scriptUriAllowed = scriptUriAllowed;
            _isServiceWorker = isServiceWorker;
            _taskQueue = new TaskQueue();
            _microtaskQueue = new MicrotaskQueue();
            _cts = new CancellationTokenSource();
            _taskSignal = new AutoResetEvent(false);
            Context = new FenBrowser.FenEngine.Core.ExecutionContext(null); // Basic context for worker
            _isRunning = true;

            if (!Uri.TryCreate(_scriptUrl, UriKind.Absolute, out _resolvedScriptUri))
            {
                if (Uri.TryCreate(_origin, UriKind.Absolute, out var originUri) &&
                    Uri.TryCreate(originUri, _scriptUrl, out var resolvedFromOrigin))
                {
                    _resolvedScriptUri = resolvedFromOrigin;
                }
                else
                {
                    throw new ArgumentException($"Worker script URL must be absolute or origin-resolvable: {_scriptUrl}", nameof(scriptUrl));
                }
            }

            _bootstrapScriptLoadTask = LoadWorkerScriptAsync();
            _ = ObserveBootstrapCompletionAsync();

            // Start worker thread
            _workerThread = new Thread(WorkerThreadProc)
            {
                Name = $"Worker-{Guid.NewGuid():N}",
                IsBackground = true
            };
            _workerThread.Start();

            FenLogger.Debug($"[WorkerRuntime] Created worker for {scriptUrl} (origin: {origin})", LogCategory.JavaScript);
        }

        private async Task ObserveBootstrapCompletionAsync()
        {
            try
            {
                await _bootstrapScriptLoadTask.ConfigureAwait(false);
            }
            catch
            {
                // WorkerThreadProc/TryCompleteBootstrap surfaces the failure through OnError.
            }
            finally
            {
                try
                {
                    if (!_isDisposed)
                    {
                        _taskSignal.Set();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Worker disposal raced the bootstrap completion signal.
                }
            }
        }

        /// <summary>
        /// Post a message to the worker
        /// </summary>
        public void PostMessage(object data)
        {
            if (_isDisposed) return;

            var clonedData = StructuredClone.Clone(data);
            
            _taskQueue.Enqueue(() =>
            {
                try
                {
                    // Create MessageEvent-like object
                    var messageEvent = new WorkerMessageEvent
                    {
                        Data = clonedData,
                        Origin = _origin,
                        Type = "message"
                    };

                    // Invoke worker's onmessage handler
                    InvokeOnMessage(messageEvent);
                }
                catch (Exception ex)
                {
                    FenLogger.Debug($"[WorkerRuntime] PostMessage error: {ex.Message}", LogCategory.Errors);
                    OnError?.Invoke(ex);
                }
            }, TaskSource.Messaging, "Worker.postMessage");
            _taskSignal.Set();
        }

        /// <summary>
        /// Terminate the worker immediately
        /// </summary>
        public void Terminate()
        {
            if (_isDisposed) return;

            FenLogger.Debug("[WorkerRuntime] Terminating worker", LogCategory.JavaScript);
            _isRunning = false;
            _cts.Cancel();
            _taskQueue.Clear();
            _microtaskQueue.Clear();
            _taskSignal.Set();
        }

        /// <summary>
        /// Worker thread main loop - processes tasks and microtasks
        /// </summary>
        private void WorkerThreadProc()
        {
            FenLogger.Debug("[WorkerRuntime] Worker thread started", LogCategory.JavaScript);

            try
            {
                // Initialize dedicated runtime
                _globalScope = _isServiceWorker
                    ? new ServiceWorkerGlobalScope(this, _origin, string.Empty, _storageBackend)
                    : new WorkerGlobalScope(this, _origin);
                _runtime = new FenRuntime(); // We need a way to set the global object or prototype
                
                // Inject globals into the worker runtime
                foreach (var key in _globalScope.Keys())
                {
                    _runtime.SetGlobal(key, _globalScope.Get(key));
                }
                _runtime.SetGlobal("self", FenValue.FromObject(_globalScope));

                while (_isRunning && !_cts.IsCancellationRequested)
                {
                    if (!TryCompleteBootstrap())
                    {
                        _taskSignal.WaitOne();
                        continue;
                    }

                    // Process one task
                    var task = _taskQueue.Dequeue();
                    if (task != null)
                    {
                        try
                        {
                            task.Callback.Invoke();
                        }
                        catch (Exception ex)
                        {
                            FenLogger.Debug($"[WorkerRuntime] Task error: {ex.Message}", LogCategory.Errors);
                            OnError?.Invoke(ex);
                        }

                        // Drain microtasks after each task
                        _microtaskQueue.DrainAll();
                    }
                    else
                    {
                        // No tasks ready; block until new work arrives (or terminate signal).
                        _taskSignal.WaitOne();
                    }
                }
            }
            catch (Exception ex)
            {
                FenLogger.Debug($"[WorkerRuntime] Worker thread crashed: {ex.Message}", LogCategory.Errors);
                OnError?.Invoke(ex);
            }

            FenLogger.Debug("[WorkerRuntime] Worker thread stopped", LogCategory.JavaScript);
        }

        private bool TryCompleteBootstrap()
        {
            if (_bootstrapCompleted)
            {
                return true;
            }

            if (!_bootstrapScriptLoadTask.IsCompleted)
            {
                return false;
            }

            _bootstrapCompleted = true;

            try
            {
                if (_bootstrapScriptLoadTask.IsFaulted)
                {
                    throw _bootstrapScriptLoadTask.Exception?.GetBaseException()
                        ?? new InvalidOperationException("Worker bootstrap failed.");
                }

                if (_bootstrapScriptLoadTask.IsCanceled)
                {
                    throw new TaskCanceledException("Worker bootstrap was canceled.");
                }

                var scriptContent = _bootstrapScriptLoadTask.Result;
                if (!string.IsNullOrEmpty(scriptContent))
                {
                    _runtime.ExecuteSimple(scriptContent);
                    FenLogger.Debug($"[WorkerRuntime] Executed script: {_scriptUrl}", LogCategory.JavaScript);
                }
            }
            catch (Exception ex)
            {
                FenLogger.Error($"[WorkerRuntime] Script fetch/execute error: {ex.Message}", LogCategory.Errors);
                OnError?.Invoke(ex);
            }

            return true;
        }

        public async Task<bool> DispatchServiceWorkerFetchAsync(FenBrowser.FenEngine.WebAPIs.FetchEvent fetchEvent)
        {
            if (!_isServiceWorker || fetchEvent == null || !_isRunning || _isDisposed)
                return false;

            if (_globalScope is not ServiceWorkerGlobalScope swScope)
                return false;

            var dispatchDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _taskQueue.Enqueue(() =>
            {
                try
                {
                    swScope.DispatchExtendableEvent("fetch", FenValue.FromObject(fetchEvent));
                    dispatchDone.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    FenLogger.Error($"[WorkerRuntime] Fetch event dispatch error: {ex.Message}", LogCategory.Errors);
                    OnError?.Invoke(ex);
                    dispatchDone.TrySetResult(false);
                }
            }, TaskSource.Networking, "ServiceWorker.FetchEvent");
            _taskSignal.Set();

            var dispatched = await dispatchDone.Task.ConfigureAwait(false);
            if (!dispatched)
            {
                return false;
            }

            // Spec requires respondWith() registration during fetch event dispatch.
            var hasRespondWith = await fetchEvent.WaitForRespondWithRegistrationAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            if (!hasRespondWith)
            {
                return false;
            }

            _ = fetchEvent.WaitForLifetimePromisesAsync(TimeSpan.FromMilliseconds(300));
            return true;
        }

        /// <summary>
        /// Send a message from worker to main thread (called by worker script)
        /// </summary>
        public void PostMessageToMain(object data)
        {
            if (_isDisposed) return;

            var clonedData = StructuredClone.Clone(data);
            
            // Schedule on main thread's event loop
            EventLoopCoordinator.Instance.ScheduleTask(() =>
            {
                OnMessage?.Invoke(clonedData);
            }, TaskSource.Messaging, "Worker.onmessage");
        }

        /// <summary>
        /// Schedule a microtask in the worker context
        /// </summary>
        internal void QueueMicrotask(Action callback)
        {
            _microtaskQueue.Enqueue(callback);
        }

        /// <summary>
        /// Schedule a task in the worker task queue (e.g. timer callbacks).
        /// Per HTML spec, timer callbacks are tasks, not microtasks.
        /// </summary>
        internal void QueueTask(Action callback, string label = "timer")
        {
            if (_isDisposed || !_isRunning) return;
            _taskQueue.Enqueue(callback, TaskSource.Timer, label);
            _taskSignal.Set();
        }

        /// <summary>
        /// Dispatch a named event to the worker's global scope event handlers.
        /// Used by ServiceWorkerManager for lifecycle events (install, activate, fetch).
        /// </summary>
        internal void DispatchGlobalEvent(string eventType, FenValue eventObject)
        {
            if (_globalScope is ServiceWorkerGlobalScope swScope)
            {
                swScope.DispatchExtendableEvent(eventType, eventObject);
            }
            else
            {
                _globalScope?.DispatchEventToHandlers(eventType, eventObject);
            }
        }

        /// <summary>
        /// Dispatches a message event to the worker's global scope onmessage handler.
        /// Routes through WorkerGlobalScope.DispatchMessage which invokes registered JS handlers.
        /// </summary>
        private void InvokeOnMessage(WorkerMessageEvent evt)
        {
            _globalScope?.DispatchMessage(evt.Data);
        }

        private async Task<string> LoadWorkerScriptAsync()
        {
            if (_scriptUriAllowed != null && !_scriptUriAllowed(_resolvedScriptUri))
                throw new UnauthorizedAccessException($"Worker script blocked by policy: {_resolvedScriptUri}");

            var fetcher = _scriptFetcher;
            if (fetcher == null)
                throw new InvalidOperationException("Worker script fetcher is not configured");

            var rootScript = await fetcher(_resolvedScriptUri).ConfigureAwait(false);
            CachePrefetchedScript(_resolvedScriptUri, rootScript);
            return rootScript;
        }

        internal void ImportScripts(params string[] scriptUrls)
        {
            if (scriptUrls == null || scriptUrls.Length == 0)
                return;

            if (_runtime == null)
                throw new InvalidOperationException("Worker runtime is not initialized");

            if (_scriptFetcher == null)
                throw new InvalidOperationException("Worker script fetcher is not configured");

            foreach (var raw in scriptUrls)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var target = ResolveImportScriptUri(raw);
                if (target == null)
                    throw new ArgumentException($"Invalid importScripts URL: {raw}", nameof(scriptUrls));

                if (_scriptUriAllowed != null && !_scriptUriAllowed(target))
                    throw new UnauthorizedAccessException($"importScripts blocked by policy: {target}");

                var content = GetOrLoadImportScript(target);
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                _runtime.ExecuteSimple(content);
                FenLogger.Debug($"[WorkerRuntime] importScripts executed: {target}", LogCategory.JavaScript);
            }
        }

        private string GetOrLoadImportScript(Uri target)
        {
            if (TryGetPrefetchedScript(target, out var cached))
                return cached;

            try
            {
                var content = _scriptFetcher(target).ConfigureAwait(false).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(content))
                    CachePrefetchedScript(target, content);

                return content;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load importScripts target: {target}", ex);
            }
        }

        private void CachePrefetchedScript(Uri target, string content)
        {
            if (target == null || string.IsNullOrWhiteSpace(content))
                return;

            lock (_prefetchedScriptCacheLock)
            {
                _prefetchedScriptCache[target.AbsoluteUri] = content;
            }
        }

        private bool TryGetPrefetchedScript(Uri target, out string content)
        {
            content = null;
            if (target == null)
                return false;

            lock (_prefetchedScriptCacheLock)
            {
                return _prefetchedScriptCache.TryGetValue(target.AbsoluteUri, out content);
            }
        }

        private Uri ResolveImportScriptUri(string candidate)
        {
            Uri baseUri = _resolvedScriptUri;
            if (baseUri == null && !Uri.TryCreate(_origin, UriKind.Absolute, out baseUri))
                return null;

            if (!Uri.TryCreate(baseUri, candidate, out var resolved))
                return null;

            if (!(resolved.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  resolved.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            if (!IsSameOrigin(baseUri, resolved))
                return null;

            return resolved;
        }

        private static bool IsSameOrigin(Uri left, Uri right)
        {
            if (left == null || right == null)
                return false;

            return left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) &&
                   left.Port == right.Port;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            
            Terminate();
            _cts.Dispose();
            _taskSignal.Dispose();
            
            FenLogger.Debug("[WorkerRuntime] Worker disposed", LogCategory.JavaScript);
        }
    }

    /// <summary>
    /// Message event object passed to worker's onmessage handler
    /// </summary>
    public class WorkerMessageEvent
    {
        public object Data { get; set; }
        public string Origin { get; set; }
        public string Type { get; set; }
    }
}
