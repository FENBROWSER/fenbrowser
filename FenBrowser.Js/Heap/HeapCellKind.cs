namespace FenBrowser.Js.Heap;

public enum HeapCellKind
{
    Object,
    String,
    Symbol,
    BigInt,
    Function,
    Environment,
    BytecodeFunction,
    Promise,
    Module
}
