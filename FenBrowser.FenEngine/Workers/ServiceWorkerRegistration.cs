using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Represents a ServiceWorkerRegistration object.
    /// https://w3c.github.io/ServiceWorker/#serviceworkerregistration-interface
    /// </summary>
    public class ServiceWorkerRegistration : FenObject
    {
        public string Scope { get; private set; }
        public ServiceWorker Installing { get; private set; }
        public ServiceWorker Waiting { get; private set; }
        public ServiceWorker Active { get; private set; }

        private readonly IExecutionContext _context;

        public ServiceWorkerRegistration(string scope, IExecutionContext context = null)
        {
            Scope = scope;
            _context = context;
            InitializeInterface();
        }

        private void InitializeInterface()
        {
            Set("scope", FenValue.FromString(Scope));
            Set("installing", FenValue.Null);
            Set("waiting", FenValue.Null);
            Set("active", FenValue.Null);

            Set("update", FenValue.FromFunction(new FenFunction("update", Update)));
            Set("unregister", FenValue.FromFunction(new FenFunction("unregister", Unregister)));
        }

        public void SetInstalling(ServiceWorker worker)
        {
            Installing = worker;
            Set("installing", worker != null ? FenValue.FromObject(worker) : FenValue.Null);
        }

        public void SetWaiting(ServiceWorker worker)
        {
            Waiting = worker;
            Set("waiting", worker != null ? FenValue.FromObject(worker) : FenValue.Null);
        }

        public void SetActive(ServiceWorker worker)
        {
            Active = worker;
            Set("active", worker != null ? FenValue.FromObject(worker) : FenValue.Null);
        }

        private FenValue Update(FenValue[] args, FenValue thisVal)
        {
            return FenValue.FromObject(CreatePromise(async () =>
            {
                var updated = await ServiceWorkerManager.Instance.UpdateRegistrationAsync(Scope).ConfigureAwait(false);
                return FenValue.FromObject(updated);
            }));
        }

        private FenValue Unregister(FenValue[] args, FenValue thisVal)
        {
             return FenValue.FromObject(CreatePromise(async () =>
             {
                 var removed = await ServiceWorkerManager.Instance.UnregisterAsync(Scope).ConfigureAwait(false);
                 return FenValue.FromBoolean(removed);
             }));
        }

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
                    FenBrowser.Core.FenLogger.Warn($"[ServiceWorkerRegistration] Detached async operation failed: {ex.Message}",
                        FenBrowser.Core.Logging.LogCategory.ServiceWorker);
                }
            }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }

        private FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            if (_context != null)
            {
                FenValue capturedResolve = FenValue.Undefined;
                FenValue capturedReject = FenValue.Undefined;
                var executor = new FenFunction("executor", (args, thisVal) =>
                {
                    capturedResolve = args.Length > 0 ? args[0] : FenValue.Undefined;
                    capturedReject = args.Length > 1 ? args[1] : FenValue.Undefined;
                    return FenValue.Undefined;
                });
                var jsPromise = new JsPromise(FenValue.FromFunction(executor), _context);
                _ = RunDetachedAsync(async () =>
                {
                    try
                    {
                        var value = await valueFactory().ConfigureAwait(false);
                        if (capturedResolve.IsFunction)
                            capturedResolve.AsFunction().Invoke(new[] { value }, _context);
                    }
                    catch (Exception ex)
                    {
                        if (capturedReject.IsFunction)
                            capturedReject.AsFunction().Invoke(new[] { FenValue.FromString(ex.Message) }, _context);
                    }
                });
                return jsPromise;
            }

            // Fallback: hand-rolled promise
            var promise = new FenObject();
            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    var result = await valueFactory().ConfigureAwait(false);
                    promise.Set("__state", FenValue.FromString("fulfilled"));
                    promise.Set("__result", result);
                    var onFulfilled = promise.Get("onFulfilled");
                    if (onFulfilled.IsFunction)
                        onFulfilled.AsFunction().Invoke(new[] { result }, null);
                }
                catch (Exception ex)
                {
                    var reason = FenValue.FromString(ex.Message);
                    promise.Set("__state", FenValue.FromString("rejected"));
                    promise.Set("__reason", reason);
                    var onRejected = promise.Get("onRejected");
                    if (onRejected.IsFunction)
                        onRejected.AsFunction().Invoke(new[] { reason }, null);
                }
            });

            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, _) =>
            {
                if (args.Length > 0 && args[0].IsFunction) promise.Set("onFulfilled", args[0]);
                if (args.Length > 1 && args[1].IsFunction) promise.Set("onRejected", args[1]);
                var state = promise.Get("__state");
                if (!state.IsUndefined && state.ToString() == "fulfilled" && args.Length > 0 && args[0].IsFunction)
                    args[0].AsFunction().Invoke(new[] { promise.Get("__result") }, null);
                else if (!state.IsUndefined && state.ToString() == "rejected" && args.Length > 1 && args[1].IsFunction)
                    args[1].AsFunction().Invoke(new[] { promise.Get("__reason") }, null);
                return FenValue.FromObject(promise);
            })));

            return promise;
        }
    }
}
