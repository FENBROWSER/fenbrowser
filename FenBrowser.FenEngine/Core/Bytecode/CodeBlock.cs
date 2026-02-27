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
        public IReadOnlyList<string> LocalSlotNames { get; }
        public int LocalSlotCount => LocalSlotNames?.Count ?? 0;
        
        // Maps instruction offsets to original source source line numbers for errors/stack traces
        public Dictionary<int, int> SourceLineMap { get; }

        public CodeBlock(byte[] instructions, List<FenValue> constants, List<string> localSlotNames = null)
        {
            Instructions = instructions ?? new byte[0];
            Constants = constants ?? new List<FenValue>();
            LocalSlotNames = localSlotNames ?? new List<string>();
            SourceLineMap = new Dictionary<int, int>();
        }

        public string GetLocalSlotName(int slotIndex)
        {
            if ((uint)slotIndex >= (uint)LocalSlotCount)
            {
                return null;
            }

            return LocalSlotNames[slotIndex];
        }
    }
}
