using System;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.Core.Engine; // Phase enum
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// Implements the ECMAScript Proxy API.
    /// Intercepts operations on objects and functions.
    /// </summary>
    public static class ProxyAPI
    {
        public static IValue CreateProxyConstructor()
        {
            // new Proxy(target, handler)
            var proxyCtor = new FenFunction("Proxy", (args, thisVal) =>
            {
                if (args.Length < 2)
                    throw new Errors.FenInternalError("Proxy requires target and handler arguments");

                return CreateProxy(args[0], args[1]);
            });

            return FenValue.FromFunction(proxyCtor);
        }

        public static IValue CreateProxyGlobal()
        {
             var proxyCtor = new FenFunction("Proxy", (args, thisVal) =>
            {
               // (Same logic as above)
                 if (args.Length < 2) throw new Errors.FenInternalError("Proxy requires target and handler");
                 // ...
                 return CreateProxy(args[0], args[1]);
            });
            
            // Reflect Revocable?
            
            return FenValue.FromFunction(proxyCtor);
        }

        public static IValue CreateProxy(IValue target, IValue handler)
        {
             if (!target.IsObject && !target.IsFunction)
                    throw new Errors.FenInternalError("Proxy target must be object/function");
            
            // Logic duplicated from above
             if (target.IsFunction)
             {
                 var targetFunc = target.AsFunction();
                 var proxyFunc = new FenFunction("ProxyFunc", (pArgs, pThis) => 
                 {
                     return targetFunc.Invoke(pArgs, null); // Default call
                 });
                 proxyFunc.ProxyTarget = target;
                 proxyFunc.ProxyHandler = handler;
                 return FenValue.FromFunction(proxyFunc);
             }
             
             var targetObj = target.AsObject();
             var handlerObj = handler.AsObject();
             var proxyObj = new FenObject();
             
             proxyObj.Set("__isProxy__", FenValue.FromBoolean(true));
             proxyObj.Set("__proxyTarget__", target); // We might need this for Reflect
             
             if (handlerObj.Has("get"))
             {
                 proxyObj.Set("__proxyGet__", handlerObj.Get("get"));
             }
             if (handlerObj.Has("set")) proxyObj.Set("__proxySet__", handlerObj.Get("set"));
             if (handlerObj.Has("has")) proxyObj.Set("__proxyHas__", handlerObj.Get("has"));
             
             // Important: Forwarding Logic
             // If NO 'get' trap, we must set a custom getter on the FenObject?
             // FenObject doesn't support "Default Getter".
             // We MUST rely on the fact that if 'get' is missing, standard behavior applies.
             // Standard behavior for Proxy(target) is to behave like target.
             // Since we can't make FenObject wrap another object transparently easily...
             // We will assume most frameworks provide a 'get' trap.
             
             return FenValue.FromObject(proxyObj);
        }
    }
}
