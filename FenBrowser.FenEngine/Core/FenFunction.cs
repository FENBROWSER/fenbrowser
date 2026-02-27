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
        public string Name { get; set; }
        public string Source { get; set; } // ES2019: Store original source code
        public Func<FenValue[], FenValue, FenValue> NativeImplementation { get; }
        public bool IsNative { get; }
        public bool IsAsync { get; set; }
        public bool IsGenerator { get; set; }
        public bool IsArrowFunction { get; set; }
        public bool NeedsArgumentsObject { get; set; } = true;
        
        // JIT Support
        public int CallCount { get; set; }
        public bool IsJitCompiled { get; set; }
        public Jit.FenJittedDelegate JittedDelegate { get; set; }
        public Dictionary<string, int> LocalMap { get; set; } // NEW: Maps names to indices

        public List<Identifier> Parameters { get; }
        public AstNode Body { get; }
        public Bytecode.CodeBlock BytecodeBlock { get; set; }
        public FenEnvironment Env { get; }
        public FenObject Prototype { get; set; }
        
        public List<(string name, bool isPrivate, bool isStatic, Expression initializer)> FieldDefinitions { get; set; }

        // Proxy Support
        public FenValue ProxyHandler { get; set; }
        public FenValue ProxyTarget { get; set; }

        /// <summary>
        /// Default prototype for function objects (Function.prototype).
        /// Set by FenRuntime after Function.prototype is created so that
        /// user-defined functions inherit .call(), .apply(), .bind() etc.
        /// </summary>
        public static IObject DefaultFunctionPrototype { get; set; }

        public FenFunction(string name, Func<FenValue[], FenValue, FenValue> nativeImplementation)
        {
            Name = name;
            NativeImplementation = nativeImplementation;
            IsNative = true;
            // Functions inherit from Function.prototype (which inherits from Object.prototype)
            if (DefaultFunctionPrototype != null && !ReferenceEquals(DefaultFunctionPrototype, this))
                SetPrototype(DefaultFunctionPrototype);
        }

        public FenFunction(List<Identifier> parameters, BlockStatement body, FenEnvironment env)
        {
            Parameters = parameters;
            Body = body;
            Env = env;
            IsNative = false;
            Name = "anonymous";
            if (DefaultFunctionPrototype != null && !ReferenceEquals(DefaultFunctionPrototype, this))
                SetPrototype(DefaultFunctionPrototype);
        }

        public FenFunction(List<Identifier> parameters, Bytecode.CodeBlock bytecodeBlock, FenEnvironment env)
        {
            Parameters = parameters;
            BytecodeBlock = bytecodeBlock;
            Env = env;
            IsNative = false;
            Name = "anonymous_bytecode";
            if (DefaultFunctionPrototype != null && !ReferenceEquals(DefaultFunctionPrototype, this))
                SetPrototype(DefaultFunctionPrototype);
        }

        public FenFunction(List<Identifier> parameters, AstNode body, FenEnvironment env)
        {
            Parameters = parameters;
            Body = body;
            Env = env;
            IsNative = false;
            Name = "arrow";
            if (DefaultFunctionPrototype != null && !ReferenceEquals(DefaultFunctionPrototype, this))
                SetPrototype(DefaultFunctionPrototype);
        }

        /// <summary>
        /// Override Get to expose 'name' and 'length' as standard function properties
        /// without requiring every FenFunction to explicitly set them.
        /// </summary>
        public override FenValue Get(string key, IExecutionContext context = null)
        {
            // 'name' — return the C# Name field if no explicit 'name' property was set
            if (key == "name")
            {
                var explicitName = base.Get("name", context);
                if (!explicitName.IsUndefined) return explicitName;
                return FenValue.FromString(Name ?? "");
            }
            // 'length' — return parameter count for user-defined, 0 for native unless overridden
            if (key == "length")
            {
                var explicitLen = base.Get("length", context);
                if (!explicitLen.IsUndefined) return explicitLen;
                int paramCount = Parameters?.Count ?? 0;
                return FenValue.FromNumber(paramCount);
            }
            return base.Get(key, context);
        }

        public FenValue Invoke(FenValue[] args, IExecutionContext context, FenValue? thisArg = null)
        {
            // PROXY TRAP: Apply
            if (context == null)
            {
                context = new ExecutionContext();
                if (Env != null) context.Environment = Env;
                else context.Environment = new FenEnvironment(null); // Global fallback?    
                context.ThisBinding = FenValue.Undefined;
            }

            var actualThis = thisArg ?? (context?.ThisBinding ?? FenValue.Undefined);

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
                    return NativeImplementation(args, actualThis);
                }
                catch (Exception ex)
                {
                    return FenValue.FromString($"Error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }

            if (BytecodeBlock == null)
            {
                return FenValue.FromError("Bytecode-only mode: AST-backed function invocation is not supported.");
            }

            try
            {
                return InvokeViaBytecodeThunk(args, context, actualThis);
            }
            catch (Exception ex)
            {
                return FenValue.FromError($"Error: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private FenValue InvokeViaBytecodeThunk(FenValue[] args, IExecutionContext context, FenValue thisBinding)
        {
            var constants = new List<FenValue>(2);
            var instructionBytes = new List<byte>(32);

            var baseEnv = Env ?? context?.Environment as FenEnvironment ?? new FenEnvironment();
            FenFunction callable = this;

            // Bytecode call opcodes currently resolve `this` through function lexical env.
            // Rebind env for direct host-side invoke so callback paths still observe supplied `this`.
            if (!IsArrowFunction)
            {
                var reboundEnv = new FenEnvironment(baseEnv);
                reboundEnv.Set("this", thisBinding);

                callable = new FenFunction(Parameters, BytecodeBlock, reboundEnv)
                {
                    Name = Name,
                    IsArrowFunction = IsArrowFunction,
                    IsAsync = IsAsync,
                    IsGenerator = IsGenerator,
                    NeedsArgumentsObject = NeedsArgumentsObject,
                    LocalMap = LocalMap
                };
            }

            constants.Add(FenValue.FromFunction(callable));
            constants.Add(FenValue.FromObject(CreateArrayLikeArgumentsObject(args)));

            AppendLoadConst(instructionBytes, 0);
            AppendLoadConst(instructionBytes, 1);
            instructionBytes.Add((byte)Bytecode.OpCode.CallFromArray);
            instructionBytes.Add((byte)Bytecode.OpCode.Return);

            var thunk = new Bytecode.CodeBlock(instructionBytes.ToArray(), constants);
            var vm = new Bytecode.VM.VirtualMachine();
            return vm.Execute(thunk, baseEnv);
        }

        private static FenObject CreateArrayLikeArgumentsObject(FenValue[] args)
        {
            var arr = new FenObject();
            var effectiveArgs = args ?? Array.Empty<FenValue>();
            for (int i = 0; i < effectiveArgs.Length; i++)
            {
                arr.Set(i.ToString(), effectiveArgs[i]);
            }

            arr.Set("length", FenValue.FromNumber(effectiveArgs.Length));
            return arr;
        }

        private static void AppendLoadConst(List<byte> bytes, int index)
        {
            bytes.Add((byte)Bytecode.OpCode.LoadConst);
            var raw = BitConverter.GetBytes(index);
            bytes.AddRange(raw);
        }
    }
}
