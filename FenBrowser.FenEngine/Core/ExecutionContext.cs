using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.Core.Dom.V2; // MutationRecord

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Default execution context implementation
    /// </summary>
    public class ExecutionContext : IExecutionContext
    {
        private readonly Stack<string> _callStack = new Stack<string>();
        private readonly Stopwatch _executionTimer = new Stopwatch();

        public IPermissionManager Permissions { get; }
        public IResourceLimits Limits { get; }
        public int CallStackDepth => _callStack.Count;
        public DateTime ExecutionStart { get; private set; }
        public bool ShouldContinue => _executionTimer.Elapsed < Limits.MaxExecutionTime;
        public Action RequestRender { get; private set; }
        public void SetRequestRender(Action action) => RequestRender = action;

        public Action<Action, int> ScheduleCallback { get; set; } = (action, delay) =>
        {
            if (action == null)
            {
                return;
            }

            _ = RunDetachedAsync(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                action();
            });
        };

        public Action<Action> ScheduleMicrotask { get; set; } = (action) =>
        {
            if (action == null)
            {
                return;
            }

            _ = RunDetached(action);
        };

        public FenValue ThisBinding { get; set; }
        public Func<FenValue, FenValue[], FenValue> ExecuteFunction { get; set; }
        public IModuleLoader ModuleLoader { get; set; }
        public Action<MutationRecord> OnMutation { get; set; }
        public string CurrentUrl { get; set; }
        public FenEnvironment Environment { get; set; }
        public FenValue NewTarget { get; set; }
        public string CurrentModulePath { get; set; }
        public bool StrictMode { get; set; }

        public Func<FenBrowser.FenEngine.Rendering.Core.ILayoutEngine> LayoutEngineProvider { get; set; }


        private static Task RunDetachedAsync(Func<Task> operation)
        {
            return Task.Factory.StartNew(async () =>
            {
                try
                {
                    await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ExecutionContext] Detached async operation failed: {ex.Message}");
                }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        private static Task RunDetached(Action operation)
        {
            return Task.Factory.StartNew(() =>
            {
                try
                {
                    operation();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ExecutionContext] Detached operation failed: {ex.Message}");
                }
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }

        public FenBrowser.FenEngine.Rendering.Core.ILayoutEngine GetLayoutEngine()
        {
            return LayoutEngineProvider?.Invoke();
        }

        public ExecutionContext(
            IPermissionManager permissions = null,
            IResourceLimits limits = null)
        {
            Permissions = permissions ?? new PermissionManager(JsPermissions.BasicWeb);
            Limits = limits ?? new DefaultResourceLimits();
            ExecutionStart = DateTime.UtcNow;
            _executionTimer.Start();
        }

        public void PushCallFrame(string functionName)
        {
            _callStack.Push(functionName ?? "<anonymous>");
            CheckCallStackLimit();
        }

        public void PopCallFrame()
        {
            if (_callStack.Count > 0)
                _callStack.Pop();
        }

        public void CheckCallStackLimit()
        {
            if (!Limits.CheckCallStack(CallStackDepth))
            {
                throw new Errors.FenResourceError(
                    $"Maximum call stack size exceeded ({Limits.MaxCallStackDepth})");
            }
        }

        public void CheckExecutionTimeLimit()
        {
            if (!ShouldContinue)
            {
                throw new Errors.FenTimeoutError(
                    $"Script execution timeout ({Limits.MaxExecutionTime.TotalSeconds}s)");
            }
        }

        /// <summary>
        /// Reset execution timer (for new script execution)
        /// </summary>
        public void Reset()
        {
            _callStack.Clear();
            _executionTimer.Restart();
            ExecutionStart = DateTime.UtcNow;
        }
    }
}


