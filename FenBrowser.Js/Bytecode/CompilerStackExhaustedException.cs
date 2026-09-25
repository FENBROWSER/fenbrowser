namespace FenBrowser.Js.Bytecode;

/// <summary>
/// The compiler ran out of native stack on an expression nested more deeply
/// than the current thread can recurse through. <see cref="BytecodeCompiler"/>
/// retries once on a large-stack thread before letting this escape.
/// </summary>
public sealed class CompilerStackExhaustedException : InvalidOperationException
{
    public CompilerStackExhaustedException()
        : base("Expression nesting too deep to compile (insufficient stack).")
    {
    }
}
