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

        private FenObject CreatePromise(Func<Task<FenValue>> valueFactory)
        {
            return WorkerPromise.FromTask(valueFactory, _context, nameof(ServiceWorkerRegistration));
        }
    }
}
