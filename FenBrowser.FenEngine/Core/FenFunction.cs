using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.Core.Engine; // Phase enum

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Represents a JavaScript function in FenEngine
    /// </summary>
    public class FenFunction
    {
        public string Name { get; }
        public Func<IValue[], IValue, IValue> NativeImplementation { get; }
        public bool IsNative { get; }
        public bool IsAsync { get; set; } // For async functions
        public bool IsGenerator { get; set; } // For generator functions (function*)
        public bool IsArrowFunction { get; set; } // Arrow functions don't have own `arguments`

        // User-defined function properties
        public List<Identifier> Parameters { get; }
        public AstNode Body { get; }  // Can be BlockStatement or Expression (arrow functions)
        public FenEnvironment Env { get; }
        public FenObject Prototype { get; set; } // For classes/constructors
        
        // Class field definitions for initialization during `new`
        // (fieldName, isPrivate, isStatic, initializer)
        public List<(string name, bool isPrivate, bool isStatic, Expression initializer)> FieldDefinitions { get; set; }

        // Proxy Support
        public IValue ProxyHandler { get; set; }
        public IValue ProxyTarget { get; set; }

        public FenFunction(string name, Func<IValue[], IValue, IValue> nativeImplementation)
        {
            Name = name;
            NativeImplementation = nativeImplementation;
            IsNative = true;
        }

        public FenFunction(List<Identifier> parameters, BlockStatement body, FenEnvironment env)
        {
            Parameters = parameters;
            Body = body;
            Env = env;
            IsNative = false;
            Name = "anonymous"; // Could be improved
        }

        // Constructor for arrow functions with expression body
        public FenFunction(List<Identifier> parameters, AstNode body, FenEnvironment env)
        {
            Parameters = parameters;
            Body = body;
            Env = env;
            IsNative = false;
            Name = "arrow";
        }

        public IValue Invoke(IValue[] args, IExecutionContext context)
        {
            // PROXY TRAP: Apply
            if (ProxyHandler != null && ProxyHandler.IsObject)
            {
                // Phase D spec 2.3: Proxy traps MUST NOT execute during Measure, Layout, or Paint
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);
                
                var handlerObj = ProxyHandler.AsObject();
                if (handlerObj.Has("apply"))
                {
                    var trap = handlerObj.Get("apply");
                    if (trap.IsFunction)
                    {
                        var thisVal = context?.ThisBinding ?? FenValue.Undefined;
                        var argsArray = new FenObject(); 
                        for(int i=0; i<args.Length; i++) argsArray.Set(i.ToString(), args[i]);
                        argsArray.Set("length", FenValue.FromNumber(args.Length));

                        // trap(target, thisArg, argumentsList)
                        return trap.AsFunction().Invoke(new IValue[] 
                        { 
                            ProxyTarget ?? FenValue.FromFunction(this), 
                            thisVal, 
                            FenValue.FromObject(argsArray) 
                        }, context);
                    }
                }
            }


            if (IsNative)
            {
                try
                {
                    var thisVal = context?.ThisBinding ?? FenValue.Undefined;
                    return NativeImplementation(args, thisVal);
                }
                catch (Exception ex)
                {
                    // Convert native exceptions to FenError
                    throw new Errors.FenInternalError($"Error executing native function {Name}: {ex.Message}", ex);
                }
            }
            
            // User-defined functions are executed by the Interpreter directly
            // But if invoked from host (e.g. Timer), we need to delegate back to interpreter
            if (context != null && context.ExecuteFunction != null)
            {
                return context.ExecuteFunction(FenValue.FromFunction(this), args);
            }

            return FenValue.Undefined;
        }
    }
}
