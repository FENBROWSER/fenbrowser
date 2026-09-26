using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Environments;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// Class/private-field/super helpers extracted from BytecodeInterpreter.cs as part of
// audit section 2 slice 5. Pure file move, no semantic change.
public sealed partial class BytecodeInterpreter
{

    // SetPropByName with D=1: an object literal's own property, created rather
    // than assigned (ECMA-262 13.2.5.5), exactly as the dispatch loop does it.
    // The operands come in as values: SetPropByName is not a safepoint, so the
    // frame's registers may be behind the compiled code's locals.
    internal void DefineLiteralPropertyForJit(InterpreterFrame frame, JsValue target, int nameIndex, JsValue value)
    {
        DefineOwnDataProperty(target, frame.Function.PropertyNames[nameIndex], value);
    }

    internal void DefinePrivateFieldForJit(InterpreterFrame frame, int targetReg, int nameIndex, int valueReg)
    {
        var function = frame.Function;
        var target = frame.Registers[targetReg];
        var name = function.PropertyNames[nameIndex];
        var value = frame.Registers[valueReg];
        var brand = function.BrandTokens.Count > 0 ? function.BrandTokens[0] : 0L;
        if (target.Tag == JsValueTag.HostObject)
        {
            DefineHostPrivateField(target, name, value, brand);
            return;
        }
        if (target.Tag != JsValueTag.Object)
            throw new JsThrownException(CreateTypeError("Cannot define private field on non-object."));
        var targetObj = _heap.GetObject(target.AsObjectHandle());
        targetObj.PrivateBrand = targetObj.PrivateBrand != 0 ? targetObj.PrivateBrand : brand;
        targetObj.DefineOwnProperty(name, new JsPropertyDescriptor(value, Writable: true, Enumerable: false, Configurable: false));
    }


    internal void GetPrivateFieldForJit(InterpreterFrame frame, int destReg, int objReg, int nameIndex)
    {
        var function = frame.Function;
        var objVal = frame.Registers[objReg];
        var name = function.PropertyNames[nameIndex];
        var brand = function.BrandTokens.Count > 0 ? function.BrandTokens[0] : 0L;
        if (objVal.Tag == JsValueTag.HostObject)
        {
            if (!TryGetHostPrivateField(objVal, name, brand, out var hostPrivateValue))
                throw new JsThrownException(CreateTypeError("Cannot read private field from an object whose class did not declare it."));
            frame.Registers[destReg] = hostPrivateValue;
            return;
        }
        if (objVal.Tag != JsValueTag.Object)
            throw new JsThrownException(CreateTypeError("Cannot read private field from non-object."));
        var obj = _heap.GetObject(objVal.AsObjectHandle());
        if (obj.PrivateBrand == 0 || obj.PrivateBrand != brand)
            throw new JsThrownException(CreateTypeError("Cannot read private field from an object whose class did not declare it."));
        if (!TryGetPropertyValue(obj, objVal, name, out var privateValue))
            throw new JsThrownException(CreateTypeError("Cannot read private field from an object whose class did not declare it."));
        frame.Registers[destReg] = privateValue;
    }


    internal void SetPrivateFieldForJit(InterpreterFrame frame, int objReg, int nameIndex, int valueReg)
    {
        var function = frame.Function;
        var objVal = frame.Registers[objReg];
        var name = function.PropertyNames[nameIndex];
        var value = frame.Registers[valueReg];
        var brand = function.BrandTokens.Count > 0 ? function.BrandTokens[0] : 0L;
        if (objVal.Tag == JsValueTag.HostObject)
        {
            if (!TrySetHostPrivateField(objVal, name, value, brand))
                throw new JsThrownException(CreateTypeError("Cannot write private field to an object whose class did not declare it."));
            return;
        }
        if (objVal.Tag != JsValueTag.Object)
            throw new JsThrownException(CreateTypeError("Cannot write private field to non-object."));
        var obj = _heap.GetObject(objVal.AsObjectHandle());
        if (obj.PrivateBrand == 0 || obj.PrivateBrand != brand)
            throw new JsThrownException(CreateTypeError("Cannot write private field to an object whose class did not declare it."));
        WritePrivateField(obj, objVal, name, value);
    }

    // ECMA-262 PrivateSet: write a private field/accessor after the brand check has
    // passed. A private *field* is an own data property (set in place). A private
    // *setter* lives on the prototype as an accessor — invoke it with the receiver.
    // A getter-only private accessor (no setter) is a TypeError per the spec.
    private void WritePrivateField(JsObject obj, JsValue receiver, string name, JsValue value)
    {
        if (obj.TryGetOwnProperty(name, out var own) && !own.IsAccessor)
        {
            obj.DefineOwnProperty(name, own with { Value = value });
            return;
        }

        if (own.IsAccessor)
        {
            if (!CallSetter(own, value, receiver))
                throw new JsThrownException(CreateTypeError("Cannot write to a private accessor that has only a getter."));
            return;
        }

        if (TryGetPrototypePropertyDescriptor(obj, name, out var protoDesc) && protoDesc.IsAccessor)
        {
            if (!CallSetter(protoDesc, value, receiver))
                throw new JsThrownException(CreateTypeError("Cannot write to a private accessor that has only a getter."));
            return;
        }

        throw new JsThrownException(CreateTypeError("Cannot write private field to an object whose class did not declare it."));
    }


    // ECMA-262 10.2.9 SetFunctionName. Stamps an accessor/method's `name` own
    // property to "<prefix> <key>" (e.g. "get id", "set [test262]"). For a Symbol
    // key the base name is "" when the description is undefined, else "[desc]".
    // The prefix (and the joining space) is always present for get/set accessors.
    private void ApplyFunctionName(JsValue fnValue, JsValue keyValue, string? prefix)
    {
        if (fnValue.Tag != JsValueTag.Object)
        {
            return;
        }

        var fn = _heap.GetObject(fnValue.AsObjectHandle());
        if (fn is not JsFunctionObject)
        {
            return;
        }

        string baseName;
        if (keyValue.Tag == JsValueTag.Symbol)
        {
            var desc = keyValue.AsSymbolDescription();
            baseName = desc is null ? string.Empty : "[" + desc + "]";
        }
        else
        {
            baseName = ToPropertyKey(keyValue);
        }

        var full = prefix is null ? baseName : prefix + " " + baseName;
        _ = fn.DefineOwnProperty(
            "name",
            new JsPropertyDescriptor(JsValue.FromString(full), Writable: false, Enumerable: false, Configurable: true));
    }

    internal void HandleDefineAccessor(InterpreterFrame frame, BytecodeFunction function, Instruction ins)
    {
        try
        {
            DefineAccessorCore(
                frame.Registers[ins.A], function.PropertyNames[ins.B], JsValue.Undefined,
                frame.Registers[ins.C], ins.OpCode == OpCode.DefineGetter, enumerable: ins.D != 0);
        }
        catch (JsThrownException ex) when (HasHandler(frame))
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }


    // H.5 - HandleDefineAccessorByReg: Like HandleDefineAccessor but the property
    // key is a JsValue in a register (for computed property names) instead of an
    // index into the constant pool.
    internal void HandleDefineAccessorByReg(InterpreterFrame frame, Instruction ins)
    {
        try
        {
            DefineAccessorCore(
                frame.Registers[ins.A], name: null, frame.Registers[ins.B],
                frame.Registers[ins.C], ins.OpCode == OpCode.DefineGetterByReg, enumerable: ins.D != 0);
        }
        catch (JsThrownException ex) when (HasHandler(frame))
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }


    // ECMA-262 15.7.13 / 7.3.6 CreateMethodProperty: install a data property with
    // { [[Writable]]: true, [[Enumerable]]: false, [[Configurable]]: true }.
    internal void HandleDefineMethod(InterpreterFrame frame, BytecodeFunction function, Instruction ins)
    {
        try
        {
            DefineMethodCore(
                frame.Registers[ins.A], function.PropertyNames[ins.B], JsValue.Undefined, frame.Registers[ins.C]);
        }
        catch (JsThrownException ex) when (HasHandler(frame))
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }

    internal void HandleDefineMethodByReg(InterpreterFrame frame, Instruction ins)
    {
        try
        {
            DefineMethodCore(frame.Registers[ins.A], name: null, frame.Registers[ins.B], frame.Registers[ins.C]);
        }
        catch (JsThrownException ex) when (HasHandler(frame))
        {
            ThrowOrHandle(frame, ex.Value);
        }
    }

    // H.4 / H.5 - an accessor on an object literal or a class, by constant name
    // or by a key computed into a register. This core throws rather than routing
    // to a frame: the Handle* wrappers above catch and hand the exception to the
    // frame's handler stack, and the register-window loop calls the core from a
    // dispatch that is wrapped as a whole. The JIT binds the Handle* names by
    // reflection, which is why those stay as they are.
    //
    // The companion half of an existing accessor is preserved, per ECMA-262
    // 6.2.5.6 CompletePropertyDescriptor. `enumerable` is the compiler's D flag:
    // an object-literal accessor is enumerable, a class accessor is not.
    internal void DefineAccessorCore(
        JsValue targetValue, string? name, JsValue keyValue, JsValue accessorFnValue, bool isGetter, bool enumerable)
    {
        if (targetValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("DefineGetter/Setter target must be an object."));
        }

        var targetHandle = targetValue.AsObjectHandle();
        var targetObj = _heap.GetObject(targetHandle);
        var prefix = isGetter ? "get" : "set";
        JsValue getValue = JsValue.Undefined;
        JsValue setValue = JsValue.Undefined;

        if (name is null && keyValue.Tag == JsValueTag.Symbol)
        {
            var symbolId = keyValue.AsSymbolId();
            if (targetObj.TryGetOwnSymbolProperty(symbolId, out var existingSymbol) && existingSymbol.IsAccessor)
            {
                getValue = existingSymbol.Get;
                setValue = existingSymbol.Set;
            }

            if (isGetter) getValue = accessorFnValue;
            else setValue = accessorFnValue;
            ApplyFunctionName(accessorFnValue, keyValue, prefix);

            if (!targetObj.DefineOwnSymbolProperty(symbolId,
                    Objects.JsPropertyDescriptor.Accessor(getValue, setValue, Enumerable: enumerable, Configurable: true)))
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot define symbol-keyed accessor property on target object."));
            }
        }
        else
        {
            // Coerced once and passed on as a string: ApplyFunctionName would
            // coerce a key again, and a key's toString is user code that must
            // run exactly one time.
            var accessorName = name ?? ToPropertyKey(keyValue);
            if (targetObj.TryGetOwnProperty(accessorName, out var existing) && existing.IsAccessor)
            {
                getValue = existing.Get;
                setValue = existing.Set;
            }

            if (isGetter) getValue = accessorFnValue;
            else setValue = accessorFnValue;
            ApplyFunctionName(accessorFnValue, JsValue.FromString(accessorName), prefix);

            if (!targetObj.DefineOwnProperty(accessorName,
                    Objects.JsPropertyDescriptor.Accessor(getValue, setValue, Enumerable: enumerable, Configurable: true)))
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot define accessor property '" + accessorName + "' on target object."));
            }
        }

        if (accessorFnValue.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(targetHandle, accessorFnValue.AsObjectHandle());
        }
    }

    // ECMA-262 15.7.13 / 7.3.6 CreateMethodProperty: { [[Writable]]: true,
    // [[Enumerable]]: false, [[Configurable]]: true }. A method with a constant
    // name was named when its function was created; one with a computed key is
    // named here, from the key coerced exactly once - the error path used to
    // coerce it a second time just to build its message.
    internal void DefineMethodCore(JsValue targetValue, string? name, JsValue keyValue, JsValue fnValue)
    {
        if (targetValue.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("DefineMethod target must be an object."));
        }

        var targetHandle = targetValue.AsObjectHandle();
        var targetObj = _heap.GetObject(targetHandle);
        var descriptor = new Objects.JsPropertyDescriptor(fnValue, Writable: true, Enumerable: false, Configurable: true);

        if (name is null && keyValue.Tag == JsValueTag.Symbol)
        {
            // ECMA-262 SetFunctionName: a Symbol key renders as "[description]".
            ApplyFunctionName(fnValue, keyValue, prefix: null);
            if (!targetObj.DefineOwnSymbolProperty(keyValue.AsSymbolId(), descriptor))
            {
                throw new JsThrownException(CreateTypeError("Cannot define property 'symbol' on target object."));
            }
        }
        else
        {
            var propertyName = name;
            if (propertyName is null)
            {
                propertyName = ToPropertyKey(keyValue);
                ApplyFunctionName(fnValue, JsValue.FromString(propertyName), prefix: null);
            }

            if (!targetObj.DefineOwnProperty(propertyName, descriptor))
            {
                throw new JsThrownException(CreateTypeError(
                    "Cannot define property '" + propertyName + "' on target object."));
            }
        }

        if (fnValue.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(targetHandle, fnValue.AsObjectHandle());
        }
    }


    internal void HandleSetHomeObject(InterpreterFrame frame, Instruction ins)
    {
        var fnValue = frame.Registers[ins.A];
        var homeValue = frame.Registers[ins.B];
        if (fnValue.Tag != JsValueTag.Object || homeValue.Tag != JsValueTag.Object) return;
        if (_heap.GetObject(fnValue.AsObjectHandle()) is JsFunctionObject fn)
        {
            fn.HomeObject = homeValue.AsObjectHandle();
            fn.BarrierInternalSlot(homeValue.AsObjectHandle());
            _heap.WriteBarrier(fnValue.AsObjectHandle(), homeValue.AsObjectHandle());
        }
    }


    internal void HandleLoadSuperProperty(InterpreterFrame frame, BytecodeFunction function, Instruction ins)
    {
        // ECMA-262 13.3.7.3 MakeSuperPropertyReference + 9.1.2 GetSuperBase.
        var name = function.PropertyNames[ins.B];
        if (!TryGetSuperHome(frame, out var home))
        {
            ThrowOrHandle(frame, CreateReferenceError("super reference requires a class method context."));
            return;
        }

        // ECMA-262: GetThisBinding() throws ReferenceError when [[ThisBindingStatus]]
        // is "uninitialized" (derived constructor before super()).
        ValidateThisInitialized(frame);

        if (!TryGetSuperPropertyBase(function, home, out var baseProtoHandle))
        {
            ThrowOrHandle(frame, CreateTypeError("Cannot read properties of null."));
            return;
        }

        var baseProto = _heap.GetObject(baseProtoHandle);
        var receiver = frame.ThisValue;
        frame.Registers[ins.A] = TryGetPropertyValue(baseProto, receiver, name, out var v)
            ? v
            : JsValue.Undefined;
    }


    internal void HandleLoadSuperElement(InterpreterFrame frame, Instruction ins)
    {
        // Computed super[key]. ECMA-262 13.3.7.3 MakeSuperPropertyReference +
        // 9.1.2 GetSuperBase. Same as LoadSuperProperty but the key comes from
        // a register and may be a Symbol or coercible to a string property key.
        if (!TryGetSuperHome(frame, out var home))
        {
            ThrowOrHandle(frame, CreateReferenceError("super reference requires a class method context."));
            return;
        }

        // ECMA-262: GetThisBinding() throws ReferenceError when [[ThisBindingStatus]]
        // is "uninitialized" (derived constructor before super()).
        ValidateThisInitialized(frame);

        if (!TryGetSuperPropertyBase(frame.Function, home, out var baseProtoHandle))
        {
            ThrowOrHandle(frame, CreateTypeError("Cannot read properties of null."));
            return;
        }

        var receiver = frame.ThisValue;
        var baseProto = _heap.GetObject(baseProtoHandle);
        var keyValue = frame.Registers[ins.B];
        if (keyValue.Tag == JsValueTag.Symbol)
        {
            frame.Registers[ins.A] = baseProto.TryGetSymbolProperty(keyValue.AsSymbolId(), ResolvePrototypeDelegate, out var desc)
                ? GetDescriptorValue(desc, receiver)
                : JsValue.Undefined;
            return;
        }

        var name = ToPropertyKey(keyValue);
        frame.Registers[ins.A] = TryGetPropertyValue(baseProto, receiver, name, out var v)
            ? v
            : JsValue.Undefined;
    }


    internal void HandleLoadSuperConstructor(InterpreterFrame frame, Instruction ins)
    {
        // ECMA-262 13.3.7.4 GetSuperConstructor: return activeFunction.[[GetPrototypeOf]]().
        // That is the constructor function object's own prototype slot - the base
        // class - not anything reached through [[HomeObject]].
        // Arrow functions and eval inherit the enclosing method's binding.
        if (!TryGetActiveSuperFunction(frame, out var activeFunction))
        {
            ThrowOrHandle(frame, CreateReferenceError("super constructor call requires a class constructor context."));
            return;
        }

        // Null when the active function has no prototype: SuperCall reports it,
        // after the arguments have been evaluated.
        frame.Registers[ins.A] = _heap.GetObject(activeFunction).PrototypeHandle is { } baseHandle
            ? JsValue.FromObject(baseHandle)
            : JsValue.Null;
    }

    /// <summary>
    /// ECMA-262 13.3.7.2 GetSuperConstructor: the [[GetPrototypeOf]] of the
    /// function GetThisEnvironment finds - <paramref name="active"/> for the
    /// constructor itself, the one in <paramref name="lexicalEnvironment"/> for
    /// an arrow inside it. Null when that function has no prototype.
    /// </summary>
    internal JsValue GetSuperConstructor(JsFunctionObject? active, EnvironmentRecord? lexicalEnvironment)
    {
        JsObject? function = active;
        if (function is null)
        {
            for (var env = lexicalEnvironment; env is not null; env = env.OuterEnv)
            {
                if (!env.HasThisBinding)
                {
                    continue;
                }

                if (env is FunctionEnvironmentRecord { FunctionObject.Tag: JsValueTag.Object } record)
                {
                    function = _heap.GetObject(record.FunctionObject.AsObjectHandle());
                }

                break;
            }
        }

        if (function is null)
        {
            throw new JsThrownException(CreateSyntaxError("'super' keyword unexpected here."));
        }

        return function.PrototypeHandle is { } parent ? JsValue.FromObject(parent) : JsValue.Null;
    }

    /// <summary>
    /// SetPrototype: a class wiring its heritage (ECMA-262 15.7.14 steps 5-8), or
    /// with <paramref name="fromLiteral"/> an object literal's `__proto__: v`
    /// (B.3.1), where anything but an object or null is ignored.
    /// </summary>
    internal void SetPrototypeFromCode(JsValue child, JsValue parent, bool fromLiteral)
    {
        if (child.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("SetPrototype requires an object target."));
        }

        if (fromLiteral && parent.Tag != JsValueTag.Object && parent.Tag != JsValueTag.Null)
        {
            return;
        }

        var childHandle = child.AsObjectHandle();
        var childObject = _heap.GetObject(childHandle);
        if (parent.Tag == JsValueTag.Null)
        {
            childObject.SetPrototype(null);
        }
        else if (parent.Tag == JsValueTag.Object)
        {
            var parentHandle = parent.AsObjectHandle();
            childObject.SetPrototype(parentHandle);
            _heap.WriteBarrier(childHandle, parentHandle);
        }
        else
        {
            throw new JsThrownException(CreateTypeError("SetPrototype value must be Object or null."));
        }
    }

    /// <summary>ECMA-262 15.7.14 step 8.e: the heritage must be null or a constructor.</summary>
    internal void ValidateClassHeritage(JsValue heritage)
    {
        if (heritage.Tag != JsValueTag.Null &&
            (heritage.Tag != JsValueTag.Object || !IsConstructableTarget(heritage.AsObjectHandle())))
        {
            throw new JsThrownException(CreateTypeError("Class extends value is not a constructor or null."));
        }
    }

    /// <summary>
    /// ECMA-262 7.3.34 DefineField for a private name (PrivateFieldAdd): the
    /// field is added under the class's brand.
    /// </summary>
    internal void DefinePrivateField(BytecodeFunction function, JsValue target, string name, JsValue value)
    {
        var brand = function.BrandTokens.Count > 0 ? function.BrandTokens[0] : 0L;
        if (target.Tag == JsValueTag.HostObject)
        {
            DefineHostPrivateField(target, name, value, brand);
            return;
        }

        if (target.Tag != JsValueTag.Object)
        {
            throw new JsThrownException(CreateTypeError("Cannot define private field on non-object."));
        }

        var targetObject = _heap.GetObject(target.AsObjectHandle());
        targetObject.PrivateBrand = targetObject.PrivateBrand != 0 ? targetObject.PrivateBrand : brand;
        targetObject.DefineOwnProperty(name, new JsPropertyDescriptor(value, Writable: true, Enumerable: false, Configurable: false));
    }

    /// <summary>
    /// ECMA-262 15.7.10 step 27 ClassFieldDefinitionEvaluation: a computed field
    /// name is converted with ToPropertyKey when the class is defined, so its
    /// errors surface then, and kept on the constructor for LoadFieldKey.
    /// </summary>
    internal void StoreComputedFieldKey(JsValue rawKey, JsValue constructor)
    {
        var propertyKey = ToPropertyKey(rawKey);
        var keyValue = rawKey.Tag == JsValueTag.Symbol ? rawKey : JsValue.FromString(propertyKey);
        if (constructor.Tag == JsValueTag.Object &&
            _heap.GetObject(constructor.AsObjectHandle()) is JsFunctionObject constructorFunction)
        {
            constructorFunction.ComputedFieldKeys.Add(keyValue);
            constructorFunction.BarrierInternalSlot(keyValue);
        }
    }

    /// <summary>
    /// ECMA-262 13.3.7.1 SuperCall steps 4-6: IsConstructor, then Construct
    /// with the running function's new.target. Shared by both loops and
    /// compiled code.
    /// </summary>
    internal JsValue SuperConstruct(JsValue superConstructor, in CallArgs args, JsValue newTarget)
    {
        if (superConstructor.Tag != JsValueTag.Object || !IsConstructableTarget(superConstructor.AsObjectHandle()))
        {
            throw new JsThrownException(CreateTypeError("Super constructor is not a constructor."));
        }

        return ConstructFunction(superConstructor, args, newTarget);
    }

    /// <summary>
    /// ECMA-262 13.3.7.1 SuperCall steps 7-8: GetThisEnvironment().BindThisValue.
    /// The environment that binds `this` is the derived constructor's own, also
    /// when super() runs in an arrow inside it; binding it twice is a
    /// ReferenceError.
    /// </summary>
    internal void BindThisFromSuper(EnvironmentRecord? environment, JsValue value)
    {
        for (var env = environment; env is not null; env = env.OuterEnv)
        {
            if (!env.HasThisBinding)
            {
                continue;
            }

            if (env is FunctionEnvironmentRecord function &&
                function.BindThisValue(value) == BindingOpResult.AlreadyDeclared)
            {
                throw new JsThrownException(CreateReferenceError("super() called twice in derived class constructor."));
            }

            return;
        }
    }

    // Locates the function object that provides the current `super` binding, using
    // the same lookup order as TryGetSuperHome (own callee, then the environment
    // chain for arrows/eval, then enclosing frames) but yielding the function
    // itself rather than its [[HomeObject]].
    private bool TryGetActiveSuperFunction(InterpreterFrame currentFrame, out ObjectHandle function)
    {
        if (currentFrame.CalleeFunctionObject is { HomeObject: not null, SelfHandle: { } selfHandle })
        {
            function = selfHandle;
            return true;
        }

        var env = currentFrame.Environment;
        while (env is not null)
        {
            if (env is FunctionEnvironmentRecord { HomeObject: not null } fen &&
                fen.FunctionObject.Tag == JsValueTag.Object)
            {
                function = fen.FunctionObject.AsObjectHandle();
                return true;
            }

            env = env.OuterEnv;
        }

        var foundCurrent = false;
        foreach (var f in _activeFrames)
        {
            if (!foundCurrent)
            {
                if (ReferenceEquals(f, currentFrame)) foundCurrent = true;
                continue;
            }

            if (f.CalleeFunctionObject is { HomeObject: not null, SelfHandle: { } parentHandle })
            {
                function = parentHandle;
                return true;
            }
        }

        function = default;
        return false;
    }

    private bool TryGetSuperPropertyBase(BytecodeFunction function, ObjectHandle home, out ObjectHandle baseProtoHandle)
    {
        // ECMA-262 9.1.2 GetSuperBase: return env.[[HomeObject]].[[GetPrototypeOf]]().
        // The HomeObject is set differently for constructors (the class itself) vs
        // methods (the class prototype), so home.[[Prototype]] produces the correct
        // result for both: Base (constructor) or Base.prototype (method).
        var homeObj = _heap.GetObject(home);
        if (homeObj.PrototypeHandle is not { } homeProto)
        {
            baseProtoHandle = default;
            return false;
        }

        baseProtoHandle = homeProto;
        return true;
    }

    // Walks the environment chain to find a FunctionEnvironmentRecord with a
    // HomeObject. Arrow functions and eval inherit the enclosing method's super
    // binding per ECMA-262 9.1.2 GetSuperBase (GetThisEnvironment walks outer envs).
    // Falls back to the frame's CalleeFunctionObject.HomeObject and the frame stack.
    private bool TryGetSuperHome(InterpreterFrame currentFrame, out ObjectHandle home)
    {
        // First try the current frame's callee directly (for direct method calls).
        if (currentFrame.CalleeFunctionObject is { } callee && callee.HomeObject is { } calleeHome)
        {
            home = calleeHome;
            return true;
        }

        // Walk the environment chain (for arrow functions, eval, etc.).
        var env = currentFrame.Environment;
        while (env is not null)
        {
            if (env is FunctionEnvironmentRecord fen && fen.HomeObject is { } fenHome)
            {
                home = fenHome;
                return true;
            }
            env = env.OuterEnv;
        }

        // Fallback: walk the parent frame's CalleeFunctionObject.
        var foundCurrent = false;
        foreach (var f in _activeFrames)
        {
            if (!foundCurrent)
            {
                if (ReferenceEquals(f, currentFrame)) foundCurrent = true;
                continue;
            }
            if (f.CalleeFunctionObject is { } parentCallee && parentCallee.HomeObject is { } parentHome)
            {
                home = parentHome;
                return true;
            }
        }

        home = default;
        return false;
    }

    // ECMA-262 9.1.1.3.4 GetThisBinding: throws ReferenceError when
    // [[ThisBindingStatus]] is "uninitialized" (derived constructor before
    // super()). Walk up the environment chain to find the nearest
    // FunctionEnvironmentRecord (arrow functions/eval skip their own env).
    private void ValidateThisInitialized(InterpreterFrame frame)
    {
        var env = frame.Environment;
        while (env is not null)
        {
            if (env is FunctionEnvironmentRecord fen)
            {
                if (fen.ThisBindingStatus == ThisBindingStatus.Uninitialized)
                    throw new JsThrownException(CreateReferenceError(
                        "Must call super constructor before accessing 'this' or 'super'."));
                return;
            }
            env = env.OuterEnv;
        }
    }

}


