using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Diagnostics;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

public sealed partial class BytecodeInterpreter
{
    private object? _diagnosticActiveCallTarget;

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

    /// <summary>
    /// Lock-free best-effort snapshot for a watchdog thread: the call a native
    /// entry is in and the register-window loop's innermost frame. The parts
    /// may be from adjacent moments, which is sufficient for diagnosing a job
    /// that remains in the same hot call for seconds.
    /// </summary>
    public string CaptureExecutionDiagnosticSnapshot()
    {
        try
        {
            var target = Volatile.Read(ref _diagnosticActiveCallTarget);
            var (function, ip, frameDepth) = _interp2?.DiagnosticTopFrame() ?? (null, -1, 0);
            var functionText = function == null
                ? "<none>"
                : BytecodeFunctionSignature.Describe(function);
            var opcodeText = function is not null && (uint)ip < (uint)function.InstructionArray.Length
                ? function.InstructionArray[ip].OpCode.ToString()
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
