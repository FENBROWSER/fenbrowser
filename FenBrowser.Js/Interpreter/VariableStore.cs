using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

public sealed class VariableStore
{
    private readonly JsVariableCell[] _cells;

    public VariableStore(int count)
    {
        _cells = new JsVariableCell[Math.Max(1, count)];
        for (var i = 0; i < _cells.Length; i++)
        {
            _cells[i] = new JsVariableCell(JsValue.Undefined);
        }
    }

    public JsValue this[int index]
    {
        get => _cells[index].Value;
        set => _cells[index].Value = value;
    }

    public void BindCell(int index, JsVariableCell cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        _cells[index] = cell;
    }

    public JsVariableCell GetCell(int index) => _cells[index];
}
