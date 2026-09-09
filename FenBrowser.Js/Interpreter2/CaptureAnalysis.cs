using System.Runtime.CompilerServices;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Objects;

namespace FenBrowser.Js.Interpreter2;

/// <summary>
/// What a function body needs from the scope that encloses it.
/// </summary>
/// <remarks>
/// Computed transitively: a function's demands include those of every function
/// nested inside it that its own declarations do not satisfy, because a closure
/// three levels down still reaches through every level above it.
/// </remarks>
public sealed class CaptureInfo
{
    internal CaptureInfo(
        IReadOnlySet<string> freeNames,
        bool needsEnclosingThis,
        bool needsEnclosingArguments,
        bool needsEnclosingNewTarget,
        bool needsEnclosingSuper)
    {
        FreeNames = freeNames;
        NeedsEnclosingThis = needsEnclosingThis;
        NeedsEnclosingArguments = needsEnclosingArguments;
        NeedsEnclosingNewTarget = needsEnclosingNewTarget;
        NeedsEnclosingSuper = needsEnclosingSuper;
    }

    /// <summary>Identifiers this body resolves outside itself.</summary>
    public IReadOnlySet<string> FreeNames { get; }

    /// <summary>
    /// True when this body reads <c>this</c> through the scope chain rather than
    /// binding its own - an arrow, or an arrow inside one. ECMA-262 9.1.2.5
    /// GetThisEnvironment walks past a declarative record to find it, so an
    /// enclosing frame that keeps no record cannot supply it.
    /// </summary>
    public bool NeedsEnclosingThis { get; }

    public bool NeedsEnclosingArguments { get; }

    public bool NeedsEnclosingNewTarget { get; }

    public bool NeedsEnclosingSuper { get; }

    /// <summary>Nothing at all is reached through the enclosing scope.</summary>
    public bool IsClosed =>
        FreeNames.Count == 0 &&
        !NeedsEnclosingThis &&
        !NeedsEnclosingArguments &&
        !NeedsEnclosingNewTarget &&
        !NeedsEnclosingSuper;
}

/// <summary>
/// Which of a function's variables a closure made inside it could observe.
/// </summary>
/// <remarks>
/// <para>
/// This is the analysis that decides whether the register-window loop can run a
/// body that creates functions - which is nearly every body worth running. The
/// question it answers is not "does this function make a closure" but "could a
/// closure it makes ever read one of its variables".
/// </para>
/// <para>
/// A closure is a function object plus the environment it captured, and the
/// only thing that environment is for is resolving the names the closure does
/// not declare itself. Those names are knowable from the bytecode: a nested
/// function's slot table lists every identifier it mentions, and the ones it
/// declares are its parameters, its vars and its lexical declarations. What is
/// left is what it reaches outwards for. If none of those names is one the
/// enclosing body declares, then no closure it makes can see its variables, and
/// they can stay in registers - the closure is simply handed the environment
/// the enclosing body itself closed over, and every name it does reach for
/// still resolves exactly where it did before.
/// </para>
/// <para>
/// <c>this</c>, <c>arguments</c>, <c>new.target</c> and <c>super</c> are the
/// same question asked about the bindings a function environment record holds
/// that are not identifiers, and they are tracked alongside the names. An arrow
/// reads <c>this</c> through the chain, so an enclosing body that keeps no
/// record cannot supply it and is declined.
/// </para>
/// <para>
/// This is the conservative half of what V8 and SpiderMonkey do. They go
/// further: when a variable <i>is</i> captured, only that variable is moved to a
/// heap context and the rest stay in registers. Here a single captured variable
/// sends the whole body back to the old loop. That is the right first version -
/// it is decidable from the bytecode alone, it needs no change to the compiler,
/// and it cannot be wrong in the direction that matters.
/// </para>
/// </remarks>
public static class CaptureAnalysis
{
    private static readonly ConditionalWeakTable<BytecodeFunction, CaptureInfo> Cache = new();

    /// <summary>
    /// A recursion ceiling for pathological nesting. Reaching it reports a body
    /// that demands everything, which sends its enclosing function to the old
    /// loop - the safe direction.
    /// </summary>
    private const int MaxNestingDepth = 64;

    private static readonly CaptureInfo DemandsEverything = new(
        new HashSet<string>(StringComparer.Ordinal) { "*" },
        needsEnclosingThis: true,
        needsEnclosingArguments: true,
        needsEnclosingNewTarget: true,
        needsEnclosingSuper: true);

    /// <summary>What this function needs from the scope enclosing it.</summary>
    public static CaptureInfo For(BytecodeFunction function) => Compute(function, depth: 0);

    /// <summary>
    /// Which of a body's own declarations a function created inside it could
    /// read, at any depth. An empty set means every variable can stay in a
    /// register; the names in it are the ones that need a heap binding.
    /// </summary>
    /// <returns>
    /// Null when a nested function reaches for something that is not an
    /// identifier - the enclosing <c>this</c>, <c>arguments</c>,
    /// <c>new.target</c> or <c>super</c>. Those live on a function environment
    /// record rather than in the bindings, so the body has to keep a real one
    /// and belongs on the old loop.
    /// </returns>
    public static HashSet<string>? CapturedNames(BytecodeFunction function, IReadOnlySet<string> declaredNames)
    {
        var captured = new HashSet<string>(StringComparer.Ordinal);
        var nested = function.NestedFunctions;
        for (var i = 0; i < nested.Count; i++)
        {
            var info = Compute(nested[i], depth: 1);
            if (info.NeedsEnclosingThis ||
                info.NeedsEnclosingArguments ||
                info.NeedsEnclosingNewTarget ||
                info.NeedsEnclosingSuper)
            {
                return null;
            }

            foreach (var name in info.FreeNames)
            {
                if (declaredNames.Contains(name))
                {
                    captured.Add(name);
                }
            }
        }

        return captured;
    }

    private static CaptureInfo Compute(BytecodeFunction function, int depth)
    {
        if (Cache.TryGetValue(function, out var cached))
        {
            return cached;
        }

        if (depth >= MaxNestingDepth)
        {
            return DemandsEverything;
        }

        var declared = DeclaredNames(function);
        var free = new HashSet<string>(StringComparer.Ordinal);

        // Every identifier the body mentions has a slot; the ones it does not
        // declare are the ones it reaches outwards for.
        foreach (var pair in function.VariableSlots)
        {
            if (!declared.Contains(pair.Key))
            {
                free.Add(pair.Key);
            }
        }

        var isArrow = function.Kind == FunctionKind.Arrow;
        var needsThis = isArrow && MentionsThis(function);
        var needsArguments = function.UsesOuterArguments;
        var needsNewTarget = isArrow && Mentions(function, OpCode.LoadNewTarget);
        var needsSuper = MentionsSuper(function);

        var nested = function.NestedFunctions;
        for (var i = 0; i < nested.Count; i++)
        {
            var child = Compute(nested[i], depth + 1);
            foreach (var name in child.FreeNames)
            {
                if (!declared.Contains(name))
                {
                    free.Add(name);
                }
            }

            // A demand for the enclosing `this` stops at the first body that
            // binds one: only an arrow reads it through the chain, so a
            // non-arrow child absorbs its own children's demand.
            if (child.NeedsEnclosingThis && isArrow) needsThis = true;
            if (child.NeedsEnclosingNewTarget && isArrow) needsNewTarget = true;
            if (child.NeedsEnclosingArguments && !function.HasOwnArgumentsObject) needsArguments = true;
            if (child.NeedsEnclosingSuper) needsSuper = true;
        }

        var info = new CaptureInfo(free, needsThis, needsArguments, needsNewTarget, needsSuper);
        Cache.AddOrUpdate(function, info);
        return info;
    }

    private static HashSet<string> DeclaredNames(BytecodeFunction function)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < function.ParameterNames.Count; i++) declared.Add(function.ParameterNames[i]);
        for (var i = 0; i < function.VarDeclarationNames.Count; i++) declared.Add(function.VarDeclarationNames[i]);
        for (var i = 0; i < function.LexicalDeclarationNames.Count; i++) declared.Add(function.LexicalDeclarationNames[i]);
        for (var i = 0; i < function.ConstDeclarationNames.Count; i++) declared.Add(function.ConstDeclarationNames[i]);

        // ECMA-262 15.2.5: a named function expression can see its own name.
        if (function.BindsOwnNameInBody && function.Name is { Length: > 0 } ownName)
        {
            declared.Add(ownName);
        }

        return declared;
    }

    private static bool MentionsThis(BytecodeFunction function)
    {
        var code = function.InstructionArray;
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i].OpCode == OpCode.LoadThis || code[i].OpCode == OpCode.InitThisBinding)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MentionsSuper(BytecodeFunction function)
    {
        var code = function.InstructionArray;
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i].OpCode is OpCode.LoadSuperProperty or OpCode.LoadSuperElement or OpCode.LoadSuperConstructor)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Mentions(BytecodeFunction function, OpCode opCode)
    {
        var code = function.InstructionArray;
        for (var i = 0; i < code.Length; i++)
        {
            if (code[i].OpCode == opCode)
            {
                return true;
            }
        }

        return false;
    }
}
