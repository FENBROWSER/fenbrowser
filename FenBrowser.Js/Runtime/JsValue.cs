namespace FenBrowser.Js.Runtime;

public readonly struct JsValue
{
    public readonly JsValueTag Tag;
    private readonly long _payload;
    private readonly double _number;

    private JsValue(JsValueTag tag, long payload, double number)
    {
        Tag = tag;
        _payload = payload;
        _number = number;
    }

    public static JsValue Undefined => new(JsValueTag.Undefined, 0, 0);

    public static JsValue Null => new(JsValueTag.Null, 0, 0);

    public static JsValue FromBoolean(bool value) => new(JsValueTag.Boolean, value ? 1 : 0, 0);

    public static JsValue FromInt32(int value) => new(JsValueTag.Int32, value, 0);

    public static JsValue FromNumber(double value) => new(JsValueTag.Number, 0, value);

    public static JsValue FromObject(ObjectHandle handle) => new(JsValueTag.Object, handle.ToInt64(), 0);

    public static JsValue FromHostObject(HostObjectHandle handle) => new(JsValueTag.HostObject, handle.ToInt64(), 0);

    public bool AsBoolean() => Tag == JsValueTag.Boolean && _payload != 0;

    public int AsInt32() => checked((int)_payload);

    public double AsNumber() => _number;

    public ObjectHandle AsObjectHandle() => ObjectHandle.FromInt64(_payload);

    public HostObjectHandle AsHostObjectHandle(int realmId = 0, int documentEpoch = 0) => HostObjectHandle.FromInt64(_payload, realmId, documentEpoch);
}
