using System;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core.EventLoop;

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
        private readonly string _scriptUrl;
        private readonly TaskQueue _taskQueue;
        private readonly MicrotaskQueue _microtaskQueue;
        private readonly CancellationTokenSource _cts;
        private readonly Thread _workerThread;
        private bool _isRunning;
        private bool _isDisposed;

        /// <summary>
        /// Event fired when the worker sends a message to the main thread
        /// </summary>
        public event Action<object> OnMessage;

        /// <summary>
        /// Event fired when the worker encounters an error
        /// </summary>
        public event Action<Exception> OnError;

        /// <summary>
        /// Creates a new worker runtime with isolated execution context
        /// </summary>
        /// <param name="scriptUrl">URL of the worker script</param>
        /// <param name="origin">Origin for security context</param>
        public WorkerRuntime(string scriptUrl, string origin)
        {
            _scriptUrl = scriptUrl ?? throw new ArgumentNullException(nameof(scriptUrl));
            _origin = origin ?? "null";
            _taskQueue = new TaskQueue();
            _microtaskQueue = new MicrotaskQueue();
            _cts = new CancellationTokenSource();
            _isRunning = true;

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
                // Load and execute worker script
                // In a real impl, we'd fetch the script and execute it
                // For now, this is a placeholder for the event loop
                
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
            // This would be: workerGlobalScope.onmessage(evt)
            // For now, we just log
            FenLogger.Debug($"[WorkerRuntime] Received message: {evt.Data}", LogCategory.JavaScript);
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
