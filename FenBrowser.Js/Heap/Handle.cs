namespace FenBrowser.Js.Heap;

public readonly struct Handle<T>
    where T : struct
{
    public readonly T Value;

    public Handle(T value)
    {
        Value = value;
    }
}
