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

    // The environment a body's var and function declarations belong to. For
    // everything but eval code that is the frame's own environment. An eval's
    // frame environment is the fresh lexical environment PerformEval made for it
    // (ECMA-262 19.2.1.1); its variable environment is that record when the eval
    // is strict, and otherwise the caller's - the nearest one outward.
    private static EnvironmentRecord VariableEnvironmentFor(BytecodeFunction function, EnvironmentRecord environment)
        => function.IsEvalCode ? NearestVariableScope(environment) : environment;

    private static EnvironmentRecord NearestVariableScope(EnvironmentRecord environment)
    {
        for (EnvironmentRecord? env = environment; env is not null; env = env.OuterEnv)
        {
            if (env is GlobalEnvironmentRecord or FunctionEnvironmentRecord or ModuleEnvironmentRecord ||
                env is DeclarativeEnvironmentRecord { IsVariableScope: true })
            {
                return env;
            }
        }

        return environment;
    }

    private void ValidateDeclarationInstantiation(BytecodeFunction function, EnvironmentRecord environment)
    {
        // Lexical declarations live in the frame's own environment - for eval
        // code, the fresh one PerformEval made - so only a script's reach the
        // global record at all.
        if (environment is GlobalEnvironmentRecord lexicalGlobal)
        {
            foreach (var name in function.LexicalDeclarationNames.Concat(function.ConstDeclarationNames))
            {
                if (lexicalGlobal.HasRestrictedGlobalProperty(name))
                {
                    throw new JsThrownException(CreateSyntaxError(
                        $"Cannot declare global lexical binding '{name}' over an existing var binding."));
                }
            }
        }

        // A var belongs to the variable environment, which for a sloppy eval at
        // global level is the global record even though the eval's frame is not
        // (EvalDeclarationInstantiation step 3.a) - so `let x; eval('var x')`
        // there is still a SyntaxError.
        if (VariableEnvironmentFor(function, environment) is GlobalEnvironmentRecord varGlobal)
        {
            foreach (var name in function.VarDeclarationNames)
            {
                if (varGlobal.HasLexicalDeclaration(name))
                {
                    throw new JsThrownException(CreateSyntaxError(
                        $"Cannot declare global var binding '{name}' over an existing lexical binding."));
                }
            }
        }
    }

    private void InstantiateVarDeclarations(BytecodeFunction function, EnvironmentRecord environment)
    {
        // For eval code, Annex B B.3.3.3 requires var/function bindings to be
        // configurable (deletable) on the global scope so tests verifyProperty
        // with { configurable: true }.
        var isEval = function.IsEvalCode;
        var varEnv = VariableEnvironmentFor(function, environment);

        // A function body's own vars, hoisted into its own environment: the
        // slots are already known, so nothing here needs to touch a name.
        if (!isEval && environment is DeclarativeEnvironmentRecord slotted &&
            slotted.OwnsSlotsOf(function))
        {
            var slots = function.VarSlots;

            // The ordinary shape — every var has a slot — is the whole array in
            // one pass with the storage hoisted out of the loop.
            if (function.AllVarSlotsMapped &&
                slotted.TryDeclareHoistedVarsAtSlots(function, slots))
            {
                return;
            }

            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i] >= 0)
                {
                    slotted.DeclareAtSlot(slots[i], JsValue.Undefined, deletable: false, overwrite: false);
                }
                else
                {
                    _ = environment.EnsureVarBinding(function.VarDeclarationNames[i], deletable: false);
                }
            }

            return;
        }

        foreach (var name in function.VarDeclarationNames)
        {
            if (varEnv is GlobalEnvironmentRecord global)
            {
                var result = global.CreateGlobalVarBinding(name, deletable: isEval);
                if (result != BindingOpResult.Ok)
                {
                    throw new JsThrownException(CreateTypeError($"Cannot declare global var binding '{name}'."));
                }

                continue;
            }

            // The ordinary case - a function body's own vars, hoisted into a
            // fresh function environment - needs none of the eval checks below
            // and can be settled without touching the name at all when the
            // environment carries this function's slot numbering.
            if (!isEval)
            {

                var ensured = environment.EnsureVarBinding(name, deletable: false);
                if (ensured != BindingOpResult.Ok)
                {
                    throw new JsThrownException(CreateTypeError($"Cannot declare var binding '{name}'."));
                }

                continue;
            }

            // Eval code from here on. The binding goes in the variable
            // environment, and one already there is left as it is (step 16.b).
            // A conflict with a lexical binding between the eval and that
            // environment was already a SyntaxError in ValidateEvalDeclarations,
            // which is the one place that walks those scopes.
            if (varEnv.HasBinding(name))
            {
                continue;
            }

            var create = varEnv.CreateMutableBinding(name, deletable: isEval);
            if (create != BindingOpResult.Ok)
            {
                // ECMA-262: for eval code, redeclaration of a lexical binding
                // must be a SyntaxError, not a TypeError (detected early).
                if (isEval)
                    throw new JsThrownException(CreateSyntaxError(
                        $"Cannot declare var binding '{name}' — a lexical binding with that name already exists."));
                throw new JsThrownException(CreateTypeError($"Cannot declare var binding '{name}'."));
            }

            var init = varEnv.InitializeBinding(name, JsValue.Undefined);
            if (init != BindingOpResult.Ok)
            {
                throw new JsThrownException(CreateTypeError($"Cannot initialize var binding '{name}'."));
            }
        }
    }


    private void InstantiateLexicalDeclarations(BytecodeFunction function, EnvironmentRecord environment)
    {
        foreach (var name in function.LexicalDeclarationNames)
        {
            var create = environment.CreateMutableBinding(name, deletable: false);
            if (create != BindingOpResult.Ok)
            {
                // Duplicate lexical declarations are early errors (SyntaxError),
                // not TypeError. This path is hit when eval-introduced var
                // declarations shadow a body-level let/const.
                throw new JsThrownException(CreateSyntaxError($"Cannot declare lexical binding '{name}'."));
            }
        }

        foreach (var name in function.ConstDeclarationNames)
        {
            var create = environment.CreateImmutableBinding(name, strict: true);
            if (create != BindingOpResult.Ok)
            {
                throw new JsThrownException(CreateSyntaxError($"Cannot declare const binding '{name}'."));
            }
        }
    }

    /// <summary>
    /// Script, eval or synchronous module code: its declarations instantiated
    /// into <paramref name="environment"/> (ECMA-262 16.1.7, 19.2.1.3), then its
    /// body run on the register-window loop.
    /// </summary>
    private JsValue ExecuteProgram(BytecodeFunction function, JsValue thisValue, EnvironmentRecord environment)
    {
        var layout = Interp2LayoutFor(function);
        EnsureNativeStack();
        ValidateDeclarationInstantiation(function, environment);
        InstantiateVarDeclarations(function, environment);
        InstantiateLexicalDeclarations(function, environment);
        return Interp2Loop.ExecuteProgram(layout, environment, thisValue);
    }


    private ObjectHandle EnsureGlobalObject()
    {
        if (_globalObjectHandle is { } existing)
        {
            return existing;
        }

        // Installing the global object materialises the whole builtin graph
        // bottom-up: intrinsics are allocated and held in CLR locals before the
        // property store that links them into a rooted object, and any allocation
        // in between (a string intern, the next intrinsic) can collect. Pin every
        // cell allocated during the install; the window releases them once the
        // graph hangs off the rooted global.
        using var constructionWindow = _heap.BeginConstructionWindow();

        var global = CreateOrdinaryObject();
        var handle = _heap.AllocateObject(global, AllocationSite.Current());
        _heap.PushRoot(handle);
        _globalObjectHandle = handle;
        InstallGlobalObjectProperties(global, handle);
        return handle;
    }


    internal DeclarativeEnvironmentRecord CreateFunctionBodyScope(
        BytecodeFunction function, EnvironmentRecord parameterEnvironment)
    {
        var bodyEnvironment = StampEnvironment(new DeclarativeEnvironmentRecord(parameterEnvironment));
        bodyEnvironment.IsVariableScope = true;

        foreach (var name in function.BodyVarNames)
        {
            var initial = JsValue.Undefined;
            // The prologue's own vars are the names destructured parameters bind.
            var isParameterBinding = function.ParameterNames.Contains(name, StringComparer.Ordinal) ||
                function.VarDeclarationNames.Contains(name) ||
                (function.HasOwnArgumentsObject && string.Equals(name, "arguments", StringComparison.Ordinal));
            if (isParameterBinding &&
                parameterEnvironment.HasBinding(name) &&
                parameterEnvironment.GetBindingValue(name, strict: false, out var parameterValue) == BindingOpResult.Ok)
            {
                initial = parameterValue;
            }

            _ = bodyEnvironment.CreateMutableBinding(name, deletable: false);
            _ = bodyEnvironment.InitializeBinding(name, initial);
        }

        foreach (var name in function.BodyLexicalNames)
        {
            _ = bodyEnvironment.CreateMutableBinding(name, deletable: false);
        }

        foreach (var name in function.BodyConstNames)
        {
            _ = bodyEnvironment.CreateImmutableBinding(name, strict: true);
        }

        return bodyEnvironment;
    }

    /// <summary>
    /// ECMA-262 14.11.2 `with (value)`: ToObject(value), then an object record
    /// over <paramref name="outer"/> whose names are the object's properties,
    /// less those its @@unscopables blocks.
    /// </summary>
    internal ObjectEnvironmentRecord CreateWithEnvironment(JsValue value, EnvironmentRecord? outer)
    {
        var bindingHandle = ToObjectValue(value).AsObjectHandle();
        var adapter = CreateBindingAdapter(bindingHandle);
        var withEnv = StampEnvironment(new ObjectEnvironmentRecord(adapter, isWithEnvironment: true, outer));
        var unscopablesSymId = GetWellKnownSymbolId("unscopables");
        if (unscopablesSymId != 0)
        {
            withEnv.IsUnscopable = name => IsBlockedByUnscopables(bindingHandle, unscopablesSymId, name);
        }

        return withEnv;
    }

    internal void EnterScopeForJit(InterpreterFrame frame, int slotNameIndex, int isConst, int isCatch)
    {
        var newScope = StampEnvironment(new DeclarativeEnvironmentRecord(frame.Environment));
        var scopeName = SlotNameTable.GetName(frame.Function, slotNameIndex);
        if (scopeName != null)
        {
            // Not deletable, per ECMA-262 14.2.3 - see the EnterScope case in the
            // dispatch loop, which this must match exactly.
            if (isConst == 1)
                _ = newScope.CreateImmutableBinding(scopeName, strict: true);
            else
                _ = newScope.CreateMutableBinding(scopeName, deletable: false);
        }
        newScope.IsCatchScope = isCatch == 1;
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
        if (!obj.TryGetSymbolProperty(unscopablesSymId, ResolvePrototypeDelegate, out var desc))
            return false;

        var unscopables = GetDescriptorValue(desc, JsValue.FromObject(handle));
        if (unscopables.Tag != JsValueTag.Object)
            return false;

        var unscopablesObj = _heap.GetObject(unscopables.AsObjectHandle());
        return TryGetPropertyValue(unscopablesObj, unscopables, name, out var blocked)
            && IsTruthy(blocked);
    }

}


