using System.Linq.Expressions;
using System.Reflection;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;

#if !PUBLISH_AOT
namespace FenBrowser.Js.Bytecode;

// Tier 4 #24 baseline JIT.
//
// Two compilation paths share one JitDelegate slot:
//
//   1. Constant-fold (abstract interpretation): for functions whose body
//      is a closed-form sequence of arithmetic/logic/control-flow on
//      compile-time-known values, TryCompile returns a delegate that
//      simply yields the folded result. Built on the existing abstract
//      interpreter — extended over multiple commits.
//
//   2. Expression-tree codegen: for functions touching runtime state
//      (LoadVar, StoreVar, InitVar so far), TryCompile builds a
//      System.Linq.Expressions tree mirroring the interpreter's switch
//      case-by-case, then Expression.Compile() turns it into a delegate.
//      Each compiled call into the interpreter's helper methods is the
//      same code the switch dispatch would execute, just bound at
//      JIT-compile time rather than resolved per-instruction.
//
// Either path can return null to fall back to the interpreter switch.
// The IL-emit path is the multi-commit effort opened by this commit; new
// opcodes are added by extending TryEmitOpcode below.
public static class JitCompiler
{
    // How much a function must have done before it is worth compiling.
    // Compiling one costs single-digit milliseconds of Expression.Compile, and
    // the compiled body runs about a quarter faster than the dispatch loop, so
    // a function has to spend well over a hundred milliseconds interpreted
    // before the trade pays. Ten calls of a bootstrap utility is nowhere near
    // that: on reCAPTCHA's bundle it compiled ~500 functions and the page ran
    // slower overall than with the JIT switched off.
    //
    // FEN_JIT_TIERUP overrides it, so the trade can be measured against a real
    // page rather than argued about.
    //
    // Measured on google.com/recaptcha/api2/demo, one run per threshold:
    //
    //   threshold  compiled  compileMs  queueWait  neverCalled  postCompileCalls
    //         100       329      12356     276.7s     74 (22%)         2,366,847
    //        1000       116       5067      25.3s      5 (4.3%)        2,328,620
    //        5000        82       3792       4.8s      1 (1.2%)        1,970,622
    //
    // 1000 keeps 98.4% of the calls that ever reach compiled code for 41% of
    // the compile cost, and cuts the aggregate queue wait by 91% - which is
    // what a genuinely hot function actually feels, because at 100 it waited
    // behind a queue of functions that would be called a handful of times or,
    // for 74 of them, never again.
    //
    // The floor is this low only because compiled code is barely faster than
    // the interpreter. Measured on a 40-op function called 100k times:
    // interpreter 1207ms, compiled 1176ms - a 10% gain on execution against a
    // 90ms compile, which puts true break-even near 79,000 calls. Raising the
    // threshold that far would compile almost nothing; the real fix is better
    // codegen, and until then this is the knee of the curve rather than the
    // point where compilation pays for itself.
    public static readonly int TierUpThreshold =
        int.TryParse(Environment.GetEnvironmentVariable("FEN_JIT_TIERUP"), out var configured) && configured > 0
            ? configured
            : 1000;

    /// <summary>
    /// Set FEN_JIT_DISABLE=1 to keep everything on the dispatch loop. Having a
    /// switch makes a JIT change measurable against the interpreter it is
    /// supposed to beat, rather than against the last build.
    /// </summary>
    public static readonly bool Enabled =
        !string.Equals(Environment.GetEnvironmentVariable("FEN_JIT_DISABLE"), "1", StringComparison.Ordinal);

    // JIT-compiled body. Runs to completion inside the caller-set-up
    // InterpreterFrame and returns the function's return value. Throws
    // JsThrownException for uncaught exceptions, same as ExecuteInternal.
    //
    // startIp is 0 for an ordinary call. A running interpreter frame that
    // has spent long enough in a loop hands over at a loop header instead,
    // which is the only way a function that is entered once and then loops
    // for seconds can ever reach compiled code.
    public delegate JsValue JitDelegate(BytecodeInterpreter interp, InterpreterFrame frame, int startIp);

    /// <summary>
    /// Why compilation gave up, tallied by the opcode that could not be
    /// emitted. A JIT that rejects the functions a page actually runs is worth
    /// nothing however fast the ones it accepts are, so the rejection reasons
    /// matter more than the success count.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<OpCode, int> Rejections = new();

    private static void NoteRejection(OpCode op) => Rejections.AddOrUpdate(op, 1, (_, n) => n + 1);

    public static string DescribeRejections()
    {
        var ordered = Rejections.ToArray();
        Array.Sort(ordered, (x, y) => y.Value.CompareTo(x.Value));
        var parts = new List<string>();
        foreach (var entry in ordered)
        {
            parts.Add($"{entry.Key}={entry.Value}");
        }

        return parts.Count == 0 ? "none" : string.Join(" ", parts);
    }

    public static long CompileAttempts;
    public static long CompileSuccesses;
    public static long CompileExpressionTreeSuccesses;

    // Expression-tree compilation is not free and it happens on the thread that
    // was trying to run the function, so on a bundle that tiers up hundreds of
    // functions the compiler competes with the code it exists to speed up. The
    // counters said how many were compiled but never what that cost, so a run
    // could not tell a JIT that paid for itself from one that did not.
    public static long CompileTicks;

    // CompileTicks is the total; these split it, because the halves have very
    // different fixes. Building the tree is our code and can be made cheaper or
    // skipped; LambdaCompiler is not ours and can only be avoided or deferred;
    // PrepareDelegate is the CLR turning the emitted IL into machine code, which
    // is pure overhead for a function that is never called again.
    public static long TreeBuildTicks;
    public static long LambdaCompileTicks;
    public static long PrepareTicks;

    // How long a request sat in the queue before the compiler reached it. A
    // delegate that arrives after the page has stopped calling the function is
    // worth nothing no matter how fast it was to produce, and only this says
    // whether that is happening.
    public static long QueueWaitTicks;
    public static long QueuedRequests;

    public static double TreeBuildMilliseconds =>
        TreeBuildTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public static double LambdaCompileMilliseconds =>
        LambdaCompileTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public static double PrepareMilliseconds =>
        PrepareTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    public static double QueueWaitMilliseconds =>
        QueueWaitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    // Set FEN_JIT_INTERPRET=1 to ask LambdaCompiler for an interpreted
    // delegate instead of emitted IL. It compiles far faster and runs far
    // slower, which is the whole question a tiering policy has to answer.
    public static readonly bool PreferInterpretation =
        string.Equals(Environment.GetEnvironmentVariable("FEN_JIT_INTERPRET"), "1", StringComparison.Ordinal);

    public static double CompileMilliseconds =>
        CompileTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    // Compilation happens off the thread that asked for it. Building an
    // expression tree and handing it to LambdaCompiler is not cheap - on
    // google.com/recaptcha/api2/demo, 279 functions cost 9.9 seconds of a
    // 15.1-second callback - and doing it inline means the page waits for the
    // compiler before it may run the very function it wanted to speed up. The
    // function keeps running interpreted until its delegate shows up.
    //
    // Set FEN_JIT_SYNC=1 to compile inline instead, which a measurement that
    // wants a delegate to exist by a known call needs.
    public static readonly bool CompileInBackground =
        !string.Equals(Environment.GetEnvironmentVariable("FEN_JIT_SYNC"), "1", StringComparison.Ordinal);

    private static readonly System.Collections.Concurrent.BlockingCollection<BytecodeFunction> PendingCompiles = new();
    private static int _compilerStarted;

    public static long BackgroundQueueDepth => PendingCompiles.Count;

    /// <summary>
    /// Asks for <paramref name="function"/> to be compiled. The delegate is
    /// published when it is ready; callers keep interpreting until then.
    /// </summary>
    public static void RequestCompile(BytecodeFunction function)
    {
        if (function is null) return;

        if (!CompileInBackground)
        {
            Publish(function, TryCompile(function));
            return;
        }

        EnsureCompilerThread();
        try
        {
            function.CompileRequestedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            PendingCompiles.Add(function);
        }
        catch (InvalidOperationException)
        {
            // Queue completed during shutdown: staying interpreted is correct.
        }
    }

    // OsrEntryPoints is written while compiling; publishing the delegate last
    // means a reader that sees a delegate also sees the entry points that go
    // with it. Every on-stack-replacement test checks the delegate first.
    private static void Publish(BytecodeFunction function, JitDelegate? compiled) =>
        System.Threading.Volatile.Write(ref function.JitDelegate, compiled);

    private static void EnsureCompilerThread()
    {
        if (Interlocked.Exchange(ref _compilerStarted, 1) == 1) return;

        // Expression-tree compilation recurses with the shape of the function
        // it is compiling, so it gets the same large stack the JS worker runs on.
        var thread = new System.Threading.Thread(CompilerLoop, 16 * 1024 * 1024)
        {
            IsBackground = true,
            Name = "fenjs-jit",
        };
        thread.Start();
    }

    private static void CompilerLoop()
    {
        foreach (var function in PendingCompiles.GetConsumingEnumerable())
        {
            try
            {
                if (function.CompileRequestedTicks != 0)
                {
                    Interlocked.Add(
                        ref QueueWaitTicks,
                        System.Diagnostics.Stopwatch.GetTimestamp() - function.CompileRequestedTicks);
                    Interlocked.Increment(ref QueuedRequests);
                }

                Publish(function, TryCompile(function));
            }
            catch (Exception)
            {
                // A function the compiler cannot handle simply stays
                // interpreted; it must never take the page down with it.
                Publish(function, null);
            }
        }
    }

    private static void PrepareForFirstCall(JitDelegate compiled)
    {
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(compiled);
        }
        catch (Exception)
        {
            // Not every runtime will pre-JIT a dynamic method on demand. If it
            // will not, the delegate is still correct - the CLR compiles it on
            // first call as before.
        }
    }

    // Every function that reached the compiler, with what it cost and how many
    // times it was called afterwards. Compilation only pays if the delegate is
    // used, and a total compile time cannot say whether it was: a run that
    // compiles 300 functions and then calls each twice is pure loss no matter
    // how quick each compile was.
    private static readonly object CompiledLogGate = new();
    private static readonly List<(string Name, double Ms, int InvocationsAtCompile, BytecodeFunction Function)> CompiledLog = new();

    private static void NoteCompiled(BytecodeFunction function, double milliseconds)
    {
        lock (CompiledLogGate)
        {
            CompiledLog.Add((function.Name is { Length: > 0 } n ? n : "<anon>", milliseconds, function.Invocations, function));
        }
    }

    public static string Report()
    {
        var frequency = (double)System.Diagnostics.Stopwatch.Frequency;
        var text = new System.Text.StringBuilder();
        text.Append("[FenJsJit] attempts=").Append(Interlocked.Read(ref CompileAttempts))
            .Append(" compiled=").Append(Interlocked.Read(ref CompileSuccesses))
            .Append(" mode=").Append(PreferInterpretation ? "interpreted-lambda" : "emitted-il")
            .AppendLine();
        text.Append("[FenJsJit] total=").Append(CompileMilliseconds.ToString("F1"))
            .Append("ms  treeBuild=").Append(TreeBuildMilliseconds.ToString("F1"))
            .Append("ms  lambdaCompile=").Append(LambdaCompileMilliseconds.ToString("F1"))
            .Append("ms  prepareDelegate=").Append(PrepareMilliseconds.ToString("F1"))
            .Append("ms  queueWait=").Append(QueueWaitMilliseconds.ToString("F1"))
            .Append("ms over ").Append(Interlocked.Read(ref QueuedRequests)).AppendLine(" requests");

        (string Name, double Ms, int InvocationsAtCompile, BytecodeFunction Function)[] log;
        lock (CompiledLogGate) log = CompiledLog.ToArray();
        if (log.Length == 0) return text.ToString();

        // Calls made after the delegate existed are the only ones it could have
        // sped up; everything before was interpreted regardless.
        var wasted = 0;
        long usedAfter = 0;
        foreach (var entry in log)
        {
            var after = entry.Function.Invocations - entry.InvocationsAtCompile;
            usedAfter += after;
            if (after == 0) wasted++;
        }

        text.Append("[FenJsJit] neverCalledAfterCompile=").Append(wasted).Append('/').Append(log.Length)
            .Append("  postCompileInvocations=").Append(usedAfter)
            .Append("  msPerCompile=").Append((CompileMilliseconds / log.Length).ToString("F1"))
            .AppendLine();

        Array.Sort(log, static (a, b) => b.Ms.CompareTo(a.Ms));
        text.AppendLine("[FenJsJit] costliest compiles (ms, calls after compile, body ops)");
        for (var i = 0; i < log.Length && i < 8; i++)
        {
            var entry = log[i];
            text.Append("    ").Append(entry.Name.Length > 30 ? entry.Name.Substring(0, 30) : entry.Name.PadRight(30))
                .Append(entry.Ms.ToString("F1").PadLeft(8))
                .Append((entry.Function.Invocations - entry.InvocationsAtCompile).ToString().PadLeft(12))
                .Append(entry.Function.InstructionArray.Length.ToString().PadLeft(10))
                .AppendLine();
        }

        return text.ToString();
    }

    public static JitDelegate? TryCompile(BytecodeFunction function)
    {
        var compileStart = System.Diagnostics.Stopwatch.GetTimestamp();
        JitDelegate? result = null;
        try
        {
            result = TryCompileCore(function);
            return result;
        }
        finally
        {
            var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - compileStart;
            Interlocked.Add(ref CompileTicks, elapsed);
            if (result is not null)
            {
                NoteCompiled(function, elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
        }
    }

    private static JitDelegate? TryCompileCore(BytecodeFunction function)
    {
        Interlocked.Increment(ref CompileAttempts);
        if (function is null || function.Instructions.Count == 0) return null;

        // Path 1: try the cheap constant-fold first. If every register's
        // value at Return is statically known, we emit a delegate that
        // returns it directly.
        if (TryConstantFold(function) is { } folded)
        {
            // A folded body is the answer for running the function from the
            // top; there is no partial state it could be resumed into, so it
            // never advertises an on-stack entry point.
            function.OsrEntryPoints = null;
            Interlocked.Increment(ref CompileSuccesses);
            return folded;
        }

        // Path 2: Expression-tree codegen. Compiles a per-function
        // delegate that runs the same dispatch as ExecuteInternal but
        // with each opcode bound at JIT-compile time. Bails to null if
        // any opcode in the function lacks an emitter — the interpreter
        // takes over.
        var emitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var lambdaBefore = Interlocked.Read(ref LambdaCompileTicks);
        var prepareBefore = Interlocked.Read(ref PrepareTicks);
        var emitted = TryEmitExpressionTree(function);
        Interlocked.Add(
            ref TreeBuildTicks,
            System.Diagnostics.Stopwatch.GetTimestamp() - emitStart
                - (Interlocked.Read(ref LambdaCompileTicks) - lambdaBefore)
                - (Interlocked.Read(ref PrepareTicks) - prepareBefore));
        if (emitted is not null)
        {
            Interlocked.Increment(ref CompileSuccesses);
            Interlocked.Increment(ref CompileExpressionTreeSuccesses);
            return emitted;
        }

        return null;
    }

    // ---- Path 1: constant-fold abstract interpreter -----------------

    private static JitDelegate? TryConstantFold(BytecodeFunction function)
    {
        var registerValues = new JsValue?[function.RegisterCount];
        var ip = 0;
        var stepsRemaining = function.Instructions.Count * 4;

        while (stepsRemaining-- > 0)
        {
            if ((uint)ip >= (uint)function.Instructions.Count) return null;
            var ins = function.Instructions[ip];
            switch (ins.OpCode)
            {
                case OpCode.LoadConst:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (ins.B < 0 || ins.B >= function.Constants.Count) return null;
                    registerValues[ins.A] = function.Constants[ins.B];
                    ip++;
                    break;
                case OpCode.Move:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (ins.B < 0 || ins.B >= function.RegisterCount) return null;
                    if (registerValues[ins.B] is not { } srcValue) return null;
                    registerValues[ins.A] = srcValue;
                    ip++;
                    break;
                case OpCode.Add:
                case OpCode.Sub:
                case OpCode.Mul:
                case OpCode.Div:
                case OpCode.Mod:
                case OpCode.Exp:
                case OpCode.BitAnd:
                case OpCode.BitOr:
                case OpCode.BitXor:
                case OpCode.ShiftLeft:
                case OpCode.ShiftRight:
                case OpCode.UnsignedShiftRight:
                case OpCode.Eq:
                case OpCode.Neq:
                case OpCode.StrictEq:
                case OpCode.StrictNeq:
                case OpCode.Lt:
                case OpCode.Gt:
                case OpCode.Le:
                case OpCode.Ge:
                case OpCode.And:
                case OpCode.Or:
                    if (!TryFoldBinop(function, ins, registerValues, out var foldedValue))
                        return null;
                    registerValues[ins.A] = foldedValue;
                    ip++;
                    break;
                case OpCode.Not:
                case OpCode.Pos:
                case OpCode.Neg:
                case OpCode.BitNot:
                case OpCode.Void:
                case OpCode.TypeOf:
                    if (!TryFoldUnaryOp(function, ins, registerValues, out var foldedUnary))
                        return null;
                    registerValues[ins.A] = foldedUnary;
                    ip++;
                    break;
                case OpCode.Jump:
                    if (ins.A < 0 || ins.A >= function.Instructions.Count) return null;
                    ip = ins.A;
                    break;
                case OpCode.JumpIfFalse:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (ins.B < 0 || ins.B >= function.Instructions.Count) return null;
                    if (registerValues[ins.A] is not { } condValue) return null;
                    ip = IsTruthy(condValue) ? ip + 1 : ins.B;
                    break;
                case OpCode.Return:
                    if (ins.A < 0 || ins.A >= function.RegisterCount) return null;
                    if (registerValues[ins.A] is not { } returnValue) return null;
                    return (_, _, _) => returnValue;
                default:
                    return null;
            }
        }
        return null;
    }

    // ---- Path 2: Expression-tree codegen ---------------------------

    private static readonly MethodInfo MiLoadName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.LoadName), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiStoreName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.StoreName), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiInitializeName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.InitializeName), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiPreResolveBinding = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.PreResolveBinding), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiStoreToResolvedBinding = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.StoreToResolvedBinding), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiIsTruthy = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.IsTruthy), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiLoadThis = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.LoadThisForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiNewObject = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.NewObjectForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiNewArray = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.NewArrayForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiInitThisBinding = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.InitThisBindingForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiEnterScope = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.EnterScopeForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiLeaveScope = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.LeaveScopeForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiCreatePerIterationEnvironment = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CreatePerIterationEnvironment), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo FiValueTag = typeof(JsValue).GetField(nameof(JsValue.Tag))!;
    private static readonly MethodInfo MiAsNumber =
        typeof(JsValue).GetMethod(nameof(JsValue.AsNumber), Type.EmptyTypes)!;
    private static readonly MethodInfo MiAsInt32 =
        typeof(JsValue).GetMethod(nameof(JsValue.AsInt32), Type.EmptyTypes)!;
    private static readonly MethodInfo MiFromBoolean =
        typeof(JsValue).GetMethod(nameof(JsValue.FromBoolean), new[] { typeof(bool) })!;
    private static readonly MethodInfo MiFromInt32 =
        typeof(JsValue).GetMethod(nameof(JsValue.FromInt32), new[] { typeof(int) })!;
    private static readonly MethodInfo MiFromNumber =
        typeof(JsValue).GetMethod(nameof(JsValue.FromNumber), new[] { typeof(double) })!;
    private static readonly MethodInfo MiAsBoolean =
        typeof(JsValue).GetMethod(nameof(JsValue.AsBoolean), Type.EmptyTypes)!;
    private static readonly MethodInfo MiFastNumberResult = typeof(BytecodeInterpreter)
        .GetMethod("FastNumberResult", BindingFlags.Static | BindingFlags.NonPublic,
            null, new[] { typeof(double) }, null)!;

    private static readonly FieldInfo FiThrowRouted =
        typeof(InterpreterFrame).GetField(nameof(InterpreterFrame.ThrowRoutedToHandler))!;

    private static readonly MethodInfo MiEndFinally = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.EndFinallyForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiTypeOfName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.TypeOfName), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiJsValueFromString = typeof(JsValue)
        .GetMethod(nameof(JsValue.FromString), BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) })!;
    private static readonly MethodInfo MiCreateFunctionFromNested = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CreateFunctionFromNestedForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiNewRegExp = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.NewRegExpForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiThrow = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.ThrowForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiGetPropByName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.GetPropByNameForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiGetPropByNameDirect = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.GetPropByNameForJit_Direct), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiSetPropByName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SetPropByNameForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiSetPropByNameDirect = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SetPropByNameForJit_Direct), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiDeletePropByName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.DeletePropByNameForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiGetElem = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.GetElemForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiSetElem = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SetElemForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiSetElemByIndex = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SetElemByIndexForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiDeleteElem = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.DeleteElemForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiCall0 = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.Call0ForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiCall1 = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.Call1ForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiCallN = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CallNForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiCallMethod0 = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CallMethod0ForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiCallMethod1 = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CallMethod1ForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiCallMethodN = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CallMethodNForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiConstruct0 = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.Construct0ForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiConstruct1 = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.Construct1ForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiConstructN = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.ConstructNForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly PropertyInfo PiCatchHandlers =
        typeof(InterpreterFrame).GetProperty("CatchHandlers")!;

    private static readonly PropertyInfo PiFinallyHandlers =
        typeof(InterpreterFrame).GetProperty("FinallyHandlers")!;

    private static readonly PropertyInfo PiHandlerEnvironments =
        typeof(InterpreterFrame).GetProperty("HandlerEnvironments")!;

    private static readonly PropertyInfo PiFrameEnvironment =
        typeof(InterpreterFrame).GetProperty("Environment")!;

    private static readonly PropertyInfo PiFrameInstructionPointer =
        typeof(InterpreterFrame).GetProperty("InstructionPointer")!;

    private static readonly MethodInfo MiIntStackPush = typeof(Stack<int>).GetMethod("Push")!;
    private static readonly MethodInfo MiIntStackPop = typeof(Stack<int>).GetMethod("Pop")!;
    private static readonly PropertyInfo PiIntStackCount = typeof(Stack<int>).GetProperty("Count")!;

    private static readonly MethodInfo MiEnvStackPush =
        typeof(Stack<FenBrowser.Js.Environments.EnvironmentRecord>).GetMethod("Push")!;
    private static readonly MethodInfo MiEnvStackPop =
        typeof(Stack<FenBrowser.Js.Environments.EnvironmentRecord>).GetMethod("Pop")!;
    private static readonly PropertyInfo PiEnvStackCount =
        typeof(Stack<FenBrowser.Js.Environments.EnvironmentRecord>).GetProperty("Count")!;

    private static readonly ConstructorInfo CtorJsThrown =
        typeof(JsThrownException).GetConstructor(new[] { typeof(JsValue) })!;

    private static readonly PropertyInfo PiThrownValue =
        typeof(JsThrownException).GetProperty("Value")!;

    private static readonly PropertyInfo PiThrownUncatchable =
        typeof(JsThrownException).GetProperty(
            "IsUncatchableByScript", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo MiRouteThrow = typeof(BytecodeInterpreter)
        .GetMethod("TryRouteThrowForJit", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo MiApplyBinop = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.ApplyBinopForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiApplyUnaryOp = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.ApplyUnaryOpForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiInOp = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.InForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiInstanceOfOp = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.InstanceOfForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiDeleteOp = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.DeleteForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiSetPrototypeOp = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SetPrototypeForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiEnumerateKeys = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.EnumerateKeysForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiEnumerateValues = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.EnumerateValuesForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiForOfNext = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.ForOfNextForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiIteratorClose = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.IteratorCloseForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiForInNext = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.ForInNextForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiDefinePrivateField = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.DefinePrivateFieldForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiGetPrivateField = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.GetPrivateFieldForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiSetPrivateField = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SetPrivateFieldForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiCallSpread = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CallSpreadForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiHandleDefineAccessor = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.HandleDefineAccessor), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiHandleDefineAccessorByReg = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.HandleDefineAccessorByReg), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiHandleSetHomeObject = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.HandleSetHomeObject), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiHandleLoadSuperProperty = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.HandleLoadSuperProperty), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiHandleLoadSuperElement = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.HandleLoadSuperElement), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiHandleLoadSuperConstructor = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.HandleLoadSuperConstructor), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MiSlotBindingsFor =
        typeof(FenBrowser.Js.Environments.DeclarativeEnvironmentRecord)
            .GetMethod("SlotBindingsFor", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo MiSlotPresenceFor =
        typeof(FenBrowser.Js.Environments.DeclarativeEnvironmentRecord)
            .GetMethod("SlotPresenceFor", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly Type BindingArrayType =
        typeof(FenBrowser.Js.Environments.DeclarativeEnvironmentRecord)
            .GetMethod("SlotBindingsFor", BindingFlags.Instance | BindingFlags.NonPublic)!.ReturnType;

    private static readonly PropertyInfo PiBindingValue =
        BindingArrayType.GetElementType()!.GetProperty("Value")!;

    private static readonly PropertyInfo PiBindingInitialized =
        BindingArrayType.GetElementType()!.GetProperty("IsInitialized")!;
    private static readonly ConstructorInfo CiBinding =
        BindingArrayType.GetElementType()!.GetConstructors()[0]!;
    private static readonly PropertyInfo PiBindingMutable =
        BindingArrayType.GetElementType()!.GetProperty("IsMutable")!;
    private static readonly PropertyInfo PiBindingStrict =
        BindingArrayType.GetElementType()!.GetProperty("IsStrict")!;
    private static readonly PropertyInfo PiBindingDeletable =
        BindingArrayType.GetElementType()!.GetProperty("IsDeletable")!;

    private static readonly MethodInfo MiLoadSlotFast = typeof(BytecodeInterpreter)
        .GetMethod("LoadSlotFast", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo MiStoreSlotFast = typeof(BytecodeInterpreter)
        .GetMethod("StoreSlotFast", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo MiCheckExecutionBudgetCharged = typeof(BytecodeInterpreter)
        .GetMethod("CheckExecutionBudgetForJit", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(int) }, null)!;

    private static readonly MethodInfo MiCheckExecutionBudget = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CheckExecutionBudgetForJit),
            BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!;
    private static readonly PropertyInfo PiRegisters = typeof(InterpreterFrame).GetProperty(nameof(InterpreterFrame.Registers))!;
    private static readonly PropertyInfo PiFunction = typeof(InterpreterFrame).GetProperty(nameof(InterpreterFrame.Function))!;
    private static readonly PropertyInfo PiConstants = typeof(BytecodeFunction).GetProperty(nameof(BytecodeFunction.Constants))!;
    private static readonly PropertyInfo PiThisValue = typeof(InterpreterFrame).GetProperty(nameof(InterpreterFrame.ThisValue))!;
    private static readonly PropertyInfo PiNewTarget = typeof(InterpreterFrame).GetProperty(nameof(InterpreterFrame.NewTarget))!;

    private static JitDelegate? TryEmitExpressionTree(BytecodeFunction function)
    {
        // Pre-pass: bail if the function contains any opcode that the
        // current emitter does not handle — particularly handler ops,
        // because Throw / ThrowOrHandle would otherwise redirect
        // frame.InstructionPointer to a catch target the JIT delegate
        // has no label for.
        for (var i = 0; i < function.Instructions.Count; i++)
        {
            var op = function.Instructions[i].OpCode;
            // Frame-suspending ops can't be JIT'd — they need IP save/restore
            // across delegate boundaries which the JIT lambda doesn't model.
            if (op == OpCode.Yield || op == OpCode.YieldStar ||
                op == OpCode.Await || op == OpCode.EnumerateValuesAsync)
            {
                NoteRejection(op);
                return null;
            }
        }

        var interpParam = Expression.Parameter(typeof(BytecodeInterpreter), "interp");
        var frameParam = Expression.Parameter(typeof(InterpreterFrame), "frame");
        var startIpParam = Expression.Parameter(typeof(int), "startIp");
        var registersLocal = Expression.Variable(typeof(JsValue[]), "registers");
        var constantsLocal = Expression.Variable(typeof(IReadOnlyList<JsValue>), "constants");
        var returnLabel = Expression.Label(typeof(JsValue), "return");

        var instructionLabels = new LabelTarget[function.Instructions.Count];
        for (var i = 0; i < instructionLabels.Length; i++)
            instructionLabels[i] = Expression.Label("ip_" + i);

        // A loop header -- the target of a backwards branch -- is where a
        // running frame may hand over. A catch target is where a routed throw
        // resumes. Both need a slot in the dispatch below; nothing else does.
        var loopHeaders = CollectLoopHeaders(function);
        var resumePoints = new HashSet<int>(loopHeaders);
        foreach (var handler in CollectHandlerTargets(function))
        {
            resumePoints.Add(handler);
        }

        var resumeIpLocal = Expression.Variable(typeof(int), "resumeIp");
        var slotBindingsLocal = Expression.Variable(BindingArrayType, "slotBindings");
        var slotPresentLocal = Expression.Variable(typeof(bool[]), "slotPresent");

        // Deriving the frame's slot storage costs a cast, an identity check and
        // a call. None of it changes while the environment does not, so do it
        // once here and again only where the environment is replaced.
        Expression RefreshSlots()
        {
            var envLocal = Expression.Variable(
                typeof(FenBrowser.Js.Environments.DeclarativeEnvironmentRecord), "ownEnv");
            return Expression.Block(
                new[] { envLocal },
                Expression.Assign(envLocal, Expression.TypeAs(
                    Expression.Property(frameParam, PiFrameEnvironment),
                    typeof(FenBrowser.Js.Environments.DeclarativeEnvironmentRecord))),
                Expression.IfThenElse(
                    Expression.Equal(envLocal, Expression.Constant(null, envLocal.Type)),
                    Expression.Block(
                        Expression.Assign(slotBindingsLocal, Expression.Constant(null, BindingArrayType)),
                        Expression.Assign(slotPresentLocal, Expression.Constant(null, typeof(bool[])))),
                    Expression.Block(
                        Expression.Assign(slotBindingsLocal, Expression.Call(
                            envLocal, MiSlotBindingsFor, Expression.Property(frameParam, PiFunction))),
                        Expression.Assign(slotPresentLocal, Expression.Call(
                            envLocal, MiSlotPresenceFor, Expression.Property(frameParam, PiFunction))))));
        }

        var body = new List<Expression>();
        var dispatchLabel = Expression.Label("dispatch");
        body.Add(Expression.Label(dispatchLabel));
        // A frame that entered compiled code after the dispatch loop routed a
        // throw would otherwise see a flag left over from that.
        body.Add(Expression.Assign(
            Expression.Field(frameParam, FiThrowRouted), Expression.Constant(false)));

        // On every entry, including a re-dispatch after a throw was routed to a
        // handler: the handler restores the environment it was pushed with, so
        // slot storage derived before the throw no longer describes the frame.
        body.Add(RefreshSlots());

        if (resumePoints.Count > 0)
        {
            var entryCases = new List<SwitchCase>(resumePoints.Count);
            foreach (var target in resumePoints)
            {
                entryCases.Add(Expression.SwitchCase(
                    Expression.Goto(instructionLabels[target]),
                    Expression.Constant(target)));
            }

            // Anything else -- including 0 -- falls through to the top.
            body.Add(Expression.Switch(
                typeof(void),
                resumeIpLocal,
                Expression.Empty(),
                comparison: null,
                entryCases));
        }

        // Poll at loop headers rather than before every instruction. Two calls
        // ahead of each opcode cost more than most opcodes do, and a script that
        // fails to terminate always goes round a loop, so a header is where the
        // check earns its keep. Each one is charged for the span it governs so
        // the instruction budget still measures work rather than iterations.
        var safepointCharge = new Dictionary<int, int>();
        foreach (var header in loopHeaders)
        {
            var furthest = header;
            for (var i = header; i < function.Instructions.Count; i++)
            {
                var branch = function.Instructions[i];
                if ((branch.OpCode == OpCode.Jump && branch.A == header) ||
                    (branch.OpCode == OpCode.JumpIfFalse && branch.B == header))
                {
                    furthest = i;
                }
            }

            safepointCharge[header] = Math.Max(1, furthest - header + 1);
        }

        for (var i = 0; i < function.Instructions.Count; i++)
        {
            body.Add(Expression.Label(instructionLabels[i]));
            if (i == 0 || safepointCharge.ContainsKey(i))
            {
                body.Add(Expression.Call(
                    interpParam,
                    MiCheckExecutionBudgetCharged,
                    Expression.Constant(safepointCharge.TryGetValue(i, out var charge) ? charge : 1)));
            }

            var ins = function.Instructions[i];
            if (!TryEmitOpcode(function, ins, i, interpParam, frameParam, registersLocal, constantsLocal,
                    slotBindingsLocal, slotPresentLocal, RefreshSlots, resumeIpLocal, dispatchLabel,
                    resumePoints.Count > 0, instructionLabels, returnLabel, body))
            {
                NoteRejection(ins.OpCode);
                return null;
            }

            // Most of what an opcode delegates to can route a throw to a handler
            // in this frame rather than raising it: the helper moves the
            // instruction pointer and returns, and without this the body would
            // carry on with the next instruction and run the rest of the try
            // block as though nothing had thrown. Pure emissions -- ones that
            // touch nothing but registers and constants -- are skipped.
            if (resumePoints.Count > 0 && CanRouteThrow(ins.OpCode))
            {
                body.Add(Expression.IfThen(
                    Expression.Field(frameParam, FiThrowRouted),
                    Expression.Block(
                        Expression.Assign(
                            Expression.Field(frameParam, FiThrowRouted), Expression.Constant(false)),
                        Expression.Assign(
                            resumeIpLocal, Expression.Property(frameParam, PiFrameInstructionPointer)),
                        Expression.Goto(dispatchLabel))));
            }
        }

        // Falling off the end without a Return yields undefined, same as the
        // dispatch loop. Leaving the try lands on the label below it.
        body.Add(Expression.Goto(returnLabel, Expression.Constant(JsValue.Undefined)));

        // Anything the body throws is offered to this frame's handlers exactly
        // as the dispatch loop offers it. If one takes it, ThrowOrHandle has
        // already set the instruction pointer and restored the environment, so
        // resuming is a matter of dispatching to that offset; if none does, the
        // exception carries on out of the frame.
        var thrownParam = Expression.Parameter(typeof(JsThrownException), "thrown");
        var routed = Expression.IfThenElse(
            Expression.AndAlso(
                // An interrupt or an exhausted budget is not the script's to
                // catch; it stops the script, and routing it into a handler
                // would let a runaway loop swallow the thing ending it.
                Expression.Not(Expression.Property(thrownParam, PiThrownUncatchable)),
                Expression.Call(interpParam, MiRouteThrow, frameParam,
                    Expression.Property(thrownParam, PiThrownValue))),
            Expression.Assign(resumeIpLocal, Expression.Property(frameParam, PiFrameInstructionPointer)),
            Expression.Rethrow());

        var guarded = Expression.TryCatch(
            Expression.Block(typeof(void), body),
            Expression.Catch(thrownParam, routed));

        var full = Expression.Block(
            typeof(JsValue),
            new[] { registersLocal, constantsLocal, resumeIpLocal, slotBindingsLocal, slotPresentLocal },
            Expression.Assign(registersLocal, Expression.Property(frameParam, PiRegisters)),
            Expression.Assign(constantsLocal, Expression.Property(Expression.Property(frameParam, PiFunction), PiConstants)),
            Expression.Assign(resumeIpLocal, startIpParam),
            // The loop only ever repeats when a throw was routed back into the
            // body; every other way out of it goes through the return label.
            Expression.Loop(guarded),
            Expression.Label(returnLabel, Expression.Constant(JsValue.Undefined)));

        var lambda = Expression.Lambda<JitDelegate>(full, interpParam, frameParam, startIpParam);
        try
        {
            var lambdaStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var compiled = PreferInterpretation ? lambda.Compile(preferInterpretation: true) : lambda.Compile();
            Interlocked.Add(ref LambdaCompileTicks, System.Diagnostics.Stopwatch.GetTimestamp() - lambdaStart);
            // Compile() hands back a delegate over a DynamicMethod whose IL the
            // CLR has not turned into machine code yet; it does that on the
            // first invocation. That invocation is on the thread running the
            // page, so without this the compile is off-thread and the CLR's own
            // JIT of what it produced is not - and on a bundle this size that is
            // the larger of the two (dotnet.jit.compilation.time measured 16s
            // over one reCAPTCHA run). Force it here, where we already are.
            // An interpreted delegate has no IL to pre-JIT, so preparing it
            // would cost time and produce nothing.
            if (!PreferInterpretation)
            {
                var prepareStart = System.Diagnostics.Stopwatch.GetTimestamp();
                PrepareForFirstCall(compiled);
                Interlocked.Add(ref PrepareTicks, System.Diagnostics.Stopwatch.GetTimestamp() - prepareStart);
            }

            function.OsrEntryPoints = loopHeaders.Count > 0 ? loopHeaders : null;
            return compiled;
        }
        catch
        {
            function.OsrEntryPoints = null;
            return null;
        }
    }

    /// <summary>
    /// Catch offsets named by the function's handler pushes. A throw routed to
    /// one of these has to be resumable, so each needs a dispatch entry.
    /// </summary>
    /// <summary>
    /// A binary operator, with the numeric case written out in line.
    ///
    /// Every one of these used to be a call into ApplyBinopForJit, which takes
    /// the operator as an argument and switches on it at run time. The operator
    /// is a constant here, so the switch, the call and the argument shuffling
    /// are all avoidable: emit the tag test and the arithmetic directly and let
    /// the host JIT keep the operands in registers. Anything the fast form does
    /// not cover -- strings, BigInt, objects with valueOf -- still goes to the
    /// interpreter, which is the only thing that knows those rules.
    /// </summary>
    private static Expression EmitBinop(
        Instruction ins, ParameterExpression interp, ParameterExpression frame, ParameterExpression registers)
    {
        var slow = Expression.Call(interp, MiApplyBinop, frame,
            Expression.Constant((int)ins.OpCode),
            Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C));

        var left = Expression.Variable(typeof(JsValue), "binL");
        var right = Expression.Variable(typeof(JsValue), "binR");
        var dest = Expression.ArrayAccess(registers, Expression.Constant(ins.A));

        static Expression IsNumeric(Expression value) =>
            Expression.OrElse(
                Expression.Equal(Expression.Field(value, FiValueTag),
                    Expression.Constant(JsValueTag.Int32)),
                Expression.Equal(Expression.Field(value, FiValueTag),
                    Expression.Constant(JsValueTag.Number)));

        static Expression IsInt32(Expression value) =>
            Expression.Equal(Expression.Field(value, FiValueTag), Expression.Constant(JsValueTag.Int32));

        Expression AsDouble(ParameterExpression value) => Expression.Call(value, MiAsNumber);
        Expression AsInt(ParameterExpression value) => Expression.Call(value, MiAsInt32);

        // ToInt32 shift counts are taken modulo 32 (ECMA-262 13.9).
        Expression ShiftCount() => Expression.And(AsInt(right), Expression.Constant(31));

        Expression? fast = null;
        Expression? guard = null;

        Expression Number(Expression d) => Expression.Call(MiFastNumberResult, d);
        Expression Bool(Expression b) => Expression.Call(MiFromBoolean, b);
        Expression Int(Expression i) => Expression.Call(MiFromInt32, i);

        switch (ins.OpCode)
        {
            case OpCode.Add:
                guard = IsNumeric(left); fast = Number(Expression.Add(AsDouble(left), AsDouble(right))); break;
            case OpCode.Sub:
                guard = IsNumeric(left); fast = Number(Expression.Subtract(AsDouble(left), AsDouble(right))); break;
            case OpCode.Mul:
                guard = IsNumeric(left); fast = Number(Expression.Multiply(AsDouble(left), AsDouble(right))); break;
            case OpCode.Lt:
                guard = IsNumeric(left); fast = Bool(Expression.LessThan(AsDouble(left), AsDouble(right))); break;
            case OpCode.Gt:
                guard = IsNumeric(left); fast = Bool(Expression.GreaterThan(AsDouble(left), AsDouble(right))); break;
            case OpCode.Le:
                guard = IsNumeric(left); fast = Bool(Expression.LessThanOrEqual(AsDouble(left), AsDouble(right))); break;
            case OpCode.Ge:
                guard = IsNumeric(left); fast = Bool(Expression.GreaterThanOrEqual(AsDouble(left), AsDouble(right))); break;
            case OpCode.Eq:
            case OpCode.StrictEq:
                guard = IsNumeric(left); fast = Bool(Expression.Equal(AsDouble(left), AsDouble(right))); break;
            case OpCode.Neq:
            case OpCode.StrictNeq:
                guard = IsNumeric(left); fast = Bool(Expression.NotEqual(AsDouble(left), AsDouble(right))); break;

            // The bitwise operators coerce through ToInt32, so an operand that
            // already is one needs no coercion at all. Anything else -- a
            // double, a string, a BigInt -- takes the interpreter's path.
            case OpCode.BitAnd:
                guard = IsInt32(left); fast = Int(Expression.And(AsInt(left), AsInt(right))); break;
            case OpCode.BitOr:
                guard = IsInt32(left); fast = Int(Expression.Or(AsInt(left), AsInt(right))); break;
            case OpCode.BitXor:
                guard = IsInt32(left); fast = Int(Expression.ExclusiveOr(AsInt(left), AsInt(right))); break;
            case OpCode.ShiftLeft:
                guard = IsInt32(left); fast = Int(Expression.LeftShift(AsInt(left), ShiftCount())); break;
            case OpCode.ShiftRight:
                guard = IsInt32(left); fast = Int(Expression.RightShift(AsInt(left), ShiftCount())); break;
            case OpCode.UnsignedShiftRight:
                guard = IsInt32(left);
                fast = Number(Expression.Convert(
                    Expression.RightShift(
                        Expression.Convert(AsInt(left), typeof(uint)), ShiftCount()),
                    typeof(double)));
                break;
        }

        if (fast is null || guard is null)
        {
            // And, Or and anything else not listed: no numeric shortcut.
            return slow;
        }

        // The right operand's guard is the same shape as the left's.
        var rightGuard = ins.OpCode is OpCode.BitAnd or OpCode.BitOr or OpCode.BitXor
            or OpCode.ShiftLeft or OpCode.ShiftRight or OpCode.UnsignedShiftRight
            ? IsInt32(right)
            : IsNumeric(right);

        return Expression.Block(
            new[] { left, right },
            Expression.Assign(left, Expression.ArrayAccess(registers, Expression.Constant(ins.B))),
            Expression.Assign(right, Expression.ArrayAccess(registers, Expression.Constant(ins.C))),
            Expression.IfThenElse(
                Expression.AndAlso(guard, rightGuard),
                Expression.Assign(dest, fast),
                slow));
    }

    /// <summary>
    /// A unary operator, with the numeric case written out in line. Same
    /// reasoning as <see cref="EmitBinop"/>: the operator is a constant here,
    /// so the run-time switch behind ApplyUnaryOpForJit is avoidable.
    /// </summary>
    private static Expression EmitUnaryOp(
        Instruction ins, ParameterExpression interp, ParameterExpression frame, ParameterExpression registers)
    {
        var slow = Expression.Call(interp, MiApplyUnaryOp, frame,
            Expression.Constant((int)ins.OpCode),
            Expression.Constant(ins.A), Expression.Constant(ins.B));

        var operand = Expression.Variable(typeof(JsValue), "unOperand");
        var dest = Expression.ArrayAccess(registers, Expression.Constant(ins.A));
        var tag = Expression.Field(operand, FiValueTag);
        var isNumeric = Expression.OrElse(
            Expression.Equal(tag, Expression.Constant(JsValueTag.Int32)),
            Expression.Equal(tag, Expression.Constant(JsValueTag.Number)));

        Expression asNumber = Expression.Call(operand, MiAsNumber);
        Expression? fast = null;
        Expression? guard = null;

        switch (ins.OpCode)
        {
            // Match StepNumeric exactly, tagging included: an integral result
            // keeps the Int32 tag. The tag decides which fast paths downstream
            // operators can take, and a loop counter feeds the bitwise
            // operators an obfuscated bundle is built out of, so handing `i++`
            // back as a Number deoptimised every `i & mask` after it. Negating
            // zero still yields a Number, because Int32 cannot carry -0.
            case OpCode.Increment:
                guard = isNumeric;
                fast = Expression.Call(MiFastNumberResult, Expression.Add(asNumber, Expression.Constant(1.0)));
                break;
            case OpCode.Decrement:
                guard = isNumeric;
                fast = Expression.Call(MiFastNumberResult, Expression.Subtract(asNumber, Expression.Constant(1.0)));
                break;
            case OpCode.Neg:
                guard = isNumeric;
                fast = Expression.Call(MiFastNumberResult, Expression.Negate(asNumber));
                break;
            case OpCode.Pos:
                guard = isNumeric;
                fast = Expression.Call(MiFastNumberResult, asNumber);
                break;
            case OpCode.ToNumeric:
                // Already numeric: ToNumeric is the identity on it.
                guard = isNumeric;
                fast = operand;
                break;
            case OpCode.Not:
                guard = Expression.Equal(tag, Expression.Constant(JsValueTag.Boolean));
                fast = Expression.Call(MiFromBoolean, Expression.Not(Expression.Call(operand, MiAsBoolean)));
                break;
        }

        if (fast is null || guard is null)
        {
            return slow;
        }

        return Expression.Block(
            new[] { operand },
            Expression.Assign(operand, Expression.ArrayAccess(registers, Expression.Constant(ins.B))),
            Expression.IfThenElse(guard, Expression.Assign(dest, fast), slow));
    }

    /// <summary>
    /// Whether an opcode's compiled form can reach code that routes a throw to
    /// one of this frame's handlers. Only the emissions that touch nothing but
    /// registers, constants and labels are exempt.
    /// </summary>
    private static bool CanRouteThrow(OpCode op) => op switch
    {
        OpCode.LoadConst or OpCode.Move or OpCode.Jump or OpCode.JumpIfFalse or
        OpCode.Nop or OpCode.Return or OpCode.Throw or
        OpCode.PushHandler or OpCode.PopHandler => false,
        _ => true
    };

    private static HashSet<int> CollectHandlerTargets(BytecodeFunction function)
    {
        var targets = new HashSet<int>();
        for (var i = 0; i < function.Instructions.Count; i++)
        {
            var ins = function.Instructions[i];
            if (ins.OpCode != OpCode.PushHandler)
            {
                continue;
            }

            if (ins.A >= 0 && ins.A < function.Instructions.Count) targets.Add(ins.A);
            if (ins.D >= 0 && ins.D < function.Instructions.Count) targets.Add(ins.D);
        }

        return targets;
    }

    /// <summary>
    /// Instruction offsets that are the target of a backwards branch. These
    /// are the points a running frame may transfer into compiled code at:
    /// every register the code reads lives in the frame both sides share, and
    /// a loop header is reached with no interpreter-only state outstanding.
    /// </summary>
    private static HashSet<int> CollectLoopHeaders(BytecodeFunction function)
    {
        var headers = new HashSet<int>();
        for (var i = 0; i < function.Instructions.Count; i++)
        {
            var ins = function.Instructions[i];
            switch (ins.OpCode)
            {
                case OpCode.Jump when ins.A <= i:
                    headers.Add(ins.A);
                    break;
                case OpCode.JumpIfFalse when ins.B <= i:
                    headers.Add(ins.B);
                    break;
            }
        }

        headers.RemoveWhere(h => h < 0 || h >= function.Instructions.Count);
        return headers;
    }

    private static bool TryEmitOpcode(
        BytecodeFunction function, Instruction ins, int ip,
        ParameterExpression interp, ParameterExpression frame,
        ParameterExpression registers, ParameterExpression constants,
        ParameterExpression slotBindings, ParameterExpression slotPresent, Func<Expression> refreshSlots,
        ParameterExpression resumeIp, LabelTarget dispatchLabel, bool hasResumePoints,
        LabelTarget[] labels, LabelTarget returnLabel, List<Expression> body)
    {
        switch (ins.OpCode)
        {
            case OpCode.LoadConst:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.Constants.Count) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.Property(constants, "Item", Expression.Constant(ins.B))));
                return true;
            case OpCode.Move:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.ArrayAccess(registers, Expression.Constant(ins.B))));
                return true;
            case OpCode.LoadVar:
            {
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                // The inline read below indexes two arrays with this slot;
                // only the upper bound is checked at run time, so a negative
                // slot has to be turned away here.
                if (ins.B < 0) return false;
                var slotIndex = Expression.Constant(ins.B);
                // Read the flags and the value off the array access rather than
                // copying the binding out: a property read there takes the
                // element's address, and the copy costs more than re-indexing.
                var element = Expression.ArrayAccess(slotBindings, slotIndex);
                // bindings != null && (uint)slot < length && present[slot] && initialized
                var usable = Expression.AndAlso(
                    Expression.AndAlso(
                        Expression.NotEqual(slotBindings, Expression.Constant(null, slotBindings.Type)),
                        Expression.LessThan(slotIndex, Expression.ArrayLength(slotBindings))),
                    Expression.AndAlso(
                        Expression.ArrayIndex(slotPresent, slotIndex),
                        Expression.Property(element, PiBindingInitialized)));
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.Condition(
                        usable,
                        Expression.Property(element, PiBindingValue),
                        Expression.Call(interp, MiLoadSlotFast, frame, slotIndex))));
                return true;
            }
            case OpCode.StoreVar:
            {
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0) return false;
                var storeSlot = Expression.Constant(ins.B);
                var storeValue = Expression.Variable(typeof(JsValue), "storeValue");
                var storeElement = Expression.ArrayAccess(slotBindings, storeSlot);
                // The same conditions TryWriteOwnSlot checks, plus one more: a
                // non-object value needs no write barrier, and the barrier is
                // the only reason this had to be a call.
                var storable = Expression.AndAlso(
                    Expression.AndAlso(
                        Expression.AndAlso(
                            Expression.NotEqual(slotBindings, Expression.Constant(null, slotBindings.Type)),
                            Expression.LessThan(storeSlot, Expression.ArrayLength(slotBindings))),
                        Expression.AndAlso(
                            Expression.ArrayIndex(slotPresent, storeSlot),
                            Expression.NotEqual(
                                Expression.Field(storeValue, FiValueTag),
                                Expression.Constant(JsValueTag.Object)))),
                    Expression.AndAlso(
                        Expression.Property(storeElement, PiBindingInitialized),
                        Expression.Property(storeElement, PiBindingMutable)));
                body.Add(Expression.Block(
                    new[] { storeValue },
                    Expression.Assign(storeValue, Expression.ArrayAccess(registers, Expression.Constant(ins.A))),
                    Expression.IfThenElse(
                        storable,
                        Expression.Assign(
                            storeElement,
                            Expression.New(
                                CiBinding,
                                storeValue,
                                Expression.Property(storeElement, PiBindingMutable),
                                Expression.Property(storeElement, PiBindingInitialized),
                                Expression.Property(storeElement, PiBindingStrict),
                                Expression.Property(storeElement, PiBindingDeletable))),
                        Expression.Call(interp, MiStoreSlotFast, frame, storeSlot, storeValue))));
                return true;
            }
            case OpCode.InitVar:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiInitializeName, frame, Expression.Constant(ins.B),
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A))));
                return true;
            case OpCode.PreResolveVar:
                body.Add(Expression.Call(interp, MiPreResolveBinding, frame, Expression.Constant(ins.B)));
                return true;
            case OpCode.StoreResolvedVar:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiStoreToResolvedBinding, frame, Expression.Constant(ins.B),
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A))));
                return true;
            case OpCode.Return:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                body.Add(Expression.Return(returnLabel,
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A))));
                return true;
            case OpCode.Jump:
                if (ins.A < 0 || ins.A >= function.Instructions.Count) return false;
                body.Add(Expression.Goto(labels[ins.A]));
                return true;
            case OpCode.JumpIfFalse:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.Instructions.Count) return false;
                body.Add(Expression.IfThen(
                    Expression.Not(Expression.Call(interp, MiIsTruthy,
                        Expression.ArrayAccess(registers, Expression.Constant(ins.A)))),
                    Expression.Goto(labels[ins.B])));
                return true;
            case OpCode.LoadThis:
                // Defer to the interpreter helper to keep the derived-
                // constructor and FunctionEnvironmentRecord this-binding
                // logic in one place. The JIT just emits the call and the
                // register store.
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.Call(interp, MiLoadThis, frame, Expression.Constant(ip))));
                return true;
            case OpCode.LoadNewTarget:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.Property(frame, PiNewTarget)));
                return true;
            case OpCode.NewObject:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.Call(interp, MiNewObject)));
                return true;
            case OpCode.NewArray:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.Call(interp, MiNewArray, Expression.Constant(ins.B))));
                return true;
            case OpCode.InitThisBinding:
                body.Add(Expression.Call(interp, MiInitThisBinding, frame));
                return true;
            case OpCode.EnterScope:
                body.Add(Expression.Call(interp, MiEnterScope, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B)));
                // The frame sits on a different environment now, so the slot
                // storage hoisted at entry no longer describes it.
                body.Add(refreshSlots());
                return true;
            case OpCode.LeaveScope:
                body.Add(Expression.Call(interp, MiLeaveScope, frame));
                body.Add(refreshSlots());
                return true;
            case OpCode.Nop:
                return true;
            case OpCode.EndFinally:
            {
                // Re-dispatching needs the entry switch; without one the goto
                // would land on the first instruction instead of the handler.
                if (!hasResumePoints) return false;
                var endFinallyAction = Expression.Variable(typeof(int), "endFinallyAction");
                var endFinallyReturn = Expression.Variable(typeof(JsValue), "endFinallyReturn");
                body.Add(Expression.Block(
                    new[] { endFinallyAction, endFinallyReturn },
                    Expression.Assign(
                        endFinallyAction,
                        Expression.Call(interp, MiEndFinally, frame, endFinallyReturn)),
                    Expression.IfThen(
                        Expression.Equal(endFinallyAction, Expression.Constant(2)),
                        Expression.Goto(returnLabel, endFinallyReturn)),
                    Expression.IfThen(
                        Expression.Equal(endFinallyAction, Expression.Constant(1)),
                        Expression.Block(
                            Expression.Assign(
                                resumeIp, Expression.Property(frame, PiFrameInstructionPointer)),
                            Expression.Goto(dispatchLabel)))));
                return true;
            }
            case OpCode.TypeOfName:
                // `typeof someIdentifier`. Minified bundles are full of these as
                // feature guards, and refusing the opcode meant refusing every
                // function that contained one -- which was most of the hot ones
                // in Google's robot check.
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.Call(
                        MiJsValueFromString,
                        Expression.Call(interp, MiTypeOfName, frame, Expression.Constant(ins.B)))));
                return true;
            case OpCode.NextIterationEnv:
                body.Add(Expression.Call(interp, MiCreatePerIterationEnvironment, frame,
                    Expression.Constant(ins.A)));
                // Same as EnterScope: the frame stands on different records now.
                body.Add(refreshSlots());
                return true;
            case OpCode.CreateFunction:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.NestedFunctions.Count) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.Call(interp, MiCreateFunctionFromNested, frame, Expression.Constant(ins.B))));
                return true;
            case OpCode.NewRegExp:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.Constants.Count) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.Call(interp, MiNewRegExp, frame, Expression.Constant(ins.B))));
                return true;
            case OpCode.Throw:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                // Raise it for real rather than asking the interpreter to move
                // an instruction pointer this body does not have. The wrapper
                // routes it to a handler in this frame or lets it leave, the
                // same two outcomes ThrowOrHandle produces.
                body.Add(Expression.Throw(Expression.New(
                    CtorJsThrown,
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)))));
                return true;
            case OpCode.PushHandler:
                body.Add(Expression.Call(
                    Expression.Property(frame, PiCatchHandlers), MiIntStackPush,
                    Expression.Constant(ins.A)));
                body.Add(Expression.Call(
                    Expression.Property(frame, PiFinallyHandlers), MiIntStackPush,
                    Expression.Constant(ins.D)));
                body.Add(Expression.Call(
                    Expression.Property(frame, PiHandlerEnvironments), MiEnvStackPush,
                    Expression.Property(frame, PiFrameEnvironment)));
                return true;
            case OpCode.PopHandler:
                body.Add(Expression.IfThen(
                    Expression.GreaterThan(
                        Expression.Property(Expression.Property(frame, PiCatchHandlers), PiIntStackCount),
                        Expression.Constant(0)),
                    Expression.Block(
                        Expression.Call(Expression.Property(frame, PiCatchHandlers), MiIntStackPop),
                        Expression.Call(Expression.Property(frame, PiFinallyHandlers), MiIntStackPop),
                        Expression.IfThen(
                            Expression.GreaterThan(
                                Expression.Property(Expression.Property(frame, PiHandlerEnvironments), PiEnvStackCount),
                                Expression.Constant(0)),
                            Expression.Call(Expression.Property(frame, PiHandlerEnvironments), MiEnvStackPop)))));
                return true;
            case OpCode.GetPropByName:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                if (ins.C < 0 || ins.C >= function.PropertyNames.Count) return false;
                // Audit §3.1: pre-resolve property name + pre-allocate IC at
                // compile time so each call avoids one dictionary lookup and
                // one list indexing. The IC reference is stable for the
                // function's lifetime; embedding it as a Constant is safe.
                {
                    var propConst = function.PropertyNames[ins.C];
                    var icConst = EnsureLoadIC(function, ip);
                    body.Add(Expression.Call(interp, MiGetPropByNameDirect, frame,
                        Expression.Constant(ins.A), Expression.Constant(ins.B),
                        Expression.Constant(propConst), Expression.Constant(icConst)));
                }
                return true;
            case OpCode.SetPropByName:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.PropertyNames.Count) return false;
                if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
                {
                    var propConst = function.PropertyNames[ins.B];
                    var icConst = EnsureStoreIC(function, ip);
                    body.Add(Expression.Call(interp, MiSetPropByNameDirect, frame,
                        Expression.Constant(ins.A), Expression.Constant(propConst),
                        Expression.Constant(ins.C), Expression.Constant(icConst)));
                }
                return true;
            case OpCode.DeletePropByName:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                if (ins.C < 0 || ins.C >= function.PropertyNames.Count) return false;
                body.Add(Expression.Call(interp, MiDeletePropByName, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C)));
                return true;
            case OpCode.GetElem:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiGetElem, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B),
                    Expression.Constant(ins.C), Expression.Constant(ip)));
                return true;
            case OpCode.SetElem:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiSetElem, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C)));
                return true;
            case OpCode.SetElemByIndex:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0) return false;
                if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiSetElemByIndex, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C)));
                return true;
            case OpCode.DeleteElem:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiDeleteElem, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C)));
                return true;
            case OpCode.Call0:
                body.Add(Expression.Call(interp, MiCall0, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B),
                    Expression.Constant(ins.E), Expression.Constant(ip)));
                return true;
            case OpCode.Call1:
                body.Add(Expression.Call(interp, MiCall1, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C),
                    Expression.Constant(ins.E), Expression.Constant(ip)));
                return true;
            case OpCode.CallN:
                if (ins.D < 0) return false;
                body.Add(Expression.Call(interp, MiCallN, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C),
                    Expression.Constant(ins.D), Expression.Constant(ins.E), Expression.Constant(ip)));
                return true;
            case OpCode.CallMethod0:
                body.Add(Expression.Call(interp, MiCallMethod0, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C),
                    Expression.Constant(ip)));
                return true;
            case OpCode.CallMethod1:
                body.Add(Expression.Call(interp, MiCallMethod1, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C),
                    Expression.Constant(ins.D), Expression.Constant(ip)));
                return true;
            case OpCode.CallMethodN:
                if (ins.E < 0) return false;
                body.Add(Expression.Call(interp, MiCallMethodN, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C),
                    Expression.Constant(ins.D), Expression.Constant(ins.E), Expression.Constant(ip)));
                return true;
            case OpCode.Construct0:
                body.Add(Expression.Call(interp, MiConstruct0, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B)));
                return true;
            case OpCode.Construct1:
                body.Add(Expression.Call(interp, MiConstruct1, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C)));
                return true;
            case OpCode.ConstructN:
                if (ins.D < 0) return false;
                body.Add(Expression.Call(interp, MiConstructN, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C),
                    Expression.Constant(ins.D)));
                return true;
            case OpCode.Add:
            case OpCode.Sub:
            case OpCode.Mul:
            case OpCode.Mod:
            case OpCode.Div:
            case OpCode.Exp:
            case OpCode.Eq:
            case OpCode.Neq:
            case OpCode.StrictEq:
            case OpCode.StrictNeq:
            case OpCode.Lt:
            case OpCode.Gt:
            case OpCode.Le:
            case OpCode.Ge:
            case OpCode.And:
            case OpCode.Or:
            case OpCode.BitAnd:
            case OpCode.BitOr:
            case OpCode.BitXor:
            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:
            case OpCode.UnsignedShiftRight:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
                body.Add(EmitBinop(ins, interp, frame, registers));
                return true;
            case OpCode.Not:
            case OpCode.Pos:
            case OpCode.Neg:
            case OpCode.Void:
            case OpCode.TypeOf:
            case OpCode.BitNot:
            case OpCode.ToNumeric:
            case OpCode.Increment:
            case OpCode.Decrement:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                body.Add(EmitUnaryOp(ins, interp, frame, registers));
                return true;
            case OpCode.In:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiInOp, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C)));
                return true;
            case OpCode.InstanceOf:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiInstanceOfOp, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C)));
                return true;
            case OpCode.Delete:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiDeleteOp, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B)));
                return true;
            case OpCode.SetPrototype:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiSetPrototypeOp, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B)));
                return true;
            case OpCode.EnumerateKeys:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.Call(interp, MiEnumerateKeys, frame, Expression.Constant(ins.B))));
                return true;
            case OpCode.EnumerateValues:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                body.Add(Expression.Assign(
                    Expression.ArrayAccess(registers, Expression.Constant(ins.A)),
                    Expression.Call(interp, MiEnumerateValues, frame, Expression.Constant(ins.B))));
                return true;
            case OpCode.ForOfNext:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                if (ins.C < 0 || ins.C >= function.Instructions.Count) return false;
                body.Add(Expression.IfThen(
                    Expression.Call(interp, MiForOfNext, frame,
                        Expression.Constant(ins.A), Expression.Constant(ins.B)),
                    Expression.Goto(labels[ins.C])));
                return true;
            case OpCode.ForInNext:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                if (ins.C < 0 || ins.C >= function.Instructions.Count) return false;
                body.Add(Expression.IfThen(
                    Expression.Call(interp, MiForInNext, frame,
                        Expression.Constant(ins.A), Expression.Constant(ins.B)),
                    Expression.Goto(labels[ins.C])));
                return true;
            case OpCode.IteratorClose:
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiIteratorClose, frame, Expression.Constant(ins.B)));
                return true;
            case OpCode.DefinePrivateField:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.PropertyNames.Count) return false;
                if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiDefinePrivateField, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C)));
                return true;
            case OpCode.GetPrivateField:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
                if (ins.C < 0 || ins.C >= function.PropertyNames.Count) return false;
                body.Add(Expression.Call(interp, MiGetPrivateField, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C)));
                return true;
            case OpCode.SetPrivateField:
                if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
                if (ins.B < 0 || ins.B >= function.PropertyNames.Count) return false;
                if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
                body.Add(Expression.Call(interp, MiSetPrivateField, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B), Expression.Constant(ins.C)));
                return true;
            case OpCode.CallSpread:
                body.Add(Expression.Call(interp, MiCallSpread, frame,
                    Expression.Constant(ins.A), Expression.Constant(ins.B),
                    Expression.Constant(ins.C), Expression.Constant(ins.D)));
                return true;
            case OpCode.DefineGetter:
            case OpCode.DefineSetter:
                body.Add(Expression.Call(interp, MiHandleDefineAccessor, frame,
                    Expression.Property(frame, PiFunction), Expression.Constant(ins)));
                return true;
            case OpCode.DefineGetterByReg:
            case OpCode.DefineSetterByReg:
                body.Add(Expression.Call(interp, MiHandleDefineAccessorByReg, frame, Expression.Constant(ins)));
                return true;
            case OpCode.SetHomeObject:
                body.Add(Expression.Call(interp, MiHandleSetHomeObject, frame, Expression.Constant(ins)));
                return true;
            case OpCode.LoadSuperProperty:
                body.Add(Expression.Call(interp, MiHandleLoadSuperProperty, frame,
                    Expression.Property(frame, PiFunction), Expression.Constant(ins)));
                return true;
            case OpCode.LoadSuperElement:
                body.Add(Expression.Call(interp, MiHandleLoadSuperElement, frame, Expression.Constant(ins)));
                return true;
            case OpCode.LoadSuperConstructor:
                body.Add(Expression.Call(interp, MiHandleLoadSuperConstructor, frame, Expression.Constant(ins)));
                return true;
            // Future opcodes added here as the IL-emit work continues.
            default:
                return false;
        }
    }

    // ---- shared helpers for the constant-fold path -----------------

    private static bool TryFoldBinop(BytecodeFunction function, Instruction ins, JsValue?[] regs, out JsValue result)
    {
        result = default;
        if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
        if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
        if (ins.C < 0 || ins.C >= function.RegisterCount) return false;
        if (regs[ins.B] is not { } lhs || regs[ins.C] is not { } rhs) return false;

        switch (ins.OpCode)
        {
            case OpCode.Add:
                if (lhs.Tag == JsValueTag.String && rhs.Tag == JsValueTag.String)
                { result = JsValue.FromString(lhs.AsString() + rhs.AsString()); return true; }
                if (!TryGetNumber(lhs, out var addL) || !TryGetNumber(rhs, out var addR)) return false;
                result = JsValue.FromNumber(addL + addR); return true;
            case OpCode.Sub:
            case OpCode.Mul:
            case OpCode.Div:
            case OpCode.Mod:
            case OpCode.Exp:
                if (!TryGetNumber(lhs, out var nL) || !TryGetNumber(rhs, out var nR)) return false;
                result = JsValue.FromNumber(ins.OpCode switch
                {
                    OpCode.Sub => nL - nR,
                    OpCode.Mul => nL * nR,
                    OpCode.Div => nL / nR,
                    OpCode.Mod => nL % nR,
                    OpCode.Exp => Math.Pow(nL, nR),
                    _ => double.NaN,
                });
                return true;
            case OpCode.BitAnd:
            case OpCode.BitOr:
            case OpCode.BitXor:
                if (!TryGetInt32(lhs, out var iL) || !TryGetInt32(rhs, out var iR)) return false;
                result = JsValue.FromInt32(ins.OpCode switch
                {
                    OpCode.BitAnd => iL & iR,
                    OpCode.BitOr => iL | iR,
                    OpCode.BitXor => iL ^ iR,
                    _ => 0,
                });
                return true;
            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:
                if (!TryGetInt32(lhs, out var sL) || !TryGetInt32(rhs, out var sR)) return false;
                var shift = sR & 0x1F;
                result = JsValue.FromInt32(ins.OpCode == OpCode.ShiftLeft ? sL << shift : sL >> shift);
                return true;
            case OpCode.UnsignedShiftRight:
                if (!TryGetInt32(lhs, out var uL) || !TryGetInt32(rhs, out var uR)) return false;
                var ushift = uR & 0x1F;
                result = JsValue.FromNumber((double)((uint)uL >> ushift));
                return true;
            case OpCode.StrictEq:
                result = JsValue.FromBoolean(StrictEquals(lhs, rhs)); return true;
            case OpCode.StrictNeq:
                result = JsValue.FromBoolean(!StrictEquals(lhs, rhs)); return true;
            case OpCode.Eq:
            case OpCode.Neq:
                if (lhs.Tag != rhs.Tag) return false;
                var eqResult = StrictEquals(lhs, rhs);
                result = JsValue.FromBoolean(ins.OpCode == OpCode.Eq ? eqResult : !eqResult);
                return true;
            case OpCode.Lt:
            case OpCode.Gt:
            case OpCode.Le:
            case OpCode.Ge:
                if (!TryGetNumber(lhs, out var cL) || !TryGetNumber(rhs, out var cR)) return false;
                if (double.IsNaN(cL) || double.IsNaN(cR))
                { result = JsValue.FromBoolean(false); return true; }
                result = JsValue.FromBoolean(ins.OpCode switch
                {
                    OpCode.Lt => cL < cR,
                    OpCode.Gt => cL > cR,
                    OpCode.Le => cL <= cR,
                    OpCode.Ge => cL >= cR,
                    _ => false,
                });
                return true;
            case OpCode.And:
                result = IsTruthy(lhs) ? rhs : lhs; return true;
            case OpCode.Or:
                result = IsTruthy(lhs) ? lhs : rhs; return true;
        }
        return false;
    }

    private static bool TryFoldUnaryOp(BytecodeFunction function, Instruction ins, JsValue?[] regs, out JsValue result)
    {
        result = default;
        if (ins.A < 0 || ins.A >= function.RegisterCount) return false;
        if (ins.B < 0 || ins.B >= function.RegisterCount) return false;
        if (regs[ins.B] is not { } v) return false;

        switch (ins.OpCode)
        {
            case OpCode.Not:
                result = JsValue.FromBoolean(!IsTruthy(v)); return true;
            case OpCode.Void:
                result = JsValue.Undefined; return true;
            case OpCode.Pos:
                if (!TryGetNumber(v, out var pn)) return false;
                result = JsValue.FromNumber(pn); return true;
            case OpCode.Neg:
                if (!TryGetNumber(v, out var nn)) return false;
                result = JsValue.FromNumber(-nn); return true;
            case OpCode.BitNot:
                if (!TryGetInt32(v, out var bi)) return false;
                result = JsValue.FromInt32(~bi); return true;
            case OpCode.TypeOf:
                result = JsValue.FromString(v.Tag switch
                {
                    JsValueTag.Undefined => "undefined",
                    JsValueTag.Null => "object",
                    JsValueTag.Boolean => "boolean",
                    JsValueTag.Int32 or JsValueTag.Number => "number",
                    JsValueTag.String => "string",
                    JsValueTag.Symbol => "symbol",
                    JsValueTag.BigInt => "bigint",
                    _ => "object",
                });
                return true;
        }
        return false;
    }

    private static bool StrictEquals(JsValue a, JsValue b)
    {
        if (a.Tag != b.Tag)
        {
            if ((a.Tag == JsValueTag.Int32 || a.Tag == JsValueTag.Number) &&
                (b.Tag == JsValueTag.Int32 || b.Tag == JsValueTag.Number))
            {
                TryGetNumber(a, out var na);
                TryGetNumber(b, out var nb);
                return na == nb;
            }
            return false;
        }
        return a.Tag switch
        {
            JsValueTag.Undefined or JsValueTag.Null => true,
            JsValueTag.Boolean => a.AsBoolean() == b.AsBoolean(),
            JsValueTag.Int32 => a.AsInt32() == b.AsInt32(),
            JsValueTag.Number => a.AsNumber() == b.AsNumber(),
            JsValueTag.BigInt => a.AsBigInt() == b.AsBigInt(),
            JsValueTag.String => string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal),
            JsValueTag.Symbol => a.AsSymbolId() == b.AsSymbolId(),
            JsValueTag.Object => a.AsObjectHandle().Equals(b.AsObjectHandle()),
            JsValueTag.HostObject => a.AsHostObjectHandle().Equals(b.AsHostObjectHandle()),
            _ => false,
        };
    }

    private static bool IsTruthy(JsValue v) => v.Tag switch
    {
        JsValueTag.Undefined => false,
        JsValueTag.Null => false,
        JsValueTag.Boolean => v.AsBoolean(),
        JsValueTag.Int32 => v.AsInt32() != 0,
        JsValueTag.Number => v.AsNumber() != 0 && !double.IsNaN(v.AsNumber()),
        JsValueTag.String => v.StringLength > 0,
        _ => true,
    };

    private static bool TryGetNumber(JsValue v, out double n)
    {
        switch (v.Tag)
        {
            case JsValueTag.Number: n = v.AsNumber(); return true;
            case JsValueTag.Int32: n = v.AsInt32(); return true;
            case JsValueTag.Boolean: n = v.AsBoolean() ? 1 : 0; return true;
            case JsValueTag.Null: n = 0; return true;
            default: n = 0; return false;
        }
    }

    private static bool TryGetInt32(JsValue v, out int n)
    {
        switch (v.Tag)
        {
            case JsValueTag.Int32: n = v.AsInt32(); return true;
            case JsValueTag.Number:
                var d = v.AsNumber();
                if (double.IsNaN(d) || double.IsInfinity(d)) { n = 0; return true; }
                // Spec ToInt32: truncate then reduce modulo 2^32. A direct cast
                // is out-of-range for |d| >= 2^32 and .NET leaves that result
                // platform-defined, so x64 and ARM64 disagreed.
                var truncated = Math.Truncate(d);
                n = unchecked((int)(uint)(truncated - (Math.Floor(truncated / 4294967296d) * 4294967296d)));
                return true;
            case JsValueTag.Boolean: n = v.AsBoolean() ? 1 : 0; return true;
            case JsValueTag.Null: n = 0; return true;
            default: n = 0; return false;
        }
    }

    // Pre-allocate a Load IC for the given instruction offset so the JIT
    // can embed the reference as a Constant. Returns the existing IC if
    // one was populated by prior interpreted runs.
    private static PolymorphicInlineCache EnsureLoadIC(BytecodeFunction function, int icOffset)
    {
        var caches = function.EnsureLoadICs();
        if ((uint)icOffset >= (uint)caches.Length)
        {
            return new PolymorphicInlineCache();
        }

        return caches[icOffset] ??= new PolymorphicInlineCache();
    }

    private static PolymorphicInlineCache EnsureStoreIC(BytecodeFunction function, int icOffset)
    {
        var caches = function.EnsureStoreICs();
        if ((uint)icOffset >= (uint)caches.Length)
        {
            return new PolymorphicInlineCache();
        }

        return caches[icOffset] ??= new PolymorphicInlineCache();
    }
}
#endif // !PUBLISH_AOT
