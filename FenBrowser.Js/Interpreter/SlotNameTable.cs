using System.Runtime.CompilerServices;
using FenBrowser.Js.Bytecode;

namespace FenBrowser.Js.Interpreter;

// Reverse-map cache from BytecodeFunction.VariableSlots (name -> slot) into a slot-indexed
// array of names. Used by the env-record shim in BytecodeInterpreter to translate a
// slot-based LoadVar/StoreVar opcode into the name-based 9.1.1.1 abstract operations.
//
// The map is keyed off the BytecodeFunction instance so the table is built at most once
// per function and is released for collection together with the function itself.
public static class SlotNameTable
{
    private static readonly ConditionalWeakTable<BytecodeFunction, string?[]> _cache = new();

    public static string? GetName(BytecodeFunction function, int slot)
    {
        ArgumentNullException.ThrowIfNull(function);

        var names = _cache.GetValue(function, Build);
        if ((uint)slot >= (uint)names.Length)
        {
            return null;
        }

        return names[slot];
    }

    private static string?[] Build(BytecodeFunction function)
    {
        var max = -1;
        foreach (var kv in function.VariableSlots)
        {
            if (kv.Value > max)
            {
                max = kv.Value;
            }
        }

        if (max < 0)
        {
            return Array.Empty<string?>();
        }

        var arr = new string?[max + 1];
        foreach (var kv in function.VariableSlots)
        {
            // Multiple names mapped to the same slot is not expected from the compiler;
            // if it ever happens, last-writer-wins keeps the table deterministic and the
            // env-record shim simply falls back to the VariableStore path for that name.
            arr[kv.Value] = kv.Key;
        }

        return arr;
    }
}
