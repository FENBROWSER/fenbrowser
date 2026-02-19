using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Represents a FetchEvent (dispatched on ServiceWorkerGlobalScope).
    /// </summary>
    public class FetchEvent : FenObject
    {
        private readonly IObject _request;
        private readonly IExecutionContext _context;
        private readonly List<FenObject> _lifetimePromises = new();
        private readonly TaskCompletionSource<bool> _respondWithRegistered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FetchEvent(string type, IObject request, IExecutionContext context) 
        {
            Set("type", FenValue.FromString(type));
            Set("request", FenValue.FromObject(request));
            _request = request;
            _context = context;

            Set("respondWith", FenValue.FromFunction(new FenFunction("respondWith", RespondWith)));
            Set("waitUntil", FenValue.FromFunction(new FenFunction("waitUntil", WaitUntil)));
        }

        // Response promise set by respondWith
        public FenObject RespondWithPromise { get; private set; }
        public bool HasRespondWith => RespondWithPromise != null;

        private FenValue RespondWith(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1) return FenValue.Undefined;
            
            var promise = args[0].AsObject();
            if (promise != null)
            {
                RespondWithPromise = promise as FenObject;
                _respondWithRegistered.TrySetResult(true);
            }
            return FenValue.Undefined;
        }

        public async Task<bool> WaitForRespondWithRegistrationAsync(TimeSpan timeout)
        {
            if (HasRespondWith)
            {
                return true;
            }

            var completed = await Task.WhenAny(_respondWithRegistered.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != _respondWithRegistered.Task)
            {
                return false;
            }

            return await _respondWithRegistered.Task.ConfigureAwait(false);
        }

        public async Task<RespondWithSettlement> WaitForRespondWithSettlementAsync(TimeSpan timeout)
        {
            if (!HasRespondWith || RespondWithPromise == null)
            {
                return RespondWithSettlement.NotHandled();
            }

            if (TryGetLegacySettledState(RespondWithPromise, out var legacy))
            {
                return legacy;
            }

            var settle = new TaskCompletionSource<RespondWithSettlement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!TryAttachPromiseSettlementHandlers(RespondWithPromise, settle))
            {
                return RespondWithSettlement.NotHandled();
            }

            var completed = await Task.WhenAny(settle.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != settle.Task)
            {
                return RespondWithSettlement.Timeout();
            }

            return await settle.Task.ConfigureAwait(false);
        }

        private bool TryAttachPromiseSettlementHandlers(FenObject promise, TaskCompletionSource<RespondWithSettlement> settle)
        {
            if (promise == null)
            {
                return false;
            }

            if (promise is JsPromise jsPromise)
            {
                jsPromise.Then(
                    FenValue.FromFunction(new FenFunction("swRespondWithFulfilled", (a, _) =>
                    {
                        settle.TrySetResult(RespondWithSettlement.Fulfilled(a.Length > 0 ? a[0] : FenValue.Undefined));
                        return FenValue.Undefined;
                    })),
                    FenValue.FromFunction(new FenFunction("swRespondWithRejected", (a, _) =>
                    {
                        settle.TrySetResult(RespondWithSettlement.Rejected(a.Length > 0 ? a[0] : FenValue.Undefined));
                        return FenValue.Undefined;
                    })));
                return true;
            }

            var then = promise.Get("then");
            if (!then.IsFunction)
            {
                return false;
            }

            var resolveFn = FenValue.FromFunction(new FenFunction("swRespondWithFulfilled", (a, _) =>
            {
                settle.TrySetResult(RespondWithSettlement.Fulfilled(a.Length > 0 ? a[0] : FenValue.Undefined));
                return FenValue.Undefined;
            }));

            var rejectFn = FenValue.FromFunction(new FenFunction("swRespondWithRejected", (a, _) =>
            {
                settle.TrySetResult(RespondWithSettlement.Rejected(a.Length > 0 ? a[0] : FenValue.Undefined));
                return FenValue.Undefined;
            }));

            then.AsFunction().Invoke(new[] { resolveFn, rejectFn }, _context);
            return true;
        }

        private static bool TryGetLegacySettledState(FenObject promise, out RespondWithSettlement settlement)
        {
            settlement = default;
            if (promise == null)
            {
                return false;
            }

            var stateValue = promise.Get("__state");
            if (stateValue.IsUndefined)
            {
                return false;
            }

            var state = stateValue.ToString();
            if (string.Equals(state, "fulfilled", StringComparison.OrdinalIgnoreCase))
            {
                settlement = RespondWithSettlement.Fulfilled(promise.Get("__result"));
                return true;
            }

            if (string.Equals(state, "rejected", StringComparison.OrdinalIgnoreCase))
            {
                settlement = RespondWithSettlement.Rejected(promise.Get("__reason"));
                return true;
            }

            return false;
        }

        private FenValue WaitUntil(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1)
            {
                return FenValue.Undefined;
            }

            var promise = args[0].AsObject() as FenObject;
            if (promise != null)
            {
                _lifetimePromises.Add(promise);
            }

            return FenValue.Undefined;
        }

        public async Task<bool> WaitForLifetimePromisesAsync(TimeSpan timeout)
        {
            if (_lifetimePromises.Count == 0)
            {
                return true;
            }

            var waits = new List<Task<RespondWithSettlement>>(_lifetimePromises.Count);
            foreach (var promise in _lifetimePromises)
            {
                waits.Add(WaitForPromiseSettlementAsync(promise, timeout));
            }

            var all = Task.WhenAll(waits);
            var completed = await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != all)
            {
                return false;
            }

            var settlements = await all.ConfigureAwait(false);
            foreach (var settlement in settlements)
            {
                if (settlement.IsRejected || settlement.IsTimeout)
                {
                    return false;
                }
            }

            return true;
        }

        private async Task<RespondWithSettlement> WaitForPromiseSettlementAsync(FenObject promise, TimeSpan timeout)
        {
            if (promise == null)
            {
                return RespondWithSettlement.NotHandled();
            }

            if (TryGetLegacySettledState(promise, out var legacy))
            {
                return legacy;
            }

            var settle = new TaskCompletionSource<RespondWithSettlement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!TryAttachPromiseSettlementHandlers(promise, settle))
            {
                return RespondWithSettlement.NotHandled();
            }

            var completed = await Task.WhenAny(settle.Task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed != settle.Task)
            {
                return RespondWithSettlement.Timeout();
            }

            return await settle.Task.ConfigureAwait(false);
        }
    }

    public readonly struct RespondWithSettlement
    {
        public bool IsHandled { get; }
        public bool IsSettled { get; }
        public bool IsFulfilled { get; }
        public bool IsRejected { get; }
        public bool IsTimeout { get; }
        public FenValue Value { get; }

        private RespondWithSettlement(
            bool isHandled,
            bool isSettled,
            bool isFulfilled,
            bool isRejected,
            bool isTimeout,
            FenValue value)
        {
            IsHandled = isHandled;
            IsSettled = isSettled;
            IsFulfilled = isFulfilled;
            IsRejected = isRejected;
            IsTimeout = isTimeout;
            Value = value;
        }

        public static RespondWithSettlement NotHandled() =>
            new(false, false, false, false, false, FenValue.Undefined);

        public static RespondWithSettlement Timeout() =>
            new(true, false, false, false, true, FenValue.Undefined);

        public static RespondWithSettlement Fulfilled(FenValue value) =>
            new(true, true, true, false, false, value);

        public static RespondWithSettlement Rejected(FenValue reason) =>
            new(true, true, false, true, false, reason);
    }
}
