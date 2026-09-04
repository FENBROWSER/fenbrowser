using System.Runtime.CompilerServices;
using System.Text;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Bytecode;

/// <summary>
/// Structural identity for a compiled function.
///
/// Minified bundles compile almost entirely to functions whose <see
/// cref="BytecodeFunction.Name"/> is empty, and <see
/// cref="BytecodeFunction.SourceText"/> is null for everything the compiler
/// could not recover original text for, so GC and rooting diagnostics had
/// nothing to print but "&lt;anonymous&gt;" — the same label for thousands of
/// distinct functions, which identifies none of them.
///
/// The shape of a function identifies it well enough to find again: its
/// opcode stream, the property names it touches, the string constants it
/// carries, and its parameter/register counts. All of that is fixed at
/// compile time, so the signature is stable across runs of the same script
/// and comparable across the realms of one page.
/// </summary>
public static class BytecodeFunctionSignature
{
    private const int MaxOpcodePreview = 8;
    private const int MaxNamePreview = 4;
    private const int MaxConstantPreview = 3;
    private const int MaxConstantChars = 24;
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    // Describing a function walks its whole instruction stream, and the
    // diagnostics that want a description ask for the same function on every
    // allocation of a closure over it. Compute once per compiled function and
    // let it die with the function.
    private static readonly ConditionalWeakTable<BytecodeFunction, string> DescriptionCache = new();

    /// <summary>
    /// A 64-bit structural fingerprint. Two <see cref="BytecodeFunction"/>
    /// instances compiled from the same source text hash equal, including
    /// across two realms that each compiled the bundle separately — which is
    /// what makes a handle traced by the wrong heap recognisable.
    /// </summary>
    public static ulong Hash(BytecodeFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);

        var hash = FnvOffsetBasis;
        MixValue(ref hash, (ulong)(int)function.Kind);
        MixValue(ref hash, function.IsStrictMode ? 1UL : 0UL);
        MixValue(ref hash, (ulong)function.ParameterNames.Count);
        MixValue(ref hash, (ulong)function.RegisterCount);
        MixValue(ref hash, (ulong)function.Instructions.Count);
        MixValue(ref hash, (ulong)function.NestedFunctions.Count);

        var instructions = function.Instructions;
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            MixValue(ref hash, (ulong)(int)instruction.OpCode);
            MixValue(ref hash, unchecked((ulong)(long)instruction.A));
            MixValue(ref hash, unchecked((ulong)(long)instruction.B));
            MixValue(ref hash, unchecked((ulong)(long)instruction.C));
            MixValue(ref hash, unchecked((ulong)(long)instruction.D));
            MixValue(ref hash, unchecked((ulong)(long)instruction.E));
        }

        for (var i = 0; i < function.PropertyNames.Count; i++)
        {
            MixString(ref hash, function.PropertyNames[i]);
        }

        for (var i = 0; i < function.ParameterNames.Count; i++)
        {
            MixString(ref hash, function.ParameterNames[i]);
        }

        var constants = function.Constants;
        for (var i = 0; i < constants.Count; i++)
        {
            var constant = constants[i];
            if (constant.Tag == JsValueTag.String)
            {
                MixString(ref hash, constant.AsString());
            }
        }

        return hash;
    }

    /// <summary>
    /// A one-line description built from <see cref="Hash"/> plus the parts of
    /// the shape a human can match against a bundle: the leading opcodes, the
    /// property names the body reads or writes, and its first string
    /// constants. Cached per function.
    /// </summary>
    public static string Describe(BytecodeFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return DescriptionCache.GetValue(function, static fn => BuildDescription(fn));
    }

    /// <summary>
    /// <see cref="Describe"/> for a function that may be absent, so callers
    /// holding an object that is not a compiled-function closure do not each
    /// repeat the null check.
    /// </summary>
    public static string DescribeOrUnknown(BytecodeFunction? function) =>
        function is null ? "<no-bytecode-function>" : Describe(function);

    private static string BuildDescription(BytecodeFunction function)
    {
        var builder = new StringBuilder(160);
        builder.Append("fn#").Append(Hash(function).ToString("x16"));

        builder.Append('[');
        builder.Append(string.IsNullOrEmpty(function.Name) ? "anon" : function.Name);
        builder.Append(']');

        builder.Append(' ').Append(function.Kind);
        if (function.IsStrictMode)
        {
            builder.Append(" strict");
        }

        builder.Append(" p=").Append(function.ParameterNames.Count)
            .Append(" r=").Append(function.RegisterCount)
            .Append(" i=").Append(function.Instructions.Count)
            .Append(" n=").Append(function.NestedFunctions.Count);

        AppendOpcodePreview(builder, function);
        AppendNamePreview(builder, " props=", function.PropertyNames);
        AppendNamePreview(builder, " params=", function.ParameterNames);
        AppendConstantPreview(builder, function);

        // A recoverable source text is still the best identification there is;
        // include it when the compiler managed to keep one.
        if (!string.IsNullOrEmpty(function.SourceText))
        {
            builder.Append(" src=");
            AppendEscaped(builder, function.SourceText, 60);
        }

        return builder.ToString();
    }

    private static void AppendOpcodePreview(StringBuilder builder, BytecodeFunction function)
    {
        var instructions = function.Instructions;
        if (instructions.Count == 0)
        {
            return;
        }

        builder.Append(" ops=");
        var count = Math.Min(MaxOpcodePreview, instructions.Count);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(instructions[i].OpCode);
        }

        if (instructions.Count > count)
        {
            builder.Append(",…");
        }
    }

    private static void AppendNamePreview(StringBuilder builder, string label, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return;
        }

        builder.Append(label);
        var count = Math.Min(MaxNamePreview, names.Count);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            AppendEscaped(builder, names[i], MaxConstantChars);
        }

        if (names.Count > count)
        {
            builder.Append(",…");
        }
    }

    private static void AppendConstantPreview(StringBuilder builder, BytecodeFunction function)
    {
        var constants = function.Constants;
        var written = 0;
        for (var i = 0; i < constants.Count && written < MaxConstantPreview; i++)
        {
            var constant = constants[i];
            if (constant.Tag != JsValueTag.String)
            {
                continue;
            }

            var text = constant.AsString();
            if (text.Length == 0)
            {
                continue;
            }

            builder.Append(written == 0 ? " consts=\"" : "\",\"");
            AppendEscaped(builder, text, MaxConstantChars);
            written++;
        }

        if (written > 0)
        {
            builder.Append('"');
        }
    }

    // Signatures land in single-line log records, so newlines, tabs and quotes
    // in a constant would break the line they are meant to identify.
    private static void AppendEscaped(StringBuilder builder, string value, int maxLength)
    {
        var length = Math.Min(value.Length, maxLength);
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            builder.Append(c is '\r' or '\n' or '\t' or '"' || char.IsControl(c) ? ' ' : c);
        }

        if (value.Length > length)
        {
            builder.Append('…');
        }
    }

    private static void MixString(ref ulong hash, string? value)
    {
        if (value is null)
        {
            MixValue(ref hash, 0UL);
            return;
        }

        for (var i = 0; i < value.Length; i++)
        {
            hash = (hash ^ value[i]) * FnvPrime;
        }

        // Separator, so ["ab","c"] and ["a","bc"] do not collide.
        hash = (hash ^ 0xFF) * FnvPrime;
    }

    private static void MixValue(ref ulong hash, ulong value)
    {
        for (var shift = 0; shift < 64; shift += 8)
        {
            hash = (hash ^ ((value >> shift) & 0xFF)) * FnvPrime;
        }
    }
}
