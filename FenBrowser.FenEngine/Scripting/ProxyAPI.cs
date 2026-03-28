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
                var funcHandlerObj = handler.AsObject();
                var proxyFunc = new FenFunction(targetFunc?.Name ?? "ProxyFunc", (pArgs, pThis) =>
                {
                    // ECMA-262 §10.5.12 [[Call]]: check for apply trap
                    var applyTrap = funcHandlerObj?.Get("apply");
                    if (applyTrap.HasValue && applyTrap.Value.IsFunction)
                    {
                        var argsArray = FenObject.CreateArray();
                        for (int i = 0; i < pArgs.Length; i++)
                        {
                            argsArray.Set(i.ToString(), pArgs[i]);
                        }
                        argsArray.Set("length", FenValue.FromNumber(pArgs.Length));
                        return applyTrap.Value.AsFunction().Invoke(
                            new[] { target, pThis, FenValue.FromObject(argsArray) },
                            null, FenValue.FromObject(funcHandlerObj));
                    }
                    // Default: forward to target function
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

            // ECMA-262 §10.5: When a trap is present, use it; otherwise create a default
            // forwarding trap that transparently delegates to the target object.
            if (getTrap.IsFunction)
            {
                proxyObj.SetDirect("__proxyGet__", getTrap);
            }
            else
            {
                // Default get: forward to target
                var defaultGet = new FenFunction("[[DefaultGet]]", (trapArgs, trapThis) =>
                {
                    var prop = trapArgs.Length > 1 ? trapArgs[1] : FenValue.Undefined;
                    if (targetObj != null)
                        return targetObj.Get(prop.ToString());
                    return FenValue.Undefined;
                });
                proxyObj.SetDirect("__proxyGet__", FenValue.FromFunction(defaultGet));
            }

            if (setTrap.IsFunction)
            {
                proxyObj.SetDirect("__proxySet__", setTrap);
            }
            else
            {
                // Default set: forward to target
                var defaultSet = new FenFunction("[[DefaultSet]]", (trapArgs, trapThis) =>
                {
                    var prop = trapArgs.Length > 1 ? trapArgs[1] : FenValue.Undefined;
                    var val = trapArgs.Length > 2 ? trapArgs[2] : FenValue.Undefined;
                    if (targetObj != null)
                        targetObj.Set(prop.ToString(), val);
                    return FenValue.FromBoolean(true);
                });
                proxyObj.SetDirect("__proxySet__", FenValue.FromFunction(defaultSet));
            }

            if (hasTrap.IsFunction)
            {
                proxyObj.SetDirect("__proxyHas__", hasTrap);
            }
            else
            {
                // Default has: forward to target
                var defaultHas = new FenFunction("[[DefaultHas]]", (trapArgs, trapThis) =>
                {
                    var prop = trapArgs.Length > 1 ? trapArgs[1] : FenValue.Undefined;
                    if (targetObj != null)
                        return FenValue.FromBoolean(targetObj.Has(prop.ToString()));
                    return FenValue.FromBoolean(false);
                });
                proxyObj.SetDirect("__proxyHas__", FenValue.FromFunction(defaultHas));
            }

            proxyObj.SetDirect("__isProxy__", FenValue.FromBoolean(true));

            return FenValue.FromObject(proxyObj);
        }
    }
}
