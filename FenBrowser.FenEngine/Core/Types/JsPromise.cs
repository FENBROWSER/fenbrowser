using System;
using System.Collections.Generic;
using System.Linq;
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
            
            // .finally(f) returns a promise that executes f then passes through the previous result/error
            // .finally(f) returns a promise that executes f then passes through the previous result/error
            var P = this.Get("constructor") is FenValue fn && fn.IsFunction ? fn.AsFunction() as FenFunction : null;

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
                throw new JsPromiseException(reason); // Rethrow to propagate rejection
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
            // Simplified array support
            if (iterable.AsObject() is FenObject arr && arr.Keys().Any())
            {
                // TODO: Full iterable support
                return Resolve(FenValue.FromObject(new FenObject()), context); 
            }
            return Resolve(FenValue.FromObject(new FenObject()), context);
        }

        public static JsPromise Race(IValue iterable, IExecutionContext context)
        {
            // Stub implementation
            return Resolve(FenValue.Undefined, context);
        }

        public static JsPromise Any(IValue iterable, IExecutionContext context)
        {
             // Stub implementation
            return Resolve(FenValue.Undefined, context);
        }

        public static JsPromise AllSettled(IValue iterable, IExecutionContext context)
        {
             // Stub implementation
            return Resolve(FenValue.FromObject(new FenObject()), context);
        }
    }

    public class JsPromiseException : Exception
    {
        public FenValue value { get; }
        public JsPromiseException(FenValue value) : base(value.ToString()) { this.value = value; }
    }
}
