using System;
using System.Collections.Generic;
using FenBrowser.FenEngine.Core.Types;

namespace FenBrowser.FenEngine.Core.Bytecode
{
    /// <summary>
    /// Represents a compiled block of bytecode instructions, capturing the executable bytes,
    /// constant pool, and relevant lexical environment metadata.
    /// </summary>
    public class CodeBlock
    {
        public byte[] Instructions { get; }
        public List<FenValue> Constants { get; }
        
        // Maps instruction offsets to original source source line numbers for errors/stack traces
        public Dictionary<int, int> SourceLineMap { get; }

        public CodeBlock(byte[] instructions, List<FenValue> constants)
        {
            Instructions = instructions ?? new byte[0];
            Constants = constants ?? new List<FenValue>();
            SourceLineMap = new Dictionary<int, int>();
        }
    }
}
