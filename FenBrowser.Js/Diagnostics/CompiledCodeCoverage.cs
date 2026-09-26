using System.Threading;

namespace FenBrowser.Js.Diagnostics;

/// <summary>
/// How much of what the compiler produced is ever entered.
/// </summary>
/// <remarks>
/// The compiler is eager: every function in a script is parsed and compiled to
/// bytecode when the script is compiled, whether or not it is ever called. For a
/// page that is one hand-written file that costs nothing worth measuring. For a
/// shipped bundle it is most of the work.
/// <para>
/// youtube.com's main bundle, 10.8M characters, measured 2026-09-17:
/// <c>107,594 functions compiled, 257 entered (0.2%)</c>, and
/// <c>5,493,972 instructions compiled, 1,364,979 reachable (24.8%)</c>. Compiling
/// it took 2,248 ms. That is the case for compiling a function body on first call
/// instead - the numbers here are how it gets re-checked afterwards.
/// </para>
/// <para>
/// Instruction counts are static, not dynamic: each function contributes its
/// instruction count once, the first time it is entered. So they measure how much
/// of the emitted bytecode is reachable at all, not how often it runs.
/// </para>
/// Off unless <c>FEN_JS_COMPILED_CODE_COVERAGE=1</c>; the counters are process-wide
/// and are meant for a one-shot measurement run, not for production accounting.
/// </remarks>
public static class CompiledCodeCoverage
{
    private static int _compiledFunctions;
    private static int _enteredFunctions;
    private static long _compiledInstructions;
    private static long _reachableInstructions;

    /// <summary>Whether the counters are being kept.</summary>
    public static bool Enabled { get; } =
        System.Environment.GetEnvironmentVariable("FEN_JS_COMPILED_CODE_COVERAGE") == "1";

    public static int CompiledFunctions => Volatile.Read(ref _compiledFunctions);

    public static int EnteredFunctions => Volatile.Read(ref _enteredFunctions);

    public static long CompiledInstructions => Interlocked.Read(ref _compiledInstructions);

    public static long ReachableInstructions => Interlocked.Read(ref _reachableInstructions);

    public static void RecordCompiled(int instructionCount)
    {
        Interlocked.Increment(ref _compiledFunctions);
        Interlocked.Add(ref _compiledInstructions, instructionCount);
    }

    /// <summary>Counts <paramref name="function"/> as entered, the first time it is.</summary>
    internal static void RecordEntry(FenBrowser.Js.Bytecode.BytecodeFunction function)
    {
        if (function.CoverageEntryRecorded)
        {
            return;
        }

        function.CoverageEntryRecorded = true;
        Interlocked.Increment(ref _enteredFunctions);
        Interlocked.Add(ref _reachableInstructions, function.Instructions.Count);
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _compiledFunctions, 0);
        Interlocked.Exchange(ref _enteredFunctions, 0);
        Interlocked.Exchange(ref _compiledInstructions, 0);
        Interlocked.Exchange(ref _reachableInstructions, 0);
    }

    public static string Describe()
    {
        int compiled = CompiledFunctions;
        int entered = EnteredFunctions;
        long compiledOps = CompiledInstructions;
        long reachableOps = ReachableInstructions;

        static double Percent(double part, double whole) => whole == 0 ? 0 : 100.0 * part / whole;

        return $"functions compiled={compiled} entered={entered} " +
               $"({Percent(entered, compiled):F1}% used); " +
               $"instructions compiled={compiledOps} reachable={reachableOps} " +
               $"({Percent(reachableOps, compiledOps):F1}% reachable)";
    }
}
