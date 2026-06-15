using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Objects;

public readonly record struct JsPropertyDescriptor
{
    // When true, the corresponding field was explicitly provided in the input descriptor.
    // Needed by Integer-Indexed Exotic Object [[DefineOwnProperty]] (10.4.5.3) to
    // distinguish "configurable not present" (default allowed) from "configurable: false" (rejected).
    public bool HasConfigurable { get; init; }
    public bool HasEnumerable { get; init; }
    public bool HasWritable { get; init; }
    public bool HasValue { get; init; }
    public JsPropertyDescriptor(JsValue Value, bool Writable, bool Enumerable, bool Configurable)
    {
        this.Value = Value;
        this.Writable = Writable;
        this.Enumerable = Enumerable;
        this.Configurable = Configurable;
        Get = JsValue.Undefined;
        Set = JsValue.Undefined;
        IsAccessor = false;
        HasConfigurable = true;
        HasEnumerable = true;
        HasWritable = true;
        HasValue = true;
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
            IsAccessor: true)
        {
            HasConfigurable = true,
            HasEnumerable = true,
            HasWritable = false,
            HasValue = false,
        };
    }

    // Data descriptor where only the Configurable/Enumerable/Writable flags were provided
    // (no [[Value]] field). Used for partial descriptors in Integer-Indexed Exotic Objects.
    public static JsPropertyDescriptor DataPartial(JsValue value, bool? writable, bool? enumerable, bool? configurable)
    {
        return new JsPropertyDescriptor(
            value,
            writable ?? false,
            enumerable ?? false,
            configurable ?? false)
        {
            HasValue = true,
            HasWritable = writable.HasValue,
            HasEnumerable = enumerable.HasValue,
            HasConfigurable = configurable.HasValue,
        };
    }
}
