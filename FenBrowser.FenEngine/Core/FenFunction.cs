using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;
using FenBrowser.Core.Engine; // Phase enum

namespace FenBrowser.FenEngine.Core
{
    /// <summary>
    /// Represents a JavaScript function in FenEngine.
    /// Updated to use FenValue structs for high-performance execution.
    /// </summary>
    public class FenFunction : FenObject
    {
        public string Name { get; }
        public Func<FenValue[], FenValue, FenValue> NativeImplementation { get; }
        public bool IsNative { get; }
        public bool IsAsync { get; set; }
        public bool IsGenerator { get; set; }
        public bool IsArrowFunction { get; set; }
        
        // JIT Support
        public int CallCount { get; set; }
        public bool IsJitCompiled { get; set; }
        public Jit.FenJittedDelegate JittedDelegate { get; set; }
        public Dictionary<string, int> LocalMap { get; set; } // NEW: Maps names to indices

        public List<Identifier> Parameters { get; }
        public AstNode Body { get; }
        public FenEnvironment Env { get; }
        public FenObject Prototype { get; set; }
        
        public List<(string name, bool isPrivate, bool isStatic, Expression initializer)> FieldDefinitions { get; set; }

        // Proxy Support
        public FenValue ProxyHandler { get; set; }
        public FenValue ProxyTarget { get; set; }

        public FenFunction(string name, Func<FenValue[], FenValue, FenValue> nativeImplementation)
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
            Name = "anonymous";
        }

        public FenFunction(List<Identifier> parameters, AstNode body, FenEnvironment env)
        {
            Parameters = parameters;
            Body = body;
            Env = env;
            IsNative = false;
            Name = "arrow";
        }

        public FenValue Invoke(FenValue[] args, IExecutionContext context)
        {
            // PROXY TRAP: Apply
            if (!ProxyHandler.IsUndefined && ProxyHandler.IsObject)
            {
                EnginePhaseManager.AssertNotInPhase(EnginePhase.Measure, EnginePhase.Layout, EnginePhase.Paint);
                
                var handlerObj = ProxyHandler.AsObject();
                var trap = handlerObj.Get("apply");
                if (trap.IsFunction)
                {
                    var thisVal = context?.ThisBinding ?? FenValue.Undefined;
                    var argsArray = new FenObject(); 
                    for(int i=0; i<args.Length; i++) argsArray.Set(i.ToString(), args[i]);
                    argsArray.Set("length", FenValue.FromNumber(args.Length));

                    return trap.AsFunction().Invoke(new FenValue[] 
                    { 
                        ProxyTarget.Type != Interfaces.ValueType.Undefined ? ProxyTarget : FenValue.FromObject(this), 
                        thisVal, 
                        FenValue.FromObject(argsArray) 
                    }, context);
                }
            }

            if (IsNative)
            {
                try
                {
                    var thisVal = context != null ? context.ThisBinding : FenValue.Undefined;
                    return NativeImplementation(args, thisVal);
                }
                catch (Exception ex)
                {
                    return FenValue.FromString($"Error: {ex.Message}");
                }
            }

            // User-defined functions are handled by the Interpreter.ApplyFunction
            return FenValue.Undefined;
        }
    }
}
