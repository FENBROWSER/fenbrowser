using System.Text.RegularExpressions;

namespace FenBrowser.Js.Objects;

public sealed class RegExpObject : JsObject
{
    public RegExpObject(string pattern, string flags, Regex regex)
    {
        Pattern = pattern;
        Flags = flags;
        Regex = regex;
    }

    public string Pattern { get; private set; }
    public string Flags { get; private set; }
    public Regex Regex { get; private set; }

    // Annex B B.2.4.1 RegExp.prototype.compile(pattern, flags). Mutates this
    // instance to behave like a freshly constructed RegExp.
    public void Recompile(string pattern, string flags, Regex regex)
    {
        Pattern = pattern;
        Flags = flags;
        Regex = regex;
    }
}
