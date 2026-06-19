using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// Environment/declaration helpers extracted from BytecodeInterpreter.cs as part of
// audit section 2 slice 7. Pure file move, no semantic change.
public sealed partial class BytecodeInterpreter
{

    private void InstantiateVarDeclarations(BytecodeFunction function, InterpreterFrame frame)
    {
        // For eval code, Annex B B.3.3.3 requires var/function bindings to be
        // configurable (deletable) on the global scope so tests verifyProperty
        // with { configurable: true }.
        var isEval = function.IsEvalCode;

        foreach (var name in function.VarDeclarationNames)
        {
            if (frame.Environment is GlobalEnvironmentRecord global)
            {
                var result = global.CreateGlobalVarBinding(name, deletable: isEval);
                if (result != BindingOpResult.Ok)
                {
                    ThrowTypeError(frame, $"Cannot declare global var binding '{name}'.");
                    return;
                }

                continue;
            }

            if (frame.Environment.HasBinding(name))
            {
                continue;
            }

            var create = frame.Environment.CreateMutableBinding(name, deletable: isEval);
            if (create != BindingOpResult.Ok)
            {
                ThrowTypeError(frame, $"Cannot declare var binding '{name}'.");
                return;
            }

            var init = frame.Environment.InitializeBinding(name, JsValue.Undefined);
            if (init != BindingOpResult.Ok)
            {
                ThrowTypeError(frame, $"Cannot initialize var binding '{name}'.");
                return;
            }
        }
    }


    private void InstantiateLexicalDeclarations(BytecodeFunction function, InterpreterFrame frame)
    {
        foreach (var name in function.LexicalDeclarationNames)
        {
            var create = frame.Environment.CreateMutableBinding(name, deletable: false);
            if (create != BindingOpResult.Ok)
            {
                ThrowTypeError(frame, $"Cannot declare lexical binding '{name}'.");
                return;
            }
        }

        foreach (var name in function.ConstDeclarationNames)
        {
            var create = frame.Environment.CreateImmutableBinding(name, strict: true);
            if (create != BindingOpResult.Ok)
            {
                ThrowTypeError(frame, $"Cannot declare const binding '{name}'.");
                return;
            }
        }
    }


    private ObjectHandle EnsureGlobalObject()
    {
        if (_globalObjectHandle is { } existing)
        {
            return existing;
        }

        var global = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(global, AllocationSite.Current());
        _heap.PushRoot(handle);
        _globalObjectHandle = handle;
        InstallGlobalObjectProperties(global, handle);
        return handle;
    }


    internal void EnterScopeForJit(InterpreterFrame frame, int slotNameIndex, int isConst)
    {
        var newScope = new DeclarativeEnvironmentRecord(frame.Environment);
        var scopeName = SlotNameTable.GetName(frame.Function, slotNameIndex);
        if (scopeName != null)
        {
            if (isConst == 1)
                _ = newScope.CreateImmutableBinding(scopeName, strict: true);
            else
                _ = newScope.CreateMutableBinding(scopeName, deletable: true);
        }
        frame.Environment = newScope;
    }


    internal void LeaveScopeForJit(InterpreterFrame frame)
    {
        frame.Environment = frame.Environment.OuterEnv ?? frame.Environment;
    }

}


