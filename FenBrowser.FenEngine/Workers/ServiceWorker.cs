using System;
using System.Collections.Generic;
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
        private readonly Dictionary<string, List<FenValue>> _eventListeners = new(StringComparer.OrdinalIgnoreCase);

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
            Set("onstatechange", FenValue.Null);

            Set("postMessage", FenValue.FromFunction(new FenFunction("postMessage", PostMessage)));
            Set("addEventListener", FenValue.FromFunction(new FenFunction("addEventListener", AddEventListener)));
            Set("removeEventListener", FenValue.FromFunction(new FenFunction("removeEventListener", RemoveEventListener)));
            Set("dispatchEvent", FenValue.FromFunction(new FenFunction("dispatchEvent", DispatchEvent)));
        }

        public void UpdateState(string newState)
        {
            State = newState;
            Set("state", FenValue.FromString(State));
            DispatchStateChange();
        }

        private FenValue PostMessage(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1) return FenValue.Undefined;
            
            var message = args[0].ToNativeObject(); // Structured clone happening in runtime
            var transfer = args.Length > 1 ? args[1] : FenValue.Undefined;

            // Forward to the actual worker runtime via ServiceWorkerManager
            // For now, we need a way to reference the runtime. 
            // We'll delegate to the Manager using the Scope as key.
            ServiceWorkerManager.Instance.PostMessageToWorker(Scope, message);
             
            return FenValue.Undefined;
        }

        private FenValue AddEventListener(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2 || !args[0].IsString || !args[1].IsFunction)
            {
                return FenValue.Undefined;
            }

            var type = args[0].ToString();
            if (!_eventListeners.TryGetValue(type, out var listeners))
            {
                listeners = new List<FenValue>();
                _eventListeners[type] = listeners;
            }

            listeners.Add(args[1]);
            return FenValue.Undefined;
        }

        private FenValue RemoveEventListener(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 2 || !args[0].IsString)
            {
                return FenValue.Undefined;
            }

            var type = args[0].ToString();
            if (_eventListeners.TryGetValue(type, out var listeners))
            {
                var callback = args[1];
                listeners.RemoveAll(existing => SameCallback(existing, callback));
            }

            return FenValue.Undefined;
        }

        private FenValue DispatchEvent(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1 || !args[0].IsObject)
            {
                return FenValue.FromBoolean(false);
            }

            var evt = args[0].AsObject();
            var type = evt?.Has("type") == true ? evt.Get("type").ToString() : string.Empty;
            if (string.IsNullOrWhiteSpace(type))
            {
                return FenValue.FromBoolean(false);
            }

            DispatchEventToHandlers(type, args[0]);
            return FenValue.FromBoolean(true);
        }

        private void DispatchStateChange()
        {
            var evt = new FenObject();
            evt.Set("type", FenValue.FromString("statechange"));
            evt.Set("target", FenValue.FromObject(this));
            evt.Set("state", FenValue.FromString(State));
            DispatchEventToHandlers("statechange", FenValue.FromObject(evt));
        }

        private void DispatchEventToHandlers(string eventType, FenValue eventObject)
        {
            var propertyHandler = Get($"on{eventType}");
            if (propertyHandler.IsFunction)
            {
                propertyHandler.AsFunction().Invoke(new[] { eventObject }, null);
            }

            if (_eventListeners.TryGetValue(eventType, out var listeners))
            {
                var snapshot = listeners.ToArray();
                foreach (var listener in snapshot)
                {
                    if (listener.IsFunction)
                    {
                        listener.AsFunction().Invoke(new[] { eventObject }, null);
                    }
                }
            }
        }

        private static bool SameCallback(FenValue left, FenValue right)
        {
            if (!left.IsFunction || !right.IsFunction)
            {
                return false;
            }

            if (ReferenceEquals(left.AsObject(), right.AsObject()))
            {
                return true;
            }

            return string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal);
        }
    }
}
