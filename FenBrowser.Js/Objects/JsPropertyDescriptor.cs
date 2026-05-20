using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

public readonly record struct JsPropertyDescriptor
{
    public JsPropertyDescriptor(JsValue Value, bool Writable, bool Enumerable, bool Configurable)
    {
        this.Value = Value;
        this.Writable = Writable;
        this.Enumerable = Enumerable;
        this.Configurable = Configurable;
        Get = JsValue.Undefined;
        Set = JsValue.Undefined;
        IsAccessor = false;
    }

    private JsPropertyDescriptor(
        JsValue Value,
        bool Writable,
        bool Enumerable,
        bool Configurable,
        JsValue Get,
        JsValue Set,
        bool IsAccessor)
    {
        this.Value = Value;
        this.Writable = Writable;
        this.Enumerable = Enumerable;
        this.Configurable = Configurable;
        this.Get = Get;
        this.Set = Set;
        this.IsAccessor = IsAccessor;
    }

    public JsValue Value { get; init; }

    public bool Writable { get; init; }

    public bool Enumerable { get; init; }

    public bool Configurable { get; init; }

    public JsValue Get { get; init; }

    public JsValue Set { get; init; }

    public bool IsAccessor { get; init; }

    public static JsPropertyDescriptor Accessor(JsValue Get, JsValue Set, bool Enumerable, bool Configurable)
    {
        return new JsPropertyDescriptor(
            JsValue.Undefined,
            Writable: false,
            Enumerable,
            Configurable,
            Get,
            Set,
            IsAccessor: true);
    }
}
