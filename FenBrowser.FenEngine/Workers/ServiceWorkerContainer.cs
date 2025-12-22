using System;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Represents the ServiceWorkerContainer interface (navigator.serviceWorker).
    /// </summary>
    public class ServiceWorkerContainer : FenObject
    {
        private readonly string _origin;

        public ServiceWorkerContainer(string origin)
        {
            _origin = origin;
            InitializeInterface();
        }

        private void InitializeInterface()
        {
            Set("register", FenValue.FromFunction(new FenFunction("register", Register)));
            Set("getRegistration", FenValue.FromFunction(new FenFunction("getRegistration", GetRegistration)));
            
            // controller property
            // This needs to be a getter that queries the Manager
            // For now, implementing as a method-driven property update or simplified getter-like behavior if possible.
            // Since FenObject stores values, we need to update 'controller' whenever it changes.
            // Alternatively, the JS engine binding layer would handle getters.
            Set("controller", FenValue.Null); // Updated by page logic or event loop
            
            // ready property (Promise)
            var readyPromise = new FenObject();
            Set("ready", FenValue.FromObject(readyPromise));
        }

        public void UpdateController(ServiceWorker worker)
        {
             Set("controller", worker != null ? FenValue.FromObject(worker) : FenValue.Null);
        }

        private IValue Register(IValue[] args, IValue thisVal)
        {
            if (args.Length < 1) return FenValue.Undefined; // Reject

            var scriptUrl = args[0].ToString();
            // TODO: Resolve relative URL against document base
            
            var options = args.Length > 1 ? args[1].AsObject() : null;
            var scope = options?.Get("scope")?.ToString() ?? "./"; 
            // TODO: Resolve scope

            // Return Promise
            return FenValue.FromObject(CreatePromise(async () =>
            {
                var reg = await ServiceWorkerManager.Instance.Register(scriptUrl, scope);
                return FenValue.FromObject(reg);
            }));
        }

        private IValue GetRegistration(IValue[] args, IValue thisVal)
        {
            var scope = args.Length > 0 ? args[0].ToString() : "./";
            
            return FenValue.FromObject(CreatePromise(async () => {
                 // In reality async check
                 var reg = ServiceWorkerManager.Instance.GetRegistration(scope);
                 return reg != null ? FenValue.FromObject(reg) : FenValue.Undefined;
            })); 
        }

        // --- Promise Helper (Reuse) ---
        private FenObject CreatePromise(Func<Task<IValue>> valueFactory)
        {
            var promise = new FenObject();
            Task.Run(async () =>
            {
                try
                {
                    var result = await valueFactory();
                    ResolvePromise(promise, result);
                }
                catch (Exception ex)
                {
                    RejectPromise(promise, ex.Message);
                }
            });
            SetupPromiseThen(promise);
            return promise;
        }

        private void ResolvePromise(FenObject promise, IValue result)
        {
             if (promise.Has("onFulfilled"))
             {
                 var cb = promise.Get("onFulfilled").AsFunction();
                 cb?.Invoke(new[] { result }, null);
             }
             else
             {
                 promise.Set("__result", result);
                 promise.Set("__state", FenValue.FromString("fulfilled"));
             }
        }

        private void RejectPromise(FenObject promise, string error)
        {
             if (promise.Has("onRejected"))
             {
                 var cb = promise.Get("onRejected").AsFunction();
                 cb?.Invoke(new[] { FenValue.FromString(error) }, null);
             }
             else
             {
                 promise.Set("__reason", FenValue.FromString(error));
                 promise.Set("__state", FenValue.FromString("rejected"));
             }
        }

        private void SetupPromiseThen(FenObject promise)
        {
            promise.Set("then", FenValue.FromFunction(new FenFunction("then", (args, _) =>
            {
                if (args.Length > 0) promise.Set("onFulfilled", args[0]);
                if (args.Length > 1) promise.Set("onRejected", args[1]);

                var state = promise.Get("__state")?.ToString();
                if (state == "fulfilled")
                {
                    var res = promise.Get("__result");
                    args[0]?.AsFunction()?.Invoke(new[] { res }, null);
                }
                else if (state == "rejected")
                {
                     var reason = promise.Get("__reason");
                     args[1]?.AsFunction()?.Invoke(new[] { reason }, null);
                }

                return FenValue.FromObject(promise); 
            })));
        }
    }
}
