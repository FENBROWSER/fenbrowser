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
        private string _name;
        /// <summary>Function name — stored as an explicit {writable:false, enumerable:false, configurable:true} property.</summary>
        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                StoreFunctionNameProperty(value);
            }
        }

        public string Source { get; set; } // ES2019: Store original source code
        public Func<FenValue[], FenValue, FenValue> NativeImplementation { get; }
        public bool IsNative { get; }
        public bool IsAsync { get; set; }
        public bool IsGenerator { get; set; }
        public bool IsArrowFunction { get; set; }
        public bool IsMethodDefinition { get; set; }
        public bool NeedsArgumentsObject { get; set; } = true;
        public IObject HomeObject { get; set; }

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
        /// Whether this function can be called with `new`. Defaults to true for user-defined
        /// functions, false for native prototype methods and non-constructor built-ins.
        /// </summary>
        public bool IsConstructor { get; set; } = true;

        /// <summary>
        /// Internal slot for bound functions: the callable target passed to bind().
        /// </summary>
        public FenFunction BoundTargetFunction { get; set; }

        /// <summary>
        /// Whether invocation must create the inner function-name binding used by
        /// declarations and explicit named function expressions.
        /// Inferred names only affect the observable `.name` property and must not
        /// shadow outer lexical bindings.
        /// </summary>
        public bool HasOwnNameBinding { get; set; }

        private int _nativeLength = -1;
        /// <summary>
        /// Explicit length for native functions. -1 means compute from Parameters.Count.
        /// Setting this updates the stored 'length' property descriptor.
        /// </summary>
        public int NativeLength
        {
            get => _nativeLength;
            set
            {
                _nativeLength = value;
                StoreFunctionLengthProperty();
            }
        }

        /// <summary>
        /// Default prototype for function objects (Function.prototype).
        /// Set by FenRuntime after Function.prototype is created so that
        /// user-defined functions inherit .call(), .apply(), .bind() etc.
        /// </summary>
        public static IObject DefaultFunctionPrototype { get; set; }

        public FenFunction(string name, Func<FenValue[], FenValue, FenValue> nativeImplementation)
        {
            NativeImplementation = nativeImplementation;
            IsNative = true;
            Name = name; // setter stores name property + triggers StoreFunctionLengthProperty via fallback
            StoreFunctionLengthProperty(); // Parameters is null for native, NativeLength=-1 -> length=0
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
            CompileAstBodyToBytecode();
            Name = string.Empty; // setter stores name property
            StoreFunctionLengthProperty(); // Parameters.Count -> length
            if (DefaultFunctionPrototype != null && !ReferenceEquals(DefaultFunctionPrototype, this))
                SetPrototype(DefaultFunctionPrototype);
        }

        public FenFunction(List<Identifier> parameters, Bytecode.CodeBlock bytecodeBlock, FenEnvironment env)
        {
            Parameters = parameters;
            BytecodeBlock = bytecodeBlock;
            Env = env;
            IsNative = false;
            Name = string.Empty; // setter stores name property
            StoreFunctionLengthProperty();
            if (DefaultFunctionPrototype != null && !ReferenceEquals(DefaultFunctionPrototype, this))
                SetPrototype(DefaultFunctionPrototype);
        }

        public FenFunction(List<Identifier> parameters, AstNode body, FenEnvironment env)
        {
            Parameters = parameters;
            Body = body;
            Env = env;
            IsNative = false;
            CompileAstBodyToBytecode();
            Name = string.Empty; // setter stores name property
            StoreFunctionLengthProperty();
            if (DefaultFunctionPrototype != null && !ReferenceEquals(DefaultFunctionPrototype, this))
                SetPrototype(DefaultFunctionPrototype);
        }

        /// <summary>
        /// Store 'name' as an explicit ES-spec-compliant property descriptor:
        /// {writable: false, enumerable: false, configurable: true}.
        /// </summary>
        private void StoreFunctionNameProperty(string name)
        {
            DefineOwnProperty("name", new PropertyDescriptor
            {
                Value = FenValue.FromString(name ?? ""),
                Writable = false,
                Enumerable = false,
                Configurable = true
            });
        }

        private void CompileAstBodyToBytecode()
        {
            if (IsNative || Body == null || BytecodeBlock != null)
            {
                return;
            }

            BytecodeBlock = Bytecode.Compiler.BytecodeCompiler.CompileCallableFunctionBody(
                Parameters,
                Body,
                Name,
                forceStrictRoot: false,
                out var localMap,
                out var needsArgumentsObject);

            LocalMap = localMap;
            NeedsArgumentsObject = needsArgumentsObject;
        }

        /// <summary>
        /// Store 'length' as an explicit ES-spec-compliant property descriptor:
        /// {writable: false, enumerable: false, configurable: true}.
        /// </summary>
        private void StoreFunctionLengthProperty()
        {
            int len = _nativeLength >= 0 ? _nativeLength : (Parameters?.Count ?? 0);
            DefineOwnProperty("length", new PropertyDescriptor
            {
                Value = FenValue.FromNumber(len),
                Writable = false,
                Enumerable = false,
                Configurable = true
            });
        }

        /// <summary>
        /// Override Get to fall back to DefaultFunctionPrototype for built-in functions
        /// created before DefaultFunctionPrototype was initialized (they got Object.prototype
        /// in their chain instead of Function.prototype, so call/apply/bind are missing).
        /// </summary>
        public override FenValue Get(string key, IExecutionContext context = null)
        {
            var result = base.Get(key, context);
            // Fallback: built-in functions created before DefaultFunctionPrototype was set
            // have Object.prototype (not Function.prototype) in their chain, so they miss
            // call/apply/bind etc. For properly-initialized functions, base.Get() already
            // finds the method via Function.prototype so result won't be undefined here.
            if (result.IsUndefined && DefaultFunctionPrototype != null
                && !ReferenceEquals(DefaultFunctionPrototype, this))
            {
                var dfpResult = DefaultFunctionPrototype.Get(key, context);
                if (!dfpResult.IsUndefined) return dfpResult;
            }
            return result;
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
                    var argsArray = new FenObject();
                    for (int i = 0; i < args.Length; i++) argsArray.Set(i.ToString(), args[i]);
                    argsArray.Set("length", FenValue.FromNumber(args.Length));

                    return trap.AsFunction().Invoke(new FenValue[]
                    {
                        ProxyTarget.Type != Interfaces.ValueType.Undefined ? ProxyTarget : FenValue.FromObject(this),
                        actualThis,
                        FenValue.FromObject(argsArray)
                    }, context, FenValue.FromObject(handlerObj));
                }
            }

            if (IsNative)
            {
                if (OwningRuntime != null)
                {
                    return OwningRuntime.RunWithRealmActivation(() => NativeImplementation(args, actualThis));
                }

                return NativeImplementation(args, actualThis);
            }

            if (BytecodeBlock == null)
            {
                throw new InvalidOperationException("Non-native FenFunction must be bytecode-backed before invocation.");
            }

            if (!IsAsync && !IsGenerator)
            {
                return InvokeViaDirectBytecode(args, context, actualThis);
            }

            return InvokeViaBytecodeThunk(args, context, actualThis);
        }

        private FenValue InvokeViaDirectBytecode(FenValue[] args, IExecutionContext context, FenValue thisBinding)
        {
            var baseEnv = Env ?? context?.Environment as FenEnvironment ?? new FenEnvironment();
            var newEnv = new FenEnvironment(baseEnv);
            if (BytecodeBlock != null && BytecodeBlock.IsStrict)
            {
                newEnv.StrictMode = true;
            }

            InitializeFastStore(newEnv);
            if (!IsArrowFunction)
            {
                if (HasOwnNameBinding && !string.IsNullOrEmpty(Name))
                {
                    SetBinding(newEnv, Name, FenValue.FromFunction(this));
                }

                SetBinding(newEnv, "this", thisBinding);
            }

            BindArguments(newEnv, args);

            var vm = new Bytecode.VM.VirtualMachine();
            return vm.Execute(BytecodeBlock, newEnv);
        }

        private FenValue InvokeViaBytecodeThunk(FenValue[] args, IExecutionContext context, FenValue thisBinding)
        {
            var constants = new List<FenValue>(3);
            var instructionBytes = new List<byte>(32);

            var baseEnv = Env ?? context?.Environment as FenEnvironment ?? new FenEnvironment();
            FenFunction callable = this;

            constants.Add(thisBinding);
            constants.Add(FenValue.FromFunction(callable));
            constants.Add(FenValue.FromObject(CreateArrayLikeArgumentsObject(args)));

            AppendLoadConst(instructionBytes, 0);
            AppendLoadConst(instructionBytes, 1);
            AppendLoadConst(instructionBytes, 2);
            instructionBytes.Add((byte)Bytecode.OpCode.CallMethodFromArray);
            instructionBytes.Add((byte)Bytecode.OpCode.Return);

            var thunk = new Bytecode.CodeBlock(instructionBytes.ToArray(), constants);
            var vm = new Bytecode.VM.VirtualMachine();
            return vm.Execute(thunk, baseEnv);
        }

        private void InitializeFastStore(FenEnvironment env)
        {
            if (env == null || LocalMap == null || LocalMap.Count == 0)
            {
                return;
            }

            env.InitializeFastStore(LocalMap.Count);
            env.ConfigureFastSlots(LocalMap);
        }

        private void SetBinding(FenEnvironment env, string name, FenValue value)
        {
            env.Set(name, value);
            if (LocalMap != null && LocalMap.TryGetValue(name, out int localSlot))
            {
                env.SetFast(localSlot, value);
            }
        }

        private void BindArguments(FenEnvironment env, FenValue[] args)
        {
            var effectiveArgs = args ?? Array.Empty<FenValue>();

            if (!IsArrowFunction && NeedsArgumentsObject)
            {
                var argumentsObj = new FenObject
                {
                    InternalClass = "Arguments"
                };

                for (int i = 0; i < effectiveArgs.Length; i++)
                {
                    argumentsObj.Set(i.ToString(), effectiveArgs[i]);
                }

                argumentsObj.Set("length", FenValue.FromNumber(effectiveArgs.Length));
                argumentsObj.Set("callee", FenValue.FromFunction(this));

                var paramNames = new FenObject();
                if (Parameters != null)
                {
                    for (int i = 0; i < Parameters.Count && i < effectiveArgs.Length; i++)
                    {
                        var parameter = Parameters[i];
                        if (parameter == null || parameter.IsRest || string.IsNullOrEmpty(parameter.Value))
                        {
                            continue;
                        }

                        paramNames.Set(i.ToString(), FenValue.FromString(parameter.Value));
                    }
                }

                argumentsObj.Set("__paramNames__", FenValue.FromObject(paramNames));
                SetBinding(env, "arguments", FenValue.FromObject(argumentsObj));
            }

            if (Parameters == null)
            {
                return;
            }

            for (int i = 0; i < Parameters.Count; i++)
            {
                var parameter = Parameters[i];
                if (parameter == null || string.IsNullOrEmpty(parameter.Value))
                {
                    continue;
                }

                if (parameter.IsRest)
                {
                    var restArray = FenObject.CreateArray();
                    int restIndex = 0;
                    for (int j = i; j < effectiveArgs.Length; j++)
                    {
                        restArray.Set(restIndex.ToString(), effectiveArgs[j]);
                        restIndex++;
                    }

                    restArray.Set("length", FenValue.FromNumber(restIndex));
                    SetBinding(env, parameter.Value, FenValue.FromObject(restArray));
                    break;
                }

                var argValue = i < effectiveArgs.Length ? effectiveArgs[i] : FenValue.Undefined;
                SetBinding(env, parameter.Value, argValue);
            }
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

