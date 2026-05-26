namespace FenBrowser.Js.Runtime;
public sealed class CspPolicy
{
    public bool AllowEval { get; set; } = true;
    public static CspPolicy Default { get; } = new();
}
