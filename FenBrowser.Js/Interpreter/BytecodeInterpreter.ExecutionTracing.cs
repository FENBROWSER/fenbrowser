using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Diagnostics;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

public sealed partial class BytecodeInterpreter
{
    private object? _diagnosticActiveCallTarget;
    private BytecodeFunction? _diagnosticActiveBytecodeFunction;
    private int _diagnosticActiveInstructionPointer = -1;
    private int _diagnosticActiveOpCode = -1;
    private int _diagnosticActiveFrameDepth;

    private object? CaptureCallTargetForDiagnostics(JsValue value)
    {
        try
        {
            return value.Tag == JsValueTag.Object ? ResolveObject(value) : null;
        }
        catch
        {
            return null;
        }
    }

    private void TraceInstructionProgress(
        BytecodeFunction function,
        int instructionPointer,
        OpCode opCode)
    {
        if (!CallTargetProfiler.Enabled || (_instructionCount & 0x1fff) != 0)
        {
            return;
        }

        Volatile.Write(ref _diagnosticActiveBytecodeFunction, function);
        Volatile.Write(ref _diagnosticActiveInstructionPointer, instructionPointer);
        Volatile.Write(ref _diagnosticActiveOpCode, (int)opCode);
        Volatile.Write(ref _diagnosticActiveFrameDepth, _activeFrames.Count);
    }

    /// <summary>
    /// Lock-free best-effort snapshot for a watchdog thread. The fields may be
    /// from adjacent instructions, which is sufficient for diagnosing a job
    /// that remains in the same hot call for seconds.
    /// </summary>
    public string CaptureExecutionDiagnosticSnapshot()
    {
        try
        {
            var target = Volatile.Read(ref _diagnosticActiveCallTarget);
            var function = Volatile.Read(ref _diagnosticActiveBytecodeFunction);
            var ip = Volatile.Read(ref _diagnosticActiveInstructionPointer);
            var op = Volatile.Read(ref _diagnosticActiveOpCode);
            var frameDepth = Volatile.Read(ref _diagnosticActiveFrameDepth);
            var functionText = function == null
                ? "<none>"
                : BytecodeFunctionSignature.Describe(function);
            var opcodeText = op >= byte.MinValue && op <= byte.MaxValue &&
                Enum.IsDefined((OpCode)(byte)op)
                    ? ((OpCode)(byte)op).ToString()
                    : "none";

            return $"activeCall={CallTargetProfiler.Describe(target)} callDepth={Volatile.Read(ref _callDepth)} " +
                $"frameDepth={frameDepth} bytecode={functionText} ip={ip} op={opcodeText}";
        }
        catch (Exception exception)
        {
            return $"<snapshot-failed:{exception.GetType().Name}>";
        }
    }
}
