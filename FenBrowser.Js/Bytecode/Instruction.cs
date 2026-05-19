namespace FenBrowser.Js.Bytecode;

public readonly record struct Instruction(OpCode OpCode, int A = 0, int B = 0, int C = 0, int D = 0);
