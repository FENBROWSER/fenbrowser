namespace FenBrowser.Js.Objects;

public sealed class RegExpObject : JsObject
{
    public RegExpObject(
        string pattern,
        string flags,
        System.Text.RegularExpressions.Regex regex,
        FenBrowser.Js.Regex.RegexProgram? nativeProgram = null)
    {
        Pattern = pattern;
        Flags = flags;
        Regex = regex;
        NativeProgram = nativeProgram;
    }

    public string Pattern { get; private set; }
    public string Flags { get; private set; }
    public System.Text.RegularExpressions.Regex Regex { get; private set; }
    public FenBrowser.Js.Regex.RegexProgram? NativeProgram { get; private set; }

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
        Regex = regex;
        NativeProgram = nativeProgram;
    }
}
