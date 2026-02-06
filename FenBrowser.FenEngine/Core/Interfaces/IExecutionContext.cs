using System;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Security;
using FenBrowser.Core.Dom.V2; // MutationRecord

namespace FenBrowser.FenEngine.Core.Interfaces
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
        Action RequestRender { get; }
        void SetRequestRender(Action action);

        /// <summary>
        /// Schedule a callback to be executed after a delay
        /// Action: The callback to execute
        /// int: Delay in milliseconds
        /// </summary>
        Action<Action, int> ScheduleCallback { get; set; }

        /// <summary>
        /// Schedule a microtask (Promise resolution).
        /// Executed at the end of the current task.
        /// </summary>
        Action<Action> ScheduleMicrotask { get; set; }

        /// <summary>
        /// Current 'this' binding for function execution
        /// </summary>
        FenValue ThisBinding { get; set; }

        /// <summary>
        /// Delegate to execute a function (used by FenFunction.Invoke for user functions)
        /// </summary>
        Func<FenValue, FenValue[], FenValue> ExecuteFunction { get; set; }

        /// <summary>
        /// Module loader for resolving and loading modules
        /// </summary>
        IModuleLoader ModuleLoader { get; set; }

        /// <summary>
        /// Callback for DOM mutations
        /// </summary>
        Action<MutationRecord> OnMutation { get; set; }

        /// <summary>
        /// Current script URL being executed (for debugging)
        /// </summary>
        string CurrentUrl { get; set; }
        
        /// <summary>
        /// Global variable environment (scope)
        /// NOTE: FenEnvironment is in Core, so we might need circular dependency resolution or simplified interface.
        /// Interfaces usually shouldn't depend on concrete types if possible, but FenEnvironment is quite core.
        /// Using object/dynamic or keeping standard reference if Project reference allows.
        /// As they are in same project, referencing FenBrowser.FenEngine.Core is fine.
        /// </summary>
        FenEnvironment Environment { get; set; }
    }
}

