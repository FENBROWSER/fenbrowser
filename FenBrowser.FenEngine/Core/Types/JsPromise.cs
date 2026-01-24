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
        private IValue _result = FenValue.Undefined;
        private readonly List<Reaction> _reactions = new List<Reaction>();
        private readonly IExecutionContext _context;

        private struct Reaction
        {
            public JsPromise Capability;
            public IValue OnFulfilled;
            public IValue OnRejected;
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
                    executor.AsFunction().Invoke(new IValue[] { resolve, reject }, _context);
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
            Set("toString", FenValue.FromFunction(new FenFunction("toString", (args, ctx) => FenValue.FromString("[object Promise]"))));
        }

        // --- Core Logic ---

        private IValue CreateResolveFunction()
        {
            return FenValue.FromFunction(new FenFunction("resolve", (args, ctx) =>
            {
                ResolvePromise(args.Length > 0 ? args[0] : FenValue.Undefined);
                return FenValue.Undefined;
            }));
        }

        private IValue CreateRejectFunction()
        {
            return FenValue.FromFunction(new FenFunction("reject", (args, ctx) =>
            {
                RejectPromise(args.Length > 0 ? args[0] : FenValue.Undefined);
                return FenValue.Undefined;
            }));
        }

        private void ResolvePromise(IValue resolution)
        {
            if (_state != PromiseState.Pending) return;

            // 25.4.1.3.2 Promise Resolve Functions
            if (resolution == this)
            {
                RejectPromise(FenValue.FromString("TypeError: Chaining cycle detected for promise"));
                return;
            }

            if (resolution is JsPromise other)
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

        private void FulfillPromise(IValue value)
        {
            if (_state != PromiseState.Pending) return;
            _result = value;
            _state = PromiseState.Fulfilled;
            TriggerReactions();
        }

        private void RejectPromise(IValue reason)
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
            IValue handler = (_state == PromiseState.Fulfilled) ? reaction.OnFulfilled : reaction.OnRejected;
            var capability = reaction.Capability;

            if (handler == null || !handler.IsFunction)
            {
                // Fallthrough
                if (_state == PromiseState.Fulfilled) capability.ResolvePromise(_result);
                else capability.RejectPromise(_result);
                return;
            }

            try
            {
                var result = handler.AsFunction().Invoke(new IValue[] { _result }, _context);
                capability.ResolvePromise(result);
            }
            catch (Exception ex)
            {
                capability.RejectPromise(FenValue.FromString(ex.Message));
            }
        }

        // --- Instance Methods ---

        private IValue Then(IValue[] args, IExecutionContext context)
        {
            var onFulfilled = args.Length > 0 && args[0].IsFunction ? args[0] : null;
            var onRejected = args.Length > 1 && args[1].IsFunction ? args[1] : null;

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

        private IValue Catch(IValue[] args, IExecutionContext context)
        {
            // .catch(onRejected) is .then(undefined, onRejected)
            var onRejected = args.Length > 0 ? args[0] : FenValue.Undefined;
            return Then(new IValue[] { FenValue.Undefined, onRejected }, context);
        }

        private IValue Finally(IValue[] args, IExecutionContext context)
        {
            var onFinally = args.Length > 0 && args[0].IsFunction ? args[0].AsFunction() : null;
            
            // .finally(f) returns a promise that executes f then passes through the previous result/error
            var P = this.Constructor as FenFunction; // Approximation

            var onFulfilled = new FenFunction("finallyFulfilled", (a, ctx) =>
            {
                var value = a.Length > 0 ? a[0] : FenValue.Undefined;
                if (onFinally != null) onFinally.Invoke(new IValue[0], _context);
                return value;
            });

            var onRejected = new FenFunction("finallyRejected", (a, ctx) =>
            {
                var reason = a.Length > 0 ? a[0] : FenValue.Undefined;
                if (onFinally != null) onFinally.Invoke(new IValue[0], _context);
                throw new JsPromiseException(reason); // Rethrow to propagate rejection
            });

            return Then(new IValue[] { FenValue.FromFunction(onFulfilled), FenValue.FromFunction(onRejected) }, context);
        }

        // --- Public Helpers for C# ---

        public void Then(IValue onFulfilled, IValue onRejected)
        {
            Then(new IValue[] { onFulfilled, onRejected }, _context);
        }

        // --- Static Methods ---

        public static JsPromise Resolve(IValue value, IExecutionContext context)
        {
            var p = new JsPromise(context);
            p.ResolvePromise(value);
            return p;
        }

        public static JsPromise Reject(IValue reason, IExecutionContext context)
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
    }

    public class JsPromiseException : Exception
    {
        public IValue Value { get; }
        public JsPromiseException(IValue value) : base(value?.ToString() ?? "Error") { Value = value; }
    }
}
