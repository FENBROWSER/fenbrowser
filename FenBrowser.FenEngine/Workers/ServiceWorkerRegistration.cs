using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

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

        public ServiceWorkerRegistration(string scope)
        {
            Scope = scope;
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
                    System.Diagnostics.Debug.WriteLine($"[ServiceWorker] Detached async operation failed: {ex.Message}");
                }
            }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }        private static FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            var promise = new FenObject();
            _ = RunDetachedAsync(async () =>
            {
                try
                {
                    var result = await valueFactory().ConfigureAwait(false);
                    promise.Set("__state", FenValue.FromString("fulfilled"));
                    promise.Set("__result", result);
                    if (promise.Has("onFulfilled"))
                    {
                        var cb = promise.Get("onFulfilled").AsFunction();
                        cb?.Invoke(new[] { result }, null);
                    }
                }
                catch (Exception ex)
                {
                    var reason = FenValue.FromString(ex.Message);
                    promise.Set("__state", FenValue.FromString("rejected"));
                    promise.Set("__reason", reason);
                    if (promise.Has("onRejected"))
                    {
                        var cb = promise.Get("onRejected").AsFunction();
                        cb?.Invoke(new[] { reason }, null);
                    }
                }
            });

            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, _) =>
            {
                if (args.Length > 0) promise.Set("onFulfilled", args[0]);
                if (args.Length > 1) promise.Set("onRejected", args[1]);

                var state = promise.Get("__state").ToString();
                if (string.Equals(state, "fulfilled", StringComparison.OrdinalIgnoreCase) &&
                    args.Length > 0 && args[0].IsFunction)
                {
                    args[0].AsFunction().Invoke(new[] { promise.Get("__result") }, null);
                }
                else if (string.Equals(state, "rejected", StringComparison.OrdinalIgnoreCase) &&
                         args.Length > 1 && args[1].IsFunction)
                {
                    args[1].AsFunction().Invoke(new[] { promise.Get("__reason") }, null);
                }

                return FenValue.FromObject(promise);
            })));

            return promise;
        }
    }
}

