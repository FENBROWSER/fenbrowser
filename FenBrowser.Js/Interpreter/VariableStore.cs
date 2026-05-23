using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

public sealed class VariableStore
{
    private readonly JsValue[] _values;

    public VariableStore(int count)
    {
        _values = new JsValue[Math.Max(1, count)];
        Array.Fill(_values, JsValue.Undefined);
    }

    public JsValue this[int index]
    {
        get => _values[index];
        set => _values[index] = value;
    }
}
