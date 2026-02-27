using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Types;
using FenValue = FenBrowser.FenEngine.Core.FenValue;

namespace FenBrowser.FenEngine.Core.Bytecode.VM
{
    /// <summary>
    /// A lightweight, heap-allocated frame for the Virtual Machine.
    /// Prevents CLR StackOverflowException by managing the AST depth manually.
    /// </summary>
    public struct ExceptionHandler
    {
        public int CatchOffset { get; }
        public int FinallyOffset { get; }
        public int StackBase { get; }

        public ExceptionHandler(int catchOffset, int finallyOffset, int stackBase)
        {
            CatchOffset = catchOffset;
            FinallyOffset = finallyOffset;
            StackBase = stackBase;
        }
    }

    public class CallFrame
    {
        public CodeBlock Block { get; private set; }
        
        /// <summary>
        /// Instruction Pointer (index in Block.Instructions)
        /// </summary>
        public int IP { get; set; }

        /// <summary>
        /// Lexical Environment for the function
        /// </summary>
        public FenEnvironment Environment { get; private set; }
        
        /// <summary>
        /// Stack base pointer to restore the VM stack to exactly the right height on return
        /// </summary>
        public int StackBase { get; private set; }

        private Stack<ExceptionHandler> _exceptionHandlers;
        private Stack<FenEnvironment> _withEnvironments;
        private Dictionary<string, FenEnvironment> _bindingEnvironmentCache;
        public Stack<ExceptionHandler> ExceptionHandlers
        {
            get
            {
                if (_exceptionHandlers == null)
                    _exceptionHandlers = new Stack<ExceptionHandler>();
                return _exceptionHandlers;
            }
        }
        
        public bool HasExceptionHandlers => _exceptionHandlers != null && _exceptionHandlers.Count > 0;
        public Stack<FenEnvironment> WithEnvironments
        {
            get
            {
                if (_withEnvironments == null)
                    _withEnvironments = new Stack<FenEnvironment>();
                return _withEnvironments;
            }
        }

        public bool HasWithEnvironments => _withEnvironments != null && _withEnvironments.Count > 0;

        public bool IsConstruct { get; set; }
        public FenObject ConstructedObject { get; set; }
        public FenValue NewTarget { get; set; }
        public bool IsAsyncFunction { get; set; }

        public CallFrame(CodeBlock block, FenEnvironment env, int stackBase)
        {
            Reset(block, env, stackBase);
        }

        public void Reset(CodeBlock block, FenEnvironment env, int stackBase)
        {
            Block = block;
            Environment = env;
            StackBase = stackBase;
            IP = 0;
            IsConstruct = false;
            ConstructedObject = null;
            NewTarget = FenValue.Undefined;
            IsAsyncFunction = false;
            if (_exceptionHandlers != null)
            {
                _exceptionHandlers.Clear();
            }
            if (_withEnvironments != null)
            {
                _withEnvironments.Clear();
            }
            if (_bindingEnvironmentCache != null)
            {
                _bindingEnvironmentCache.Clear();
            }
        }

        public void SetEnvironment(FenEnvironment environment)
        {
            Environment = environment;
            if (_bindingEnvironmentCache != null)
            {
                _bindingEnvironmentCache.Clear();
            }
        }

        public bool TryGetCachedBindingEnvironment(string name, out FenEnvironment environment)
        {
            if (_bindingEnvironmentCache != null)
            {
                return _bindingEnvironmentCache.TryGetValue(name, out environment);
            }

            environment = null;
            return false;
        }

        public void CacheBindingEnvironment(string name, FenEnvironment environment)
        {
            if (_bindingEnvironmentCache == null)
            {
                _bindingEnvironmentCache = new Dictionary<string, FenEnvironment>();
            }

            _bindingEnvironmentCache[name] = environment;
        }

        public void RemoveCachedBindingEnvironment(string name)
        {
            if (_bindingEnvironmentCache != null)
            {
                _bindingEnvironmentCache.Remove(name);
            }
        }

        public void ClearBindingEnvironmentCache()
        {
            if (_bindingEnvironmentCache != null)
            {
                _bindingEnvironmentCache.Clear();
            }
        }
    }
}
