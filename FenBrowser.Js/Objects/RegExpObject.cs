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

    public string Pattern { get; }
    public string Flags { get; }
    public Regex Regex { get; }
}
