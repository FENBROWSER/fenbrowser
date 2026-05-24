namespace FenBrowser.Js.Objects;

// ECMA-262 20.3 — Boolean exotic object wrapping a [[BooleanData]] internal slot.
public sealed class BooleanObject : JsObject
{
    public BooleanObject(bool value)
    {
        Value = value;
    }

    public bool Value { get; }
}
