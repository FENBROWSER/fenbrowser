using Microsoft.Extensions.Logging;

namespace FenBrowser.Js.Logging;

internal static partial class JsLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Trace,
        Message = "Executing opcode {Opcode} at ip {InstructionPointer}")]
    public static partial void ExecutingOpcode(
        ILogger logger,
        string opcode,
        int instructionPointer);
}
