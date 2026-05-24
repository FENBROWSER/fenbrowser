namespace FenBrowser.Js.Objects;

// ECMA-262 22.1 — String exotic object wrapping a [[StringData]] internal slot.
public sealed class StringObject : JsObject
{
    public StringObject(string value)
    {
        Value = value;
    }

    public string Value { get; }
}
