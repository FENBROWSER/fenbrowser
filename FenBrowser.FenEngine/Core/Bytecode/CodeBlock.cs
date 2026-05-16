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
        public bool IsStrict { get; set; } = false;

        /// <summary>
        /// Annex B §B.3.3.1: Names of block-scoped function declarations in eval code that must be
        /// pre-initialized in the enclosing variable-scope environment before eval executes.
        /// Only populated on CodeBlocks compiled with <c>isEval: true</c>.
        /// </summary>
        public List<string> AnnexBBlockFunctionNames { get; set; }

        public CodeBlock(byte[] instructions, List<FenValue> constants, List<string> localSlotNames = null)
        {
            Instructions = instructions ?? new byte[0];
            Constants = constants ?? new List<FenValue>();
            LocalSlotNames = localSlotNames ?? new List<string>();
            SourceLineMap = new Dictionary<int, int>();
        }

        /// <summary>
        /// Property load/store inline caches, keyed by instruction offset. Owned directly
        /// (no ConditionalWeakTable) to save one hash lookup per LoadProp/StoreProp dispatch.
        /// Allocated lazily on first miss.
        /// </summary>
        internal object LoadPropertyInlineCacheStorage;
        internal object StorePropertyInlineCacheStorage;

        /// <summary>
        /// Variable inline cache, keyed by instruction offset of a LoadVar opcode. Stores the
        /// resolved binding environment + slot so subsequent calls skip the full env-chain walk
        /// performed by ResolveVariable. Validity is checked by env-identity walk against the
        /// current frame; mismatches fall through to the slow path (polymorphic closure context).
        /// </summary>
        internal object LoadVarInlineCacheStorage;

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
