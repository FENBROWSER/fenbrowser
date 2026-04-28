using System;
using System.Collections.Generic;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.Core.Dom.V2; // MutationRecord

namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// Implements the ECMAScript Reflect API.
    /// Provides methods for interceptable JavaScript operations.
    /// </summary>
    public static class ReflectAPI
    {
        public static FenObject CreateReflectObject()
        {
            var reflect = new FenObject();

            // Reflect.get(target, propertyKey[, receiver])
            reflect.Set("get", FenValue.FromFunction(new FenFunction("get", (args, thisVal) =>
            {
                if (args.Length < 2) throw new Errors.FenInternalError("Reflect.get requires target and property");
                var target = args[0];
                var key = args[1];
                var receiver = args.Length > 2 ? args[2] : target;

                if ((target.IsObject || target.IsFunction) && target.AsObject() is FenObject fenTarget)
                {
                    return key.IsSymbol
                        ? fenTarget.GetWithReceiver(key, receiver)
                        : fenTarget.GetWithReceiver(key.AsString(), receiver);
                }

                if (target.IsObject || target.IsFunction)
                {
                    return target.AsObject().Get(key.ToString());
                }
                
                // If function?
                // Functions don't support get(key) in FenEngine yet.
                
                return FenValue.Undefined;
            })));

            // Reflect.set(target, propertyKey, value[, receiver])
            reflect.Set("set", FenValue.FromFunction(new FenFunction("set", (args, thisVal) =>
            {
                if (args.Length < 3) throw new Errors.FenInternalError("Reflect.set requires target, property, value");
                var target = args[0];
                var key = args[1];
                var value = args[2];
                var receiver = args.Length > 3 ? args[3] : target;

                if ((target.IsObject || target.IsFunction) && target.AsObject() is FenObject fenTarget)
                {
                    fenTarget.SetWithReceiver(key, value, receiver);
                    return FenValue.FromBoolean(true);
                }

                if (target.IsObject || target.IsFunction)
                {
                    target.AsObject().Set(key.ToString(), value);
                    return FenValue.FromBoolean(true);
                }
                return FenValue.FromBoolean(false);
            })));

            // Reflect.has(target, propertyKey)
            reflect.Set("has", FenValue.FromFunction(new FenFunction("has", (args, thisVal) =>
            {
                if (args.Length < 2) throw new Errors.FenInternalError("Reflect.has requires target and property");
                var target = args[0];
                var key = args[1];

                if ((target.IsObject || target.IsFunction) && target.AsObject() is FenObject fenTarget)
                {
                    return FenValue.FromBoolean(fenTarget.Has(key));
                }

                if (target.IsObject || target.IsFunction)
                {
                    return FenValue.FromBoolean(target.AsObject().Has(key.ToString()));
                }
                
                return FenValue.FromBoolean(false);
            })));

            // Reflect.apply(target, thisArgument, argumentsList)
            reflect.Set("apply", FenValue.FromFunction(new FenFunction("apply", (args, thisVal) =>
            {
                if (args.Length < 3) throw new Errors.FenInternalError("Reflect.apply requires target, thisArg, args");
                var target = args[0];
                var thisArg = args[1];
                var argumentsList = args[2];

                if (!target.IsFunction) throw new Errors.FenInternalError("Reflect.apply target must be a function");

                var func = target.AsFunction();
                var invokeArgs = ReadArgumentsList(argumentsList);
                return func.Invoke(invokeArgs, new ExecutionContextShim(thisArg), thisArg);
            })));

            // Reflect.construct(target, argumentsList[, newTarget])
            reflect.Set("construct", FenValue.FromFunction(new FenFunction("construct", (args, thisVal) =>
            {
                if (args.Length < 2 || !args[0].IsFunction)
                {
                    throw new Errors.FenInternalError("Reflect.construct target must be a constructor");
                }

                var targetFunction = args[0].AsFunction();
                if (targetFunction == null)
                {
                    throw new Errors.FenInternalError("Reflect.construct target must be a constructor");
                }

                // ECMA-262 §26.1.2: target must be a constructor
                if (!targetFunction.IsConstructor)
                {
                    throw new Errors.FenTypeError("Reflect.construct target must be a constructor");
                }

                var newTargetValue = args.Length > 2 ? args[2] : args[0];
                if (!newTargetValue.IsFunction)
                {
                    throw new Errors.FenInternalError("Reflect.construct newTarget must be a constructor");
                }

                // ECMA-262 §26.1.2: newTarget must be a constructor
                var newTargetFunc = newTargetValue.AsFunction();
                if (newTargetFunc != null && !newTargetFunc.IsConstructor)
                {
                    throw new Errors.FenTypeError("Reflect.construct newTarget must be a constructor");
                }

                var argsList = ReadArgumentsList(args[1]);
                return Construct(targetFunction, argsList, newTargetValue);
            })));

            return reflect;
        }

        private static FenValue[] ReadArgumentsList(FenValue argumentsList)
        {
            var invokeArgs = new List<FenValue>();
            if (!(argumentsList.IsObject || argumentsList.IsFunction))
            {
                return invokeArgs.ToArray();
            }

            var argObj = argumentsList.AsObject();
            var lenVal = argObj.Get("length");
            if (!lenVal.IsNumber)
            {
                return invokeArgs.ToArray();
            }

            int len = (int)lenVal.ToNumber();
            for (int i = 0; i < len; i++)
            {
                invokeArgs.Add(argObj.Get(i.ToString()));
            }

            return invokeArgs.ToArray();
        }

        private static FenValue Construct(FenFunction targetFunction, FenValue[] args, FenValue newTargetValue)
        {
            if (!targetFunction.ProxyHandler.IsUndefined && targetFunction.ProxyHandler.IsObject)
            {
                var handlerObject = targetFunction.ProxyHandler.AsObject();
                var constructTrap = handlerObject.Get("construct");
                if (constructTrap.IsFunction)
                {
                    var argsArray = FenObject.CreateArray();
                    for (int i = 0; i < args.Length; i++)
                    {
                        argsArray.Set(i.ToString(), args[i]);
                    }

                    argsArray.Set("length", FenValue.FromNumber(args.Length));
                    return constructTrap.AsFunction().Invoke(
                        new[]
                        {
                            targetFunction.ProxyTarget.Type != FenBrowser.FenEngine.Core.Interfaces.ValueType.Undefined ? targetFunction.ProxyTarget : FenValue.FromFunction(targetFunction),
                            FenValue.FromObject(argsArray),
                            newTargetValue
                        },
                        new ExecutionContextShim(FenValue.Undefined) { NewTarget = newTargetValue },
                        FenValue.FromObject(handlerObject));
                }

                if (targetFunction.ProxyTarget.IsFunction && targetFunction.ProxyTarget.AsFunction() is FenFunction proxyTargetFunction)
                {
                    targetFunction = proxyTargetFunction;
                }
            }

            var constructedObject = new FenObject();
            var prototype = ResolveConstructorPrototype(newTargetValue, targetFunction);
            if (prototype != null)
            {
                constructedObject.SetPrototype(prototype);
            }

            var thisValue = FenValue.FromObject(constructedObject);
            var result = targetFunction.Invoke(
                args ?? Array.Empty<FenValue>(),
                new ExecutionContextShim(thisValue) { NewTarget = newTargetValue },
                thisValue);

            return (result.IsObject || result.IsFunction) ? result : thisValue;
        }

        private static FenObject ResolveConstructorPrototype(FenValue newTargetValue, FenFunction targetFunction)
        {
            if ((newTargetValue.IsObject || newTargetValue.IsFunction) && newTargetValue.AsObject() is FenObject newTargetObject)
            {
                var explicitPrototype = newTargetObject.Get("prototype");
                if (explicitPrototype.IsObject && explicitPrototype.AsObject() is FenObject explicitPrototypeObject)
                {
                    return explicitPrototypeObject;
                }
            }

            var targetPrototype = targetFunction.Get("prototype");
            if (targetPrototype.IsObject && targetPrototype.AsObject() is FenObject targetPrototypeObject)
            {
                return targetPrototypeObject;
            }

            return FenObject.DefaultPrototype as FenObject;
        }

        private class ExecutionContextShim : IExecutionContext
        {
            public FenValue ThisBinding { get; set; }
            
            public ExecutionContextShim(FenValue thisBinding) 
            { 
                ThisBinding = thisBinding; 
                Permissions = new Security.PermissionManager(Security.JsPermissions.AllSafe); // Internal use
                Limits = new DefaultResourceLimits();
            }

            public IPermissionManager Permissions { get; }
            public IResourceLimits Limits { get; }
            public int CallStackDepth => 0;
            public DateTime ExecutionStart => DateTime.UtcNow;
            public bool ShouldContinue => true;
            public Action RequestRender { get; private set; }
            public void SetRequestRender(Action action) { RequestRender = action; }
            public Action<Action, int> ScheduleCallback { get; set; } = (a, d) => a();
            public Action<Action> ScheduleMicrotask { get; set; } = (a) => a();
            public Func<FenValue, FenValue[], FenValue> ExecuteFunction { get; set; }
            public IModuleLoader ModuleLoader { get; set; }
            public Action<MutationRecord> OnMutation { get; set; }
            public Action<FenValue, FenObject> OnUnhandledRejection { get; set; }
            public Action<FenValue, string> OnUncaughtException { get; set; }
            public string CurrentUrl { get; set; } = "reflect";
            public FenEnvironment Environment { get; set; }
            public FenValue NewTarget { get; set; }
            public string CurrentModulePath { get; set; }
            public bool StrictMode { get; set; }

            public void PushCallFrame(string functionName) { }
            public void PopCallFrame() { }
            public void CheckCallStackLimit() { }
            public void CheckExecutionTimeLimit() { }

            public FenBrowser.FenEngine.Rendering.Core.ILayoutEngine GetLayoutEngine() => null;
            
            public FenBrowser.FenEngine.Configuration.FenEngineOptions Options { get; set; } = FenBrowser.FenEngine.Configuration.FenEngineOptions.Default;
            public System.Uri DocumentUrl { get; set; } = new System.Uri("about:blank");
        }
    }
}

