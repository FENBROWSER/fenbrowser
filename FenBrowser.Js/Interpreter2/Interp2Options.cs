namespace FenBrowser.Js.Interpreter2;

/// <summary>
/// Which execution loop runs a JavaScript function body, and how loudly.
/// </summary>
/// <remarks>
/// The register-window loop in <see cref="Interp2"/> is built beside the
/// original dispatch loop rather than in place of it. Both read the same
/// bytecode, allocate from the same heap and call the same builtins; only the
/// way a call is entered and where a frame's variables live is different. That
/// makes the two comparable on the same test262 run, which is the only way to
/// know what a new loop breaks before it has broken it.
///
/// The switch is a process-level setting because it has to be reachable from
/// three places that do not share a configuration object: the JS shell, the
/// test262 runner, and the browser host. It is a mutable static rather than a
/// readonly one so tests can flip it around a single case and put it back.
/// </remarks>
public static class Interp2Options
{
    /// <summary>Env var: <c>FEN_JS_INTERPRETER</c> = <c>v2</c> to select the new loop.</summary>
    public const string EngineVariable = "FEN_JS_INTERPRETER";

    /// <summary>Env var: <c>FEN_JS_INTERP2_LOG</c> = <c>1</c> to record and print coverage.</summary>
    public const string LogVariable = "FEN_JS_INTERP2_LOG";

    /// <summary>
    /// True when an eligible function body runs on the register-window loop.
    /// Off by default: the old loop is the one with a 93.37% test262 score
    /// behind it, and it stays the default until the new one matches it.
    /// </summary>
    public static bool Enabled { get; set; } = ReadEngine();

    /// <summary>
    /// True when the loop records why each function was or was not eligible and
    /// how many frames it ran. Every trace site is behind this one flag so the
    /// JIT drops the whole of it from a hot loop when it is off.
    /// </summary>
    public static bool Log { get; set; } = ReadFlag(LogVariable);

    /// <summary>
    /// Ceiling on a single frame's window (bytecode registers plus variable
    /// slots). A function wider than this falls back to the old loop rather
    /// than reserving an unbounded slice of the shared value stack — a bound on
    /// what one hostile or generated function can claim from every other frame.
    /// </summary>
    public const int MaxFrameWindow = 4096;

    /// <summary>
    /// Ceiling on the shared value stack, in <c>JsValue</c> slots. Reaching it
    /// raises a catchable RangeError, the same shape of failure a call-depth
    /// overflow already produces, rather than growing until the host dies.
    /// </summary>
    public const int MaxValueStackSlots = 1 << 21;

    private static bool ReadEngine()
    {
        var value = System.Environment.GetEnvironmentVariable(EngineVariable);
        return value is not null &&
               (value.Equals("v2", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("2", StringComparison.Ordinal) ||
                value.Equals("new", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ReadFlag(string name)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        return value is not null &&
               (value.Equals("1", StringComparison.Ordinal) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }
}
