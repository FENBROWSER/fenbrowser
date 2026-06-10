using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// Exception/error helpers extracted from BytecodeInterpreter.cs as part of
// audit section 2 slice 8. Pure file move, no semantic change.
public sealed partial class BytecodeInterpreter
{

    private void ThrowBindingFailure(InterpreterFrame frame, BindingOpResult status, string name, bool assignment)
    {
        switch (status)
        {
            case BindingOpResult.TdzAccess:
                ThrowReferenceError(frame, $"Cannot access '{name}' before initialization.");
                return;
            case BindingOpResult.ConstAssignment:
                ThrowTypeError(frame, assignment
                    ? $"Assignment to constant variable '{name}'."
                    : $"Cannot read immutable binding '{name}'.");
                return;
            case BindingOpResult.NotInitializable:
            case BindingOpResult.AlreadyDeclared:
                ThrowTypeError(frame, $"Cannot initialize binding '{name}'.");
                return;
            case BindingOpResult.NotFound:
                ThrowReferenceError(frame, $"{name} is not defined.");
                return;
            default:
                return;
        }
    }


    private void ThrowTypeError(InterpreterFrame frame, string message)
    {
        ThrowOrHandle(frame, CreateTypeError(message));
    }


    private void ThrowReferenceError(InterpreterFrame frame, string message)
    {
        ThrowOrHandle(frame, CreateReferenceError(message));
    }


    // Route a return completion (generator .return() injected at a yield) to the
    // innermost finally block. Catch-only handlers are skipped — a return
    // completion is not catchable. Returns true when a finally was entered (the
    // dispatch loop continues there); false when no finally covers the position
    // and the caller should complete the function with the value.
    private static bool TryRouteReturnThroughFinally(InterpreterFrame frame, JsValue value)
    {
        while (frame.CatchHandlers.Count > 0)
        {
            _ = frame.CatchHandlers.Pop();
            var finallyIp = frame.FinallyHandlers.Pop();
            EnvironmentRecord? handlerEnv = null;
            if (frame.HandlerEnvironments.Count > 0)
            {
                handlerEnv = frame.HandlerEnvironments.Pop();
            }

            if (finallyIp >= 0)
            {
                if (handlerEnv is not null)
                {
                    frame.Environment = handlerEnv;
                }

                frame.PendingReturn = value;
                frame.InstructionPointer = finallyIp;
                return true;
            }
        }

        return false;
    }

    private void ThrowOrHandle(InterpreterFrame frame, JsValue value)
    {
        if (frame.CatchHandlers.Count > 0)
        {
            
    var catchIp = frame.CatchHandlers.Pop();
            var finallyIp = frame.FinallyHandlers.Pop();
            // Restore the lexical environment to the try's level, discarding any
            // block or with environments pushed inside the try body.
            if (frame.HandlerEnvironments.Count > 0)
            {
                frame.Environment = frame.HandlerEnvironments.Pop();
            }

            frame.Registers[0] = value;

            if (catchIp >= 0)
            {
                frame.InstructionPointer = catchIp;
                return;
            }

            if (finallyIp >= 0)
            {
                frame.PendingException = value;
                frame.InstructionPointer = finallyIp;
                return;
            }
        }

        throw new JsThrownException(value);
    }


    private JsValue CreateError(string message)
    {
        return CreateErrorObject("Error", GetGlobalPrototype("Error"), message);
    }


    private JsValue CreateTypeError(string message)
    {
        return CreateErrorObject("TypeError", GetGlobalPrototype("TypeError"), message);
    }


    private JsValue CreateReferenceError(string message)
    {
        return CreateErrorObject("ReferenceError", GetGlobalPrototype("ReferenceError"), message);
    }


    private JsValue CreateRangeError(string message)
    {
        return CreateErrorObject("RangeError", GetGlobalPrototype("RangeError"), message);
    }


    private JsValue CreateSyntaxError(string message)
    {
        return CreateErrorObject("SyntaxError", GetGlobalPrototype("SyntaxError"), message);
    }


    private JsValue CreateErrorObject(string name, ObjectHandle prototypeHandle, string message)
    {
        // ECMA-262 20.5.1.1: only `message` lands on the instance (non-enumerable),
        // and only when a message was provided. `name` is inherited from the prototype.
        var error = new JsObject();
        error.ToStringTagSlot = BuiltinTagSlot.Error;
        error.SetPrototype(prototypeHandle);
        if (!string.IsNullOrEmpty(message))
        {
            _ = error.DefineOwnProperty("message",
                new JsPropertyDescriptor(JsValue.FromString(message), Writable: true, Enumerable: false, Configurable: true));
        }
        return JsValue.FromObject(_heap.AllocateObject(error, AllocationSite.Current()));
    }


    private JsValue CreateUriError(string message)
    {
        return CreateErrorObject("URIError", GetGlobalPrototype("URIError"), message);
    }


    private JsValue CreateDataCloneError(string message)
    {
        var err = new JsObject();
        err.SetPrototype(EnsureErrorPrototype());
        // DataCloneError is a DOMException-like name; install on the instance with
        // CreateMethodProperty-style attributes so descriptor checks pass.
        _ = err.DefineOwnProperty("name",
            new JsPropertyDescriptor(JsValue.FromString("DataCloneError"), Writable: true, Enumerable: false, Configurable: true));
        if (!string.IsNullOrEmpty(message))
        {
            _ = err.DefineOwnProperty("message",
                new JsPropertyDescriptor(JsValue.FromString(message), Writable: true, Enumerable: false, Configurable: true));
        }
        return JsValue.FromObject(_heap.AllocateObject(err, AllocationSite.Current()));
    }

}


