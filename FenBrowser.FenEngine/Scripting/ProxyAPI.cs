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
        public static FenValue CreateProxyConstructor()
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

        public static FenValue CreateProxyGlobal()
        {
            return CreateProxyConstructor();
        }

        public static FenValue CreateProxy(FenValue target, FenValue handler)
        {
            if (!target.IsObject && !target.IsFunction)
            {
                throw new Errors.FenInternalError("Proxy target must be object/function");
            }

            if (!handler.IsObject)
            {
                throw new Errors.FenInternalError("Proxy handler must be an object");
            }

            if (target.IsFunction)
            {
                var targetFunc = target.AsFunction();
                var proxyFunc = new FenFunction(targetFunc?.Name ?? "ProxyFunc", (pArgs, pThis) =>
                {
                    return targetFunc.Invoke(pArgs, null, pThis);
                })
                {
                    ProxyTarget = target,
                    ProxyHandler = handler,
                    IsConstructor = targetFunc?.IsConstructor ?? true,
                    IsAsync = targetFunc?.IsAsync ?? false,
                    IsGenerator = targetFunc?.IsGenerator ?? false
                };

                proxyFunc.SetDirect("__target__", target);
                proxyFunc.SetDirect("__proxyTarget__", target);
                proxyFunc.SetDirect("__handler__", handler);
                proxyFunc.SetDirect("__proxyHandler__", handler);
                proxyFunc.SetDirect("__isProxy__", FenValue.FromBoolean(true));

                if (targetFunc != null)
                {
                    var targetProto = targetFunc.GetPrototype();
                    if (targetProto != null)
                    {
                        proxyFunc.SetPrototype(targetProto);
                    }

                    var functionPrototypeValue = targetFunc.Get("prototype");
                    if (functionPrototypeValue.IsObject || functionPrototypeValue.IsFunction)
                    {
                        proxyFunc.Set("prototype", functionPrototypeValue);
                    }
                }

                return FenValue.FromFunction(proxyFunc);
            }

            var targetObj = target.AsObject();
            var handlerObj = handler.AsObject();
            var proxyObj = new FenObject();
            if (targetObj?.GetPrototype() != null)
            {
                proxyObj.SetPrototype(targetObj.GetPrototype());
            }

            proxyObj.SetDirect("__proxyTarget__", target);
            proxyObj.SetDirect("__target__", target);
            proxyObj.SetDirect("__handler__", handler);
            proxyObj.SetDirect("__proxyHandler__", handler);

            var getTrap = handlerObj.Get("get");
            var setTrap = handlerObj.Get("set");
            var hasTrap = handlerObj.Get("has");

            if (getTrap.IsFunction) proxyObj.SetDirect("__proxyGet__", getTrap);
            if (setTrap.IsFunction) proxyObj.SetDirect("__proxySet__", setTrap);
            if (hasTrap.IsFunction) proxyObj.SetDirect("__proxyHas__", hasTrap);
            proxyObj.SetDirect("__isProxy__", FenValue.FromBoolean(true));

            return FenValue.FromObject(proxyObj);
        }
    }
}
