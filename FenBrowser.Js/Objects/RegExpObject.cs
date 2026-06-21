namespace FenBrowser.Js.Objects;

public sealed class RegExpObject : JsObject
{
    private System.Text.RegularExpressions.Regex? _regex;
    private FenBrowser.Js.Regex.RegexProgram? _nativeProgram;
    private Func<(System.Text.RegularExpressions.Regex Regex, FenBrowser.Js.Regex.RegexProgram? NativeProgram)>? _lazyCompiler;

    public RegExpObject(
        string pattern,
        string flags,
        System.Text.RegularExpressions.Regex regex,
        FenBrowser.Js.Regex.RegexProgram? nativeProgram = null)
    {
        Pattern = pattern;
        Flags = flags;
        _regex = regex;
        _nativeProgram = nativeProgram;
    }

    public RegExpObject(
        string pattern,
        string flags,
        Func<(System.Text.RegularExpressions.Regex Regex, FenBrowser.Js.Regex.RegexProgram? NativeProgram)> lazyCompiler)
    {
        Pattern = pattern;
        Flags = flags;
        _lazyCompiler = lazyCompiler ?? throw new ArgumentNullException(nameof(lazyCompiler));
    }

    public string Pattern { get; private set; }
    public string Flags { get; private set; }
    public System.Text.RegularExpressions.Regex Regex
    {
        get { EnsureCompiled(); return _regex!; }
    }

    public FenBrowser.Js.Regex.RegexProgram? NativeProgram
    {
        get { EnsureCompiled(); return _nativeProgram; }
    }

    /// <summary>
    /// Maps original ECMAScript group names to the .NET-safe aliases used
    /// in the BclRegex (populated by RewriteNamedGroupSyntaxForDotNet).
    /// The alias for group name "$" is "g1", for "π" is "g2", etc.
    /// Used to translate .NET group names back to ECMAScript names in exec.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, string>? NamedGroupAliases { get; set; }

    /// <summary>
    /// Reverse map: alias → original name (for fast lookup during exec).
    /// </summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, string>? NamedGroupReverseMap { get; set; }

    // Annex B B.2.4.1 RegExp.prototype.compile(pattern, flags). Mutates this
    // instance to behave like a freshly constructed RegExp.
    public void Recompile(
        string pattern,
        string flags,
        System.Text.RegularExpressions.Regex regex,
        FenBrowser.Js.Regex.RegexProgram? nativeProgram = null)
    {
        Pattern = pattern;
        Flags = flags;
        _regex = regex;
        _nativeProgram = nativeProgram;
        _lazyCompiler = null;
    }

    private void EnsureCompiled()
    {
        if (_regex is not null) return;
        lock (this)
        {
            if (_regex is not null) return;
            var compiled = _lazyCompiler!();
            _regex = compiled.Regex;
            _nativeProgram = compiled.NativeProgram;
            _lazyCompiler = null;
        }
    }
}
