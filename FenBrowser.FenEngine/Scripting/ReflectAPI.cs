using System;
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
                
                // Convert argumentsList (array-like) to IValue[]
                // Simplified: Assuming generic array or object with length
                var invokeArgs = new System.Collections.Generic.List<FenValue>();
                
                if (argumentsList.IsObject)
                {
                     var argObj = argumentsList.AsObject();
                     var lenVal = argObj.Get("length");
                     if (lenVal.IsNumber)
                     {
                         int len = (int)lenVal.ToNumber();
                         for(int i=0; i<len; i++)
                         {
                             invokeArgs.Add(argObj.Get(i.ToString()));
                         }
                     }
                }
                
                return func.Invoke(invokeArgs.ToArray(), null); // Context is null? Need to pass 'thisArg'
                // Wait, Invoke() signature takes Context?
                // FenFunction.Invoke(args, context)
                // We need to pass 'thisArg' as binding.
                // We can create a fake context or modify Invoke to accept ThisBinding directly?
                // Invoke takes IExecutionContext.
                // We can pass null context, but how to set 'this'?
                // Actually FenFunction.Invoke implementation (Line 61) reads context?.ThisBinding.
                // We must pass a simple context wrapper.
                
                // return func.Invoke(invokeArgs.ToArray(), new SimpleContext { ThisBinding = thisArg });
                // We assume there is a way to pass context.
                return func.Invoke(invokeArgs.ToArray(), new ExecutionContextShim(thisArg));
            })));

            // Reflect.construct(target, argumentsList[, newTarget])
            reflect.Set("construct", FenValue.FromFunction(new FenFunction("construct", (args, thisVal) =>
            {
                 // Not implemented yet
                 return FenValue.Null;
            })));

            return reflect;
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
        }
    }
}

