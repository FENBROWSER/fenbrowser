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
        public Func<IValue[], IValue> NativeImplementation { get; }
        public bool IsNative { get; }

        // User-defined function properties
        public List<Identifier> Parameters { get; }
        public BlockStatement Body { get; }
        public FenEnvironment Env { get; }

        public FenFunction(string name, Func<IValue[], IValue> nativeImplementation)
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

        public IValue Invoke(IValue[] args, IExecutionContext context)
        {
            if (IsNative)
            {
                try
                {
                    return NativeImplementation(args);
                }
                catch (Exception ex)
                {
                    // Convert native exceptions to FenError
                    throw new Errors.FenInternalError($"Error executing native function {Name}: {ex.Message}", ex);
                }
            }
            
            // User-defined functions are executed by the Interpreter directly
            return FenValue.Undefined;
        }
    }
}
