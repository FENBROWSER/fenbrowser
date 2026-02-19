using System;
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
        private readonly TaskQueue _taskQueue;
        private readonly MicrotaskQueue _microtaskQueue;
        private readonly CancellationTokenSource _cts;
        private readonly Thread _workerThread;
        private bool _isRunning;
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

            // Start worker thread
            _workerThread = new Thread(WorkerThreadProc)
            {
                Name = $"Worker-{Guid.NewGuid():N}",
                IsBackground = true
            };
            _workerThread.Start();

            FenLogger.Debug($"[WorkerRuntime] Created worker for {scriptUrl} (origin: {origin})", LogCategory.JavaScript);
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

                // Load and execute worker script
                _ = Task.Run(async () => {
                    try {
                        var scriptContent = await LoadWorkerScriptAsync().ConfigureAwait(false);

                        if (!string.IsNullOrEmpty(scriptContent)) {
                            _taskQueue.Enqueue(() => {
                                try {
                                    _runtime.ExecuteSimple(scriptContent);
                                    FenLogger.Debug($"[WorkerRuntime] Executed script: {_scriptUrl}", LogCategory.JavaScript);
                                } catch (Exception ex) {
                                    FenLogger.Error($"[WorkerRuntime] Script execution error: {ex.Message}", LogCategory.Errors);
                                    OnError?.Invoke(ex);
                                }
                            }, TaskSource.Networking, "Worker-ScriptLoad");
                        }
                    } catch (Exception ex) {
                        FenLogger.Error($"[WorkerRuntime] Script fetch error: {ex.Message}", LogCategory.Errors);
                        OnError?.Invoke(ex);
                    }
                });
                
                while (_isRunning && !_cts.IsCancellationRequested)
                {
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
                        // No tasks, sleep briefly
                        Thread.Sleep(10);
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

        public async Task<bool> DispatchServiceWorkerFetchAsync(FenBrowser.FenEngine.WebAPIs.FetchEvent fetchEvent)
        {
            if (!_isServiceWorker || fetchEvent == null || !_isRunning || _isDisposed)
                return false;

            if (_globalScope is not ServiceWorkerGlobalScope swScope)
                return false;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _taskQueue.Enqueue(() =>
            {
                try
                {
                    swScope.DispatchExtendableEvent("fetch", FenValue.FromObject(fetchEvent));
                    tcs.TrySetResult(fetchEvent.RespondWithPromise != null);
                }
                catch (Exception ex)
                {
                    FenLogger.Error($"[WorkerRuntime] Fetch event dispatch error: {ex.Message}", LogCategory.Errors);
                    OnError?.Invoke(ex);
                    tcs.TrySetResult(false);
                }
            }, TaskSource.Networking, "ServiceWorker.FetchEvent");

            return await tcs.Task.ConfigureAwait(false);
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
        /// Placeholder for invoking the worker's onmessage handler
        /// In a real implementation, this would call into the JS engine
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

            return await fetcher(_resolvedScriptUri).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            
            Terminate();
            _cts.Dispose();
            
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
