using System;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.FenEngine.Security;
using FenBrowser.Core.Dom; // MutationRecord

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
                var key = args[1].ToString();
                
                if (target.IsObject)
                    return target.AsObject().Get(key);
                
                // If function?
                // Functions don't support get(key) in FenEngine yet.
                
                return FenValue.Undefined;
            })));

            // Reflect.set(target, propertyKey, value[, receiver])
            reflect.Set("set", FenValue.FromFunction(new FenFunction("set", (args, thisVal) =>
            {
                if (args.Length < 3) throw new Errors.FenInternalError("Reflect.set requires target, property, value");
                var target = args[0];
                var key = args[1].ToString();
                var value = args[2];

                if (target.IsObject)
                {
                    target.AsObject().Set(key, value);
                    return FenValue.FromBoolean(true);
                }
                return FenValue.FromBoolean(false);
            })));

            // Reflect.has(target, propertyKey)
            reflect.Set("has", FenValue.FromFunction(new FenFunction("has", (args, thisVal) =>
            {
                if (args.Length < 2) throw new Errors.FenInternalError("Reflect.has requires target and property");
                var target = args[0];
                var key = args[1].ToString();

                if (target.IsObject)
                    return FenValue.FromBoolean(target.AsObject().Has(key));
                
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
                var invokeArgs = new System.Collections.Generic.List<IValue>();
                
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
            public IValue ThisBinding { get; set; }
            
            public ExecutionContextShim(IValue thisBinding) 
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
            public Func<IValue, IValue[], IValue> ExecuteFunction { get; set; }
            public IModuleLoader ModuleLoader { get; set; }
            public Action<MutationRecord> OnMutation { get; set; }
            public string CurrentUrl { get; set; } = "reflect";
            public FenEnvironment Environment { get; set; }

            public void PushCallFrame(string functionName) { }
            public void PopCallFrame() { }
            public void CheckCallStackLimit() { }
            public void CheckExecutionTimeLimit() { }
        }
    }
}
