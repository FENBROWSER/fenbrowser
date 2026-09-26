namespace FenBrowser.Js.Interpreter2;

/// <summary>
/// Settings for the register-window loop, which runs every JavaScript function
/// body that is not running as compiled code.
/// </summary>
/// <remarks>
/// Process-level because they have to be reachable from three places that do
/// not share a configuration object: the JS shell, the test262 runner, and the
/// browser host. Mutable so tests can flip one around a single case.
/// </remarks>
public static class Interp2Options
{
    /// <summary>Env var: <c>FEN_JS_INTERP2_LOG</c> = <c>1</c> to record and print coverage.</summary>
    public const string LogVariable = "FEN_JS_INTERP2_LOG";

    /// <summary>
    /// True when the loop records how each function was laid out and how many
    /// frames it ran. Every trace site is behind this one flag so the JIT drops
    /// the whole of it from a hot loop when it is off.
    /// </summary>
    public static bool Log { get; set; } = ReadFlag(LogVariable);

    /// <summary>
    /// Ceiling on the shared value stack, in <c>JsValue</c> slots. Reaching it
    /// raises a catchable RangeError, the same shape of failure a call-depth
    /// overflow already produces, rather than growing until the host dies.
    /// </summary>
    public const int MaxValueStackSlots = 1 << 21;

    private static bool ReadFlag(string name)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        return value is not null &&
               (value.Equals("1", StringComparison.Ordinal) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }
}
