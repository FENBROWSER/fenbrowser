using System;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Workers
{
    /// <summary>
    /// Represents a Service Worker object.
    /// https://w3c.github.io/ServiceWorker/#serviceworker-interface
    /// </summary>
    public class ServiceWorker : FenObject
    {
        public string ScriptURL { get; private set; }
        public string Scope { get; private set; }
        public string State { get; private set; } // installing, installed, activating, activated, redundant

        public ServiceWorker(string scriptURL, string scope, string state)
        {
            ScriptURL = scriptURL;
            Scope = scope;
            State = state;
            InitializeInterface();
        }

        private void InitializeInterface()
        {
            Set("scriptURL", FenValue.FromString(ScriptURL));
            
            // State is a getter
            // For simplicity in this phase, we'll update the property and the JS value manually or via a setter hook if needed.
            // Using a simple value for now.
            Set("state", FenValue.FromString(State));

            Set("postMessage", FenValue.FromFunction(new FenFunction("postMessage", PostMessage)));
        }

        public void UpdateState(string newState)
        {
            State = newState;
            Set("state", FenValue.FromString(State));
            // TODO: Dispatch 'statechange' event
        }

        private FenValue PostMessage(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1) return FenValue.Undefined;
            
            var message = args[0].ToObject(); // Structured clone happening in runtime
            var transfer = args.Length > 1 ? args[1] : FenValue.Undefined;

            // Forward to the actual worker runtime via ServiceWorkerManager
            // For now, we need a way to reference the runtime. 
            // We'll delegate to the Manager using the Scope as key.
            ServiceWorkerManager.Instance.PostMessageToWorker(Scope, message);
            
            return FenValue.Undefined;
        }
    }
}
