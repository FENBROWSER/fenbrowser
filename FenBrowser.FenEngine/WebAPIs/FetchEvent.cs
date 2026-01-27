using System;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// Represents a FetchEvent (dispatched on ServiceWorkerGlobalScope).
    /// </summary>
    public class FetchEvent : FenObject
    {
        private readonly IObject _request;
        private readonly IExecutionContext _context;

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

        private FenValue RespondWith(FenValue[] args, FenValue thisVal)
        {
            if (args.Length < 1) return FenValue.Undefined;
            
            var promise = args[0].AsObject();
            if (promise != null)
            {
                RespondWithPromise = promise as FenObject;
            }
            return FenValue.Undefined;
        }

        private FenValue WaitUntil(FenValue[] args, FenValue thisVal)
        {
            // TODO: Track lifetime extension
            return FenValue.Undefined;
        }
    }
}
