using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.EventLoop;

namespace FenBrowser.FenEngine.Core.Types
{
    /// <summary>
    /// A spec-compliant Promise implementation that uses the EventLoopCoordinator for microtasks.
    /// Eliminates .NET ThreadPool usage to ensure single-threaded run-to-completion semantics.
    /// </summary>
    public class JsPromise : FenObject
    {
        private enum PromiseState { Pending, Fulfilled, Rejected }

        private PromiseState _state = PromiseState.Pending;
        private FenValue _result = FenValue.Undefined;
        private readonly List<Reaction> _reactions = new List<Reaction>();
        private readonly IExecutionContext _context;

        /// <summary>True when the promise has been resolved or rejected.</summary>
        public bool IsSettled => _state != PromiseState.Pending;
        /// <summary>True when the promise was fulfilled.</summary>
        public bool IsFulfilled => _state == PromiseState.Fulfilled;
        /// <summary>True when the promise was rejected.</summary>
        public bool IsRejected => _state == PromiseState.Rejected;
        /// <summary>The resolved/rejected value. Only meaningful when IsSettled is true.</summary>
        public FenValue Result => _result;

        private struct Reaction
        {
            public JsPromise Capability;
            public FenValue OnFulfilled;
            public FenValue OnRejected;
        }

        // --- Constructors ---

        // "new Promise((resolve, reject) => ...)"
        public JsPromise(IValue executor, IExecutionContext context)
        {
            _context = context;
            InitPrototype();

            if (executor != null && executor.IsFunction)
            {
                var resolve = CreateResolveFunction();
                var reject = CreateRejectFunction();

                try
                {
                    // Executor runs synchronously immediately
                    executor.AsFunction().Invoke(new FenValue[] { resolve, reject }, _context);
                }
                catch (Exception ex)
                {
                    // Any error during execution rejects the promise
                    RejectPromise(FenValue.FromString(ex.Message));
                }
            }
        }

        // Private constructor for internal creation
        private JsPromise(IExecutionContext context)
        {
            _context = context;
            InitPrototype();
        }

        private void InitPrototype()
        {
            // Usually the prototype methods are shared, but for this implementation we'll attach them to the instance
            // or ensure FenObject prototype chain logic is used. 
            // For now, attaching instance methods matches the previous implementation style.
            Set("then", FenValue.FromFunction(new FenFunction("then", Then)));
            Set("catch", FenValue.FromFunction(new FenFunction("catch", Catch)));
            Set("finally", FenValue.FromFunction(new FenFunction("finally", Finally)));
            
            // Tag identifying this as a Promise
            Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, thisVal) => FenValue.FromString("[object Promise]"))));
        }

        // --- Core Logic ---

        private FenValue CreateResolveFunction()
        {
            return FenValue.FromFunction(new FenFunction("resolve", (args, thisVal) =>
            {
                ResolvePromise(args.Length > 0 ? args[0] : FenValue.Undefined);
                return FenValue.Undefined;
            }));
        }

        private FenValue CreateRejectFunction()
        {
            return FenValue.FromFunction(new FenFunction("reject", (args, thisVal) =>
            {
                RejectPromise(args.Length > 0 ? args[0] : FenValue.Undefined);
                return FenValue.Undefined;
            }));
        }

        private void ResolvePromise(FenValue resolution)
        {
            if (_state != PromiseState.Pending) return;

            // 25.4.1.3.2 Promise Resolve Functions
            if (resolution.IsObject && ReferenceEquals(resolution.AsObject(), this))
            {
                RejectPromise(FenValue.FromString("TypeError: Chaining cycle detected for promise"));
                return;
            }

            if (resolution.IsObject && resolution.AsObject() is JsPromise other)
            {
                // Adopt state of the other promise
                if (other._state == PromiseState.Pending)
                {
                    other.Then(CreateResolveFunction(), CreateRejectFunction());
                }
                else if (other._state == PromiseState.Fulfilled)
                {
                    FulfillPromise(other._result);
                }
                else // Rejected
                {
                    RejectPromise(other._result);
                }
                return;
            }

            // Normal value
            FulfillPromise(resolution);
        }

        private void FulfillPromise(FenValue value)
        {
            if (_state != PromiseState.Pending) return;
            _result = value;
            _state = PromiseState.Fulfilled;
            TriggerReactions();
        }

        private void RejectPromise(FenValue reason)
        {
            if (_state != PromiseState.Pending) return;
            _result = reason;
            _state = PromiseState.Rejected;
            TriggerReactions();
        }

        private void TriggerReactions()
        {
            // Schedule microtasks for all reactions
            foreach (var reaction in _reactions)
            {
                EventLoopCoordinator.Instance.ScheduleMicrotask(() => ExecuteReaction(reaction));
            }
            _reactions.Clear();

            // If resolution happens outside JS execution (for example in async host callbacks),
            // drain microtasks so promise continuations can progress without a separate task tick.
            if (EngineContext.Current.CurrentPhase == EnginePhase.Idle &&
                EventLoopCoordinator.Instance.HasPendingMicrotasks)
            {
                EventLoopCoordinator.Instance.PerformMicrotaskCheckpoint();
            }
        }

        private void ExecuteReaction(Reaction reaction)
        {
            // 25.4.1.2.1 Promise Reaction Job
            FenValue handler = (_state == PromiseState.Fulfilled) ? reaction.OnFulfilled : reaction.OnRejected;
            var capability = reaction.Capability;

            if (handler.IsUndefined || !handler.IsFunction)
            {
                // Fallthrough
                if (_state == PromiseState.Fulfilled) capability.ResolvePromise(_result);
                else capability.RejectPromise(_result);
                return;
            }

            try
            {
                var result = handler.AsFunction().Invoke(new FenValue[] { _result }, _context);
                capability.ResolvePromise(result);
            }
            catch (Exception ex)
            {
                capability.RejectPromise(FenValue.FromString(ex.Message));
            }
        }

        // --- Instance Methods ---

        private FenValue Then(FenValue[] args, FenValue thisVal)
        {
            var onFulfilled = args.Length > 0 && args[0].IsFunction ? args[0] : FenValue.Undefined;
            var onRejected = args.Length > 1 && args[1].IsFunction ? args[1] : FenValue.Undefined;

            var capability = new JsPromise(_context);

            var reaction = new Reaction
            {
                Capability = capability,
                OnFulfilled = onFulfilled,
                OnRejected = onRejected
            };

            if (_state == PromiseState.Pending)
            {
                _reactions.Add(reaction);
            }
            else
            {
                EventLoopCoordinator.Instance.ScheduleMicrotask(() => ExecuteReaction(reaction));
            }

            return FenValue.FromObject(capability);
        }

        private FenValue Catch(FenValue[] args, FenValue thisVal)
        {
            // .catch(onRejected) is .then(undefined, onRejected)
            var onRejected = args.Length > 0 ? args[0] : FenValue.Undefined;
            return Then(new FenValue[] { FenValue.Undefined, onRejected }, thisVal);
        }

        private FenValue Finally(FenValue[] args, FenValue thisVal)
        {
            var onFinally = args.Length > 0 && args[0].IsFunction ? args[0].AsFunction() : null;

            var onFulfilled = new FenFunction("finallyFulfilled", (a, t) =>
            {
                var value = a.Length > 0 ? a[0] : FenValue.Undefined;
                if (onFinally != null) onFinally.Invoke(new FenValue[0], _context);
                return value;
            });

            var onRejected = new FenFunction("finallyRejected", (a, t) =>
            {
                var reason = a.Length > 0 ? a[0] : FenValue.Undefined;
                if (onFinally != null) onFinally.Invoke(new FenValue[0], _context);
                // Preserve rejection through finally by returning an already-rejected promise.
                return FenValue.FromObject(Reject(reason, _context));
            });

            return Then(new FenValue[] { FenValue.FromFunction(onFulfilled), FenValue.FromFunction(onRejected) }, thisVal);
        }

        // --- Public Helpers for C# ---

        public void Then(FenValue onFulfilled, FenValue onRejected)
        {
            Then(new FenValue[] { onFulfilled, onRejected }, FenValue.FromObject(this));
        }

        // --- Static Methods ---

        public static JsPromise Resolve(FenValue value, IExecutionContext context)
        {
            var p = new JsPromise(context);
            p.ResolvePromise(value);
            return p;
        }

        public static JsPromise Reject(FenValue reason, IExecutionContext context)
        {
            var p = new JsPromise(context);
            p.RejectPromise(reason);
            return p;
        }

        public static JsPromise All(IValue iterable, IExecutionContext context)
        {
            // ES2015 Promise.all - resolves when all promises are fulfilled, rejects on first rejection
            var resultPromise = new JsPromise(context);
            
            if (!(iterable.AsObject() is FenObject arr))
            {
                resultPromise.ResolvePromise(FenValue.FromObject(new FenObject()));
                return resultPromise;
            }
            
            var keys = arr.Keys().OrderBy(k => int.TryParse(k, out var n) ? n : int.MaxValue).ToList();
            if (keys.Count == 0)
            {
                resultPromise.ResolvePromise(FenValue.FromObject(new FenObject()));
                return resultPromise;
            }
            
            var results = new FenValue[keys.Count];
            int remaining = keys.Count;
            bool rejected = false;
            
            for (int i = 0; i < keys.Count; i++)
            {
                int index = i;
                var item = arr.Get(keys[i], context);
                
                // Wrap in Promise.resolve to handle non-promise values
                JsPromise promise = item.AsObject() as JsPromise;
                if (promise == null)
                {
                    promise = Resolve(item, context);
                }
                
                promise.Then(
                    FenValue.FromFunction(new FenFunction("onFulfilled", (args, t) =>
                    {
                        if (rejected) return FenValue.Undefined;
                        results[index] = args.Length > 0 ? args[0] : FenValue.Undefined;
                        remaining--;
                        if (remaining == 0)
                        {
                            var resultArray = new FenObject { InternalClass = "Array" };
                            for (int j = 0; j < results.Length; j++)
                            {
                                resultArray.Set(j.ToString(), results[j], context);
                            }
                            resultArray.Set("length", FenValue.FromNumber(results.Length), context);
                            resultPromise.ResolvePromise(FenValue.FromObject(resultArray));
                        }
                        return FenValue.Undefined;
                    })),
                    FenValue.FromFunction(new FenFunction("onRejected", (args, t) =>
                    {
                        if (!rejected)
                        {
                            rejected = true;
                            resultPromise.RejectPromise(args.Length > 0 ? args[0] : FenValue.Undefined);
                        }
                        return FenValue.Undefined;
                    }))
                );
            }
            
            return resultPromise;
        }

        public static JsPromise Race(IValue iterable, IExecutionContext context)
        {
            // ES2015 Promise.race - first promise to settle wins
            var resultPromise = new JsPromise(context);
            
            if (!(iterable.AsObject() is FenObject arr))
            {
                return resultPromise; // Never settles for empty
            }
            
            bool settled = false;
            foreach (var key in arr.Keys())
            {
                var item = arr.Get(key, context);
                JsPromise promise = item.AsObject() as JsPromise;
                if (promise == null)
                {
                    promise = Resolve(item, context);
                }
                
                promise.Then(
                    FenValue.FromFunction(new FenFunction("onFulfilled", (args, t) =>
                    {
                        if (!settled)
                        {
                            settled = true;
                            resultPromise.ResolvePromise(args.Length > 0 ? args[0] : FenValue.Undefined);
                        }
                        return FenValue.Undefined;
                    })),
                    FenValue.FromFunction(new FenFunction("onRejected", (args, t) =>
                    {
                        if (!settled)
                        {
                            settled = true;
                            resultPromise.RejectPromise(args.Length > 0 ? args[0] : FenValue.Undefined);
                        }
                        return FenValue.Undefined;
                    }))
                );
            }
            
            return resultPromise;
        }

        public static JsPromise Any(IValue iterable, IExecutionContext context)
        {
            // ES2021 Promise.any - first fulfilled wins, rejects with AggregateError if all reject
            var resultPromise = new JsPromise(context);
            
            if (!(iterable.AsObject() is FenObject arr))
            {
                // Reject with AggregateError for empty iterable
                var aggError = new FenObject { InternalClass = "AggregateError" };
                aggError.Set("message", FenValue.FromString("All promises were rejected"), context);
                aggError.Set("errors", FenValue.FromObject(new FenObject { InternalClass = "Array" }), context);
                resultPromise.RejectPromise(FenValue.FromObject(aggError));
                return resultPromise;
            }
            
            var keys = arr.Keys().ToList();
            if (keys.Count == 0)
            {
                var aggError = new FenObject { InternalClass = "AggregateError" };
                aggError.Set("message", FenValue.FromString("All promises were rejected"), context);
                aggError.Set("errors", FenValue.FromObject(new FenObject { InternalClass = "Array" }), context);
                resultPromise.RejectPromise(FenValue.FromObject(aggError));
                return resultPromise;
            }
            
            var errors = new List<FenValue>();
            int remaining = keys.Count;
            bool fulfilled = false;
            
            foreach (var key in keys)
            {
                var item = arr.Get(key, context);
                JsPromise promise = item.AsObject() as JsPromise;
                if (promise == null)
                {
                    promise = Resolve(item, context);
                }
                
                promise.Then(
                    FenValue.FromFunction(new FenFunction("onFulfilled", (args, t) =>
                    {
                        if (!fulfilled)
                        {
                            fulfilled = true;
                            resultPromise.ResolvePromise(args.Length > 0 ? args[0] : FenValue.Undefined);
                        }
                        return FenValue.Undefined;
                    })),
                    FenValue.FromFunction(new FenFunction("onRejected", (args, t) =>
                    {
                        if (fulfilled) return FenValue.Undefined;
                        errors.Add(args.Length > 0 ? args[0] : FenValue.Undefined);
                        remaining--;
                        if (remaining == 0)
                        {
                            var aggError = new FenObject { InternalClass = "AggregateError" };
                            aggError.Set("message", FenValue.FromString("All promises were rejected"), context);
                            var errArray = new FenObject { InternalClass = "Array" };
                            for (int i = 0; i < errors.Count; i++)
                            {
                                errArray.Set(i.ToString(), errors[i], context);
                            }
                            errArray.Set("length", FenValue.FromNumber(errors.Count), context);
                            aggError.Set("errors", FenValue.FromObject(errArray), context);
                            resultPromise.RejectPromise(FenValue.FromObject(aggError));
                        }
                        return FenValue.Undefined;
                    }))
                );
            }
            
            return resultPromise;
        }

        public static JsPromise AllSettled(IValue iterable, IExecutionContext context)
        {
            // ES2020 Promise.allSettled - waits for all promises, returns array of {status, value/reason}
            var resultPromise = new JsPromise(context);
            
            if (!(iterable.AsObject() is FenObject arr))
            {
                var emptyArray = new FenObject { InternalClass = "Array" };
                emptyArray.Set("length", FenValue.FromNumber(0), context);
                resultPromise.ResolvePromise(FenValue.FromObject(emptyArray));
                return resultPromise;
            }
            
            var keys = arr.Keys().OrderBy(k => int.TryParse(k, out var n) ? n : int.MaxValue).ToList();
            if (keys.Count == 0)
            {
                var emptyArray = new FenObject { InternalClass = "Array" };
                emptyArray.Set("length", FenValue.FromNumber(0), context);
                resultPromise.ResolvePromise(FenValue.FromObject(emptyArray));
                return resultPromise;
            }
            
            var results = new FenObject[keys.Count];
            int remaining = keys.Count;
            
            for (int i = 0; i < keys.Count; i++)
            {
                int index = i;
                var item = arr.Get(keys[i], context);
                JsPromise promise = item.AsObject() as JsPromise;
                if (promise == null)
                {
                    promise = Resolve(item, context);
                }
                
                promise.Then(
                    FenValue.FromFunction(new FenFunction("onFulfilled", (args, t) =>
                    {
                        var result = new FenObject();
                        result.Set("status", FenValue.FromString("fulfilled"), context);
                        result.Set("value", args.Length > 0 ? args[0] : FenValue.Undefined, context);
                        results[index] = result;
                        remaining--;
                        if (remaining == 0)
                        {
                            var resultArray = new FenObject { InternalClass = "Array" };
                            for (int j = 0; j < results.Length; j++)
                            {
                                resultArray.Set(j.ToString(), FenValue.FromObject(results[j]), context);
                            }
                            resultArray.Set("length", FenValue.FromNumber(results.Length), context);
                            resultPromise.ResolvePromise(FenValue.FromObject(resultArray));
                        }
                        return FenValue.Undefined;
                    })),
                    FenValue.FromFunction(new FenFunction("onRejected", (args, t) =>
                    {
                        var result = new FenObject();
                        result.Set("status", FenValue.FromString("rejected"), context);
                        result.Set("reason", args.Length > 0 ? args[0] : FenValue.Undefined, context);
                        results[index] = result;
                        remaining--;
                        if (remaining == 0)
                        {
                            var resultArray = new FenObject { InternalClass = "Array" };
                            for (int j = 0; j < results.Length; j++)
                            {
                                resultArray.Set(j.ToString(), FenValue.FromObject(results[j]), context);
                            }
                            resultArray.Set("length", FenValue.FromNumber(results.Length), context);
                            resultPromise.ResolvePromise(FenValue.FromObject(resultArray));
                        }
                        return FenValue.Undefined;
                    }))
                );
            }
            
            return resultPromise;
        }
    }

    public class JsPromiseException : Exception
    {
        public FenValue value { get; }
        public JsPromiseException(FenValue value) : base(value.ToString()) { this.value = value; }
    }
}
