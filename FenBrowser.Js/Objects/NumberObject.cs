namespace FenBrowser.Js.Objects;

// ECMA-262 21.1 — Number exotic object wrapping a [[NumberData]] internal slot.
public sealed class NumberObject : JsObject
{
    public NumberObject(double value)
    {
        Value = value;
    }

    public double Value { get; }
}
