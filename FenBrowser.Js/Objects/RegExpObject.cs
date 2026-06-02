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
