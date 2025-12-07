using System;
using System.Collections.Generic;
using System.Diagnostics;
using FenBrowser.FenEngine.Security;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Execution context for JavaScript code.
    /// Manages scopes, permissions, resource tracking, and limits.
    /// </summary>
    public interface IExecutionContext
    {
        /// <summary>
        /// Permission manager for security checks
        /// </summary>
        IPermissionManager Permissions { get; }

        /// <summary>
        /// Resource limits configuration
        /// </summary>
        IResourceLimits Limits { get; }

        /// <summary>
        /// Current call stack depth
        /// </summary>
        int CallStackDepth { get; }

        /// <summary>
        /// Execution start time
        /// </summary>
        DateTime ExecutionStart { get; }

        /// <summary>
        /// Check if execution should continue (within time limit)
        /// </summary>
        bool ShouldContinue { get; }

        /// <summary>
        /// Push a call frame onto the stack
        /// </summary>
        void PushCallFrame(string functionName);

        /// <summary>
        /// Pop a call frame from the stack
        /// </summary>
        void PopCallFrame();

        /// <summary>
        /// Check if call stack limit is exceeded
        /// </summary>
        void CheckCallStackLimit();

        /// <summary>
        /// Check if execution time limit is exceeded
        /// </summary>
        void CheckExecutionTimeLimit();

        /// <summary>
        /// Callback to trigger UI repaint
        /// </summary>
        /// <summary>
        /// Callback to trigger UI repaint
        /// </summary>
        Action RequestRender { get; }
        void SetRequestRender(Action action);

        /// <summary>
        /// Schedule a callback to be executed after a delay
        /// Action: The callback to execute
        /// int: Delay in milliseconds
        /// </summary>
        Action<Action, int> ScheduleCallback { get; set; }

        /// <summary>
        /// Current 'this' binding for function execution
        /// </summary>
        IValue ThisBinding { get; set; }

        /// <summary>
        /// Delegate to execute a function (used by FenFunction.Invoke for user functions)
        /// </summary>
        Func<IValue, IValue[], IValue> ExecuteFunction { get; set; }

        /// <summary>
        /// Module loader for resolving and loading modules
        /// </summary>
        IModuleLoader ModuleLoader { get; set; }
    }

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
            });
        };
        public IValue ThisBinding { get; set; }
        public Func<IValue, IValue[], IValue> ExecuteFunction { get; set; }
        public IModuleLoader ModuleLoader { get; set; }

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
