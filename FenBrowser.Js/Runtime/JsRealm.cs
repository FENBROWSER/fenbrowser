using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
namespace FenBrowser.Js.Runtime;
public sealed class JsRealm
{
    public BytecodeInterpreter Interpreter { get; }
    public JsRealm(JsHeap? heap = null) { Interpreter = heap is not null ? new BytecodeInterpreter(heap) : new BytecodeInterpreter(); }
    public JsRealm(BytecodeInterpreter interpreter) { Interpreter = interpreter; }
    public JsValue Execute(Bytecode.BytecodeFunction fn) => Interpreter.Execute(fn);
}
