using System;
using System.Collections.Generic;
using System.Diagnostics;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.Core.Dom; // MutationRecord

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Execution context for JavaScript code.
    /// Manages scopes, permissions, resource tracking, and limits.
    /// </summary>


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
            // Default implementation: Task.Delay then Run
            // Note: This does NOT run on UI thread by default, host must override for UI safety
            Task.Run(async () => 
            {
                await Task.Delay(delay);
                action();
                action();
            });
        };

        public Action<Action> ScheduleMicrotask { get; set; } = (action) => 
        {
            // Default: Run immediately or Task.Run (unsafe order without EventLoop)
            // Ideally should be overridden by Host to use EventLoopCoordinator
            Task.Run(() => action());
        };
        public FenValue ThisBinding { get; set; }
        public Func<FenValue, FenValue[], FenValue> ExecuteFunction { get; set; }
        public IModuleLoader ModuleLoader { get; set; }
        public Action<MutationRecord> OnMutation { get; set; }
        public string CurrentUrl { get; set; }
        public FenEnvironment Environment { get; set; }

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
