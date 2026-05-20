namespace FenBrowser.Js.Runtime;

public sealed class JsVariableCell
{
    public JsVariableCell(JsValue value)
    {
        Value = value;
    }

    public JsValue Value { get; set; }
}
