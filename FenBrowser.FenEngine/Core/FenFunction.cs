using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Interfaces;

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
        public bool IsAsync { get; set; } // New property for async functions

        // User-defined function properties
        public List<Identifier> Parameters { get; }
        public AstNode Body { get; }  // Can be BlockStatement or Expression (arrow functions)
        public FenEnvironment Env { get; }
        public FenObject Prototype { get; set; } // For classes/constructors

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
