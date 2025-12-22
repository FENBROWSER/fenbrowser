using System;
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

        private IValue Update(IValue[] args, IValue thisVal)
        {
            // Simple promise wrapper
            var promise = new FenObject(); 
            // TODO: Trigger update logic via Manager
            return FenValue.FromObject(promise); 
        }

        private IValue Unregister(IValue[] args, IValue thisVal)
        {
             // TODO: Trigger unregister logic via Manager
             // Returns Promise<boolean>
             var promise = new FenObject();
             return FenValue.FromObject(promise);
        }
    }
}
