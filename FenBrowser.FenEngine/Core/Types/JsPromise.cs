using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core;

namespace FenBrowser.FenEngine.Core.Types
{
    public class JsPromise : FenObject
    {
        private Task<IValue> _task;
        private TaskCompletionSource<IValue> _tcs;
        private readonly IExecutionContext _context;

        // Constructor for "new Promise((resolve, reject) => ...)"
        public JsPromise(IValue executor, IExecutionContext context)
        {
            _context = context;
            _tcs = new TaskCompletionSource<IValue>();
            _task = _tcs.Task;

            if (executor != null && executor.IsFunction)
            {
                var resolve = new FenFunction("resolve", (args, thisVal) =>
                {
                    var val = args.Length > 0 ? args[0] : FenValue.Undefined;
                    _tcs.TrySetResult(val);
                    return FenValue.Undefined;
                });

                var reject = new FenFunction("reject", (args, thisVal) =>
                {
                    var reason = args.Length > 0 ? args[0] : FenValue.Undefined;
                    _tcs.TrySetException(new JsPromiseException(reason));
                    return FenValue.Undefined;
                });

                try
                {
                    // Executor runs immediately
                    executor.AsFunction().Invoke(new IValue[] { FenValue.FromFunction(resolve), FenValue.FromFunction(reject) }, _context);
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
            
            Init();
        }

        // Private constructor for internal task wrapping
        private JsPromise(Task<IValue> task, IExecutionContext context)
        {
            _task = task;
            _context = context;
            Init();
        }

        private void Init()
        {
            // Set 'then'
            Set("then", FenValue.FromFunction(new FenFunction("then", (args, thisVal) =>
            {
                var onFulfilled = args.Length > 0 && args[0].IsFunction ? args[0].AsFunction() : null;
                var onRejected = args.Length > 1 && args[1].IsFunction ? args[1].AsFunction() : null;

                var nextPromiseTask = _task.ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion)
                    {
                        if (onFulfilled != null)
                        {
                            // Use _context stored from creation time
                            try { return onFulfilled.Invoke(new IValue[] { t.Result }, _context); }
                            catch (Exception ex) { throw ex; } // Propagate exception
                        }
                        return t.Result; // Pass through
                    }
                    else
                    {
                        var exception = t.Exception?.InnerException;
                        IValue reason = FenValue.Undefined;
                        if (exception is JsPromiseException jpe) reason = jpe.Value;
                        else if (exception != null) reason = FenValue.FromString(exception.Message);

                        if (onRejected != null)
                        {
                            try { return onRejected.Invoke(new IValue[] { reason }, _context); }
                            catch (Exception ex) { throw ex; }
                        }
                        // If no handler, rethrow
                        if (exception != null) throw exception;
                        return FenValue.Undefined;
                    }
                }, TaskScheduler.Default); // Should ideally be Microtask queue

                return FenValue.FromObject(new JsPromise(nextPromiseTask, _context));
            })));

            // Set 'catch'
            Set("catch", FenValue.FromFunction(new FenFunction("catch", (args, thisVal) =>
            {
                var onRejected = args.Length > 0 && args[0].IsFunction ? args[0].AsFunction() : null;
                // Invoke 'then(undefined, onRejected)'
                // Get 'then' from 'this' (which is the promise object, or thisVal if called appropriately)
                // But here we are inside the Promise logic, we can just access "then" directly or via Get.
                return Get("then").AsFunction().Invoke(new IValue[] { FenValue.Undefined, args.Length > 0 ? args[0] : FenValue.Undefined }, _context);
            })));
            
            // Set 'finally'
            Set("finally", FenValue.FromFunction(new FenFunction("finally", (args, thisVal) =>
            {
                var onFinally = args.Length > 0 && args[0].IsFunction ? args[0].AsFunction() : null;
                
                var nextPromiseTask = _task.ContinueWith(t =>
                {
                   // Execute callback, but don't change result unless it throws
                   if (onFinally != null) 
                   {
                       try { onFinally.Invoke(new IValue[0], _context); }
                       catch (Exception ex) { throw ex; }
                   }
                   
                   if (t.IsFaulted) throw t.Exception.InnerException;
                   return t.Result;
                });
                
                return FenValue.FromObject(new JsPromise(nextPromiseTask, _context));
            })));
        }

        // Static Helpers
        public static JsPromise Resolve(IValue value, IExecutionContext context)
        {
            return new JsPromise(Task.FromResult(value), context);
        }

        public static JsPromise Reject(IValue reason, IExecutionContext context)
        {
            var tcs = new TaskCompletionSource<IValue>();
            tcs.SetException(new JsPromiseException(reason));
            return new JsPromise(tcs.Task, context);
        }
    }

    public class JsPromiseException : Exception
    {
        public IValue Value { get; }
        public JsPromiseException(IValue value) : base(value?.ToString() ?? "Error") { Value = value; }
    }
}
