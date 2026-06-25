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

    private void ValidateDeclarationInstantiation(BytecodeFunction function, InterpreterFrame frame)
    {
        if (frame.Environment is not GlobalEnvironmentRecord global)
        {
            return;
        }

        foreach (var name in function.LexicalDeclarationNames.Concat(function.ConstDeclarationNames))
        {
            if (global.HasRestrictedGlobalProperty(name))
            {
                throw new JsThrownException(CreateSyntaxError(
                    $"Cannot declare global lexical binding '{name}' over an existing var binding."));
            }
        }

        foreach (var name in function.VarDeclarationNames)
        {
            if (global.HasLexicalDeclaration(name))
            {
                throw new JsThrownException(CreateSyntaxError(
                    $"Cannot declare global var binding '{name}' over an existing lexical binding."));
            }
        }
    }

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
                // ECMA-262: for eval code, a var declaration that conflicts
                // with an existing lexical binding must be a SyntaxError.
                if (isEval && frame.Environment is DeclarativeEnvironmentRecord declEnv &&
                    frame.Environment is not FunctionEnvironmentRecord &&
                    declEnv.HasLexicalBinding(name))
                    throw new JsThrownException(CreateSyntaxError(
                        $"Cannot declare var binding '{name}' — a lexical binding with that name already exists."));
                continue;
            }

            var create = frame.Environment.CreateMutableBinding(name, deletable: isEval);
            if (create != BindingOpResult.Ok)
            {
                // ECMA-262: for eval code, redeclaration of a lexical binding
                // must be a SyntaxError, not a TypeError (detected early).
                if (isEval)
                    throw new JsThrownException(CreateSyntaxError(
                        $"Cannot declare var binding '{name}' — a lexical binding with that name already exists."));
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
                // Duplicate lexical declarations are early errors (SyntaxError),
                // not TypeError. This path is hit when eval-introduced var
                // declarations shadow a body-level let/const.
                ThrowSyntaxError(frame, $"Cannot declare lexical binding '{name}'.");
                return;
            }
        }

        foreach (var name in function.ConstDeclarationNames)
        {
            var create = frame.Environment.CreateImmutableBinding(name, strict: true);
            if (create != BindingOpResult.Ok)
            {
                ThrowSyntaxError(frame, $"Cannot declare const binding '{name}'.");
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

    // ECMA-262 8.1.1.2.1 HasBinding step 5-6: when the binding object carries
    // @@unscopables and the requested name maps to a truthy value, the binding is
    // blocked for identifier resolution inside `with` statements.
    private bool IsBlockedByUnscopables(ObjectHandle handle, long unscopablesSymId, string name)
    {
        var obj = _heap.GetObject(handle);
        if (!obj.TryGetSymbolProperty(unscopablesSymId, h => _heap.GetObject(h), out var desc))
            return false;

        var unscopables = GetDescriptorValue(desc, JsValue.FromObject(handle));
        if (unscopables.Tag != JsValueTag.Object)
            return false;

        var unscopablesObj = _heap.GetObject(unscopables.AsObjectHandle());
        return TryGetPropertyValue(unscopablesObj, unscopables, name, out var blocked)
            && IsTruthy(blocked);
    }

}


