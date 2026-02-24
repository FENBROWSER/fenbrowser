using System;
using FenBrowser.FenEngine.Core.Types;

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
        public CodeBlock Block { get; }
        
        /// <summary>
        /// Instruction Pointer (index in Block.Instructions)
        /// </summary>
        public int IP { get; set; }

        /// <summary>
        /// Lexical Environment for the function
        /// </summary>
        public FenEnvironment Environment { get; }
        
        /// <summary>
        /// Stack base pointer to restore the VM stack to exactly the right height on return
        /// </summary>
        public int StackBase { get; }

        public Stack<ExceptionHandler> ExceptionHandlers { get; }

        public CallFrame(CodeBlock block, FenEnvironment env, int stackBase)
        {
            Block = block;
            Environment = env;
            StackBase = stackBase;
            IP = 0;
            ExceptionHandlers = new Stack<ExceptionHandler>();
        }
    }
}
