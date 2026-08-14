namespace FenBrowser.Js.Objects;

public sealed class RegExpObject : JsObject
{
    private readonly object _compileGate = new();
    private FenBrowser.Js.Regex.RegexProgram? _nativeProgram;
    private Func<FenBrowser.Js.Regex.RegexProgram>? _lazyCompiler;

    public RegExpObject(
        string pattern,
        string flags,
        FenBrowser.Js.Regex.RegexProgram nativeProgram)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Flags = flags ?? throw new ArgumentNullException(nameof(flags));
        _nativeProgram = nativeProgram ?? throw new ArgumentNullException(nameof(nativeProgram));
    }

    public RegExpObject(
        string pattern,
        string flags,
        Func<FenBrowser.Js.Regex.RegexProgram> lazyCompiler)
    {
        Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        Flags = flags ?? throw new ArgumentNullException(nameof(flags));
        _lazyCompiler = lazyCompiler ?? throw new ArgumentNullException(nameof(lazyCompiler));
    }

    public string Pattern { get; private set; }
    public string Flags { get; private set; }

    public FenBrowser.Js.Regex.RegexProgram NativeProgram
    {
        get
        {
            EnsureCompiled();
            return _nativeProgram!;
        }
    }

    // Annex B B.2.4.1 RegExp.prototype.compile(pattern, flags). Mutates this
    // instance to behave like a freshly constructed RegExp. Keep this mutation
    // serialized with lazy compilation so an older lazy compiler cannot race a
    // recompile and overwrite the new program/state.
    public void Recompile(
        string pattern,
        string flags,
        FenBrowser.Js.Regex.RegexProgram nativeProgram)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(nativeProgram);

        lock (_compileGate)
        {
            Pattern = pattern;
            Flags = flags;
            _nativeProgram = nativeProgram;
            _lazyCompiler = null;
        }
    }

    private void EnsureCompiled()
    {
        if (_nativeProgram is not null)
            return;

        lock (_compileGate)
        {
            if (_nativeProgram is not null)
                return;

            var compiler = _lazyCompiler
                ?? throw new InvalidOperationException("RegExp has neither a compiled program nor a lazy compiler.");
            var compiled = compiler()
                ?? throw new InvalidOperationException("RegExp lazy compiler returned no program.");

            _nativeProgram = compiled;
            _lazyCompiler = null;
        }
    }
}
