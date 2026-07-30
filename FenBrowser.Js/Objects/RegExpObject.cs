namespace FenBrowser.Js.Objects;

public sealed class RegExpObject : JsObject
{
    private FenBrowser.Js.Regex.RegexProgram? _nativeProgram;
    private Func<FenBrowser.Js.Regex.RegexProgram>? _lazyCompiler;

    public RegExpObject(
        string pattern,
        string flags,
        FenBrowser.Js.Regex.RegexProgram nativeProgram)
    {
        Pattern = pattern;
        Flags = flags;
        _nativeProgram = nativeProgram;
    }

    public RegExpObject(
        string pattern,
        string flags,
        Func<FenBrowser.Js.Regex.RegexProgram> lazyCompiler)
    {
        Pattern = pattern;
        Flags = flags;
        _lazyCompiler = lazyCompiler ?? throw new ArgumentNullException(nameof(lazyCompiler));
    }

    public string Pattern { get; private set; }
    public string Flags { get; private set; }

    public FenBrowser.Js.Regex.RegexProgram NativeProgram
    {
        get { EnsureCompiled(); return _nativeProgram!; }
    }

    // Annex B B.2.4.1 RegExp.prototype.compile(pattern, flags). Mutates this
    // instance to behave like a freshly constructed RegExp.
    public void Recompile(
        string pattern,
        string flags,
        FenBrowser.Js.Regex.RegexProgram nativeProgram)
    {
        Pattern = pattern;
        Flags = flags;
        _nativeProgram = nativeProgram;
        _lazyCompiler = null;
    }

    private void EnsureCompiled()
    {
        if (_nativeProgram is not null) return;
        lock (this)
        {
            if (_nativeProgram is not null) return;
            _nativeProgram = _lazyCompiler!();
            _lazyCompiler = null;
        }
    }
}
