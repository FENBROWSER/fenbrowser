using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Objects;
using FenBrowser.Js.Runtime;

namespace FenBrowser.Js.Interpreter;

// Class/private-field/super helpers extracted from BytecodeInterpreter.cs as part of
// audit section 2 slice 5. Pure file move, no semantic change.
public sealed partial class BytecodeInterpreter
{

    internal void DefinePrivateFieldForJit(InterpreterFrame frame, int targetReg, int nameIndex, int valueReg)
    {
        var function = frame.Function;
        var target = frame.Registers[targetReg];
        var name = function.PropertyNames[nameIndex];
        var value = frame.Registers[valueReg];
        if (target.Tag != JsValueTag.Object)
            throw new JsThrownException(CreateTypeError("Cannot define private field on non-object."));
        var targetObj = _heap.GetObject(target.AsObjectHandle());
        var brand = function.BrandTokens.Count > 0 ? function.BrandTokens[0] : 0L;
        targetObj.PrivateBrand = targetObj.PrivateBrand != 0 ? targetObj.PrivateBrand : brand;
        targetObj.DefineOwnProperty(name, new JsPropertyDescriptor(value, Writable: true, Enumerable: false, Configurable: false));
    }


    internal void GetPrivateFieldForJit(InterpreterFrame frame, int destReg, int objReg, int nameIndex)
    {
        var function = frame.Function;
        var objVal = frame.Registers[objReg];
        var name = function.PropertyNames[nameIndex];
        if (objVal.Tag != JsValueTag.Object)
            throw new JsThrownException(CreateTypeError("Cannot read private field from non-object."));
        var obj = _heap.GetObject(objVal.AsObjectHandle());
        var brand = function.BrandTokens.Count > 0 ? function.BrandTokens[0] : 0L;
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
        if (objVal.Tag != JsValueTag.Object)
            throw new JsThrownException(CreateTypeError("Cannot write private field to non-object."));
        var obj = _heap.GetObject(objVal.AsObjectHandle());
        var brand = function.BrandTokens.Count > 0 ? function.BrandTokens[0] : 0L;
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


    internal void HandleDefineAccessor(InterpreterFrame frame, BytecodeFunction function, Instruction ins)
    {
        // H.4 - install or update an accessor descriptor on the target object
        // under the given property name. Preserves the companion half (get/set)
        // if an accessor descriptor already exists for the key, per ECMA-262
        // 6.2.5.6 CompletePropertyDescriptor.
        var targetValue = frame.Registers[ins.A];
        if (targetValue.Tag != JsValueTag.Object)
        {
            ThrowOrHandle(frame, CreateTypeError("DefineGetter/Setter target must be an object."));
            return;
        }

        var targetHandle = targetValue.AsObjectHandle();
        var targetObj = _heap.GetObject(targetHandle);
        var accessorName = function.PropertyNames[ins.B];
        var accessorFnValue = frame.Registers[ins.C];

        JsValue getValue = JsValue.Undefined;
        JsValue setValue = JsValue.Undefined;
        if (targetObj.TryGetOwnProperty(accessorName, out var existing) && existing.IsAccessor)
        {
            getValue = existing.Get;
            setValue = existing.Set;
        }

        if (ins.OpCode == OpCode.DefineGetter)
        {
            getValue = accessorFnValue;
        }
        else
        {
            setValue = accessorFnValue;
        }

        // D=1 (set by the object-literal compiler path) => enumerable accessor;
        // class accessors leave D=0 and stay non-enumerable.
        _ = targetObj.DefineOwnProperty(accessorName,
            Objects.JsPropertyDescriptor.Accessor(getValue, setValue, Enumerable: ins.D != 0, Configurable: true));
        if (accessorFnValue.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(targetHandle, accessorFnValue.AsObjectHandle());
        }
    }


    // H.5 - HandleDefineAccessorByReg: Like HandleDefineAccessor but the property
    // key is a JsValue in a register (for computed property names) instead of an
    // index into the constant pool.
    internal void HandleDefineAccessorByReg(InterpreterFrame frame, Instruction ins)
    {
        var targetValue = frame.Registers[ins.A];
        if (targetValue.Tag != JsValueTag.Object)
        {
            ThrowOrHandle(frame, CreateTypeError("DefineGetter/Setter target must be an object."));
            return;
        }

        var targetHandle = targetValue.AsObjectHandle();
        var targetObj = _heap.GetObject(targetHandle);
        var keyValue = frame.Registers[ins.B];
        var accessorFnValue = frame.Registers[ins.C];

        JsValue getValue = JsValue.Undefined;
        JsValue setValue = JsValue.Undefined;
        if (keyValue.Tag == JsValueTag.Symbol)
        {
            var symbolId = keyValue.AsSymbolId();
            if (targetObj.TryGetOwnSymbolProperty(symbolId, out var existingSymbol) && existingSymbol.IsAccessor)
            {
                getValue = existingSymbol.Get;
                setValue = existingSymbol.Set;
            }

            if (ins.OpCode == OpCode.DefineGetterByReg)
            {
                getValue = accessorFnValue;
            }
            else
            {
                setValue = accessorFnValue;
            }

            _ = targetObj.DefineOwnSymbolProperty(symbolId,
                Objects.JsPropertyDescriptor.Accessor(getValue, setValue, Enumerable: ins.D != 0, Configurable: true));
            if (accessorFnValue.Tag == JsValueTag.Object)
            {
                _heap.WriteBarrier(targetHandle, accessorFnValue.AsObjectHandle());
            }

            return;
        }

        var accessorName = ToPropertyKey(keyValue);
        if (targetObj.TryGetOwnProperty(accessorName, out var existing) && existing.IsAccessor)
        {
            getValue = existing.Get;
            setValue = existing.Set;
        }

        if (ins.OpCode == OpCode.DefineGetterByReg)
        {
            getValue = accessorFnValue;
        }
        else
        {
            setValue = accessorFnValue;
        }

        _ = targetObj.DefineOwnProperty(accessorName,
            Objects.JsPropertyDescriptor.Accessor(getValue, setValue, Enumerable: ins.D != 0, Configurable: true));
        if (accessorFnValue.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(targetHandle, accessorFnValue.AsObjectHandle());
        }
    }


    // ECMA-262 15.7.13 / 7.3.6 CreateMethodProperty: install a data property with
    // { [[Writable]]: true, [[Enumerable]]: false, [[Configurable]]: true }.
    internal void HandleDefineMethod(InterpreterFrame frame, BytecodeFunction function, Instruction ins)
    {
        var targetValue = frame.Registers[ins.A];
        if (targetValue.Tag != JsValueTag.Object)
        {
            ThrowOrHandle(frame, CreateTypeError("DefineMethod target must be an object."));
            return;
        }

        var targetHandle = targetValue.AsObjectHandle();
        var targetObj = _heap.GetObject(targetHandle);
        var name = function.PropertyNames[ins.B];
        var fnValue = frame.Registers[ins.C];

        _ = targetObj.DefineOwnProperty(name,
            new Objects.JsPropertyDescriptor(fnValue, Writable: true, Enumerable: false, Configurable: true));
        if (fnValue.Tag == JsValueTag.Object)
        {
            _heap.WriteBarrier(targetHandle, fnValue.AsObjectHandle());
        }
    }

    internal void HandleDefineMethodByReg(InterpreterFrame frame, Instruction ins)
    {
        var targetValue = frame.Registers[ins.A];
        if (targetValue.Tag != JsValueTag.Object)
        {
            ThrowOrHandle(frame, CreateTypeError("DefineMethod target must be an object."));
            return;
        }

        var targetHandle = targetValue.AsObjectHandle();
        var targetObj = _heap.GetObject(targetHandle);
        var keyValue = frame.Registers[ins.B];
        var fnValue = frame.Registers[ins.C];

        if (keyValue.Tag == JsValueTag.Symbol)
        {
            _ = targetObj.DefineOwnSymbolProperty(keyValue.AsSymbolId(),
                new Objects.JsPropertyDescriptor(fnValue, Writable: true, Enumerable: false, Configurable: true));
        }
        else
        {
            var name = ToPropertyKey(keyValue);
            _ = targetObj.DefineOwnProperty(name,
                new Objects.JsPropertyDescriptor(fnValue, Writable: true, Enumerable: false, Configurable: true));
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
            _heap.WriteBarrier(fnValue.AsObjectHandle(), homeValue.AsObjectHandle());
        }
    }


    internal void HandleLoadSuperProperty(InterpreterFrame frame, BytecodeFunction function, Instruction ins)
    {
        // ECMA-262 13.3.7.3 MakeSuperPropertyReference + 9.1.2 GetSuperBase.
        var name = function.PropertyNames[ins.B];
        if (frame.CalleeFunctionObject is not { } calleeFn || calleeFn.HomeObject is not { } home)
        {
            ThrowOrHandle(frame, CreateReferenceError("super reference requires a class method context."));
            return;
        }

        var homeObj = _heap.GetObject(home);
        if (homeObj.PrototypeHandle is not { } baseProtoHandle)
        {
            frame.Registers[ins.A] = JsValue.Undefined;
            return;
        }

        var baseProto = _heap.GetObject(baseProtoHandle);
        frame.Registers[ins.A] = TryGetPropertyValue(baseProto, JsValue.FromObject(baseProtoHandle), name, out var v)
            ? v
            : JsValue.Undefined;
    }


    internal void HandleLoadSuperElement(InterpreterFrame frame, Instruction ins)
    {
        // Computed super[key]. ECMA-262 13.3.7.3 MakeSuperPropertyReference +
        // 9.1.2 GetSuperBase. Same as LoadSuperProperty but the key comes from
        // a register and may be a Symbol or coercible to a string property key.
        if (frame.CalleeFunctionObject is not { } calleeFn || calleeFn.HomeObject is not { } home)
        {
            ThrowOrHandle(frame, CreateReferenceError("super reference requires a class method context."));
            return;
        }

        var homeObj = _heap.GetObject(home);
        if (homeObj.PrototypeHandle is not { } baseProtoHandle)
        {
            frame.Registers[ins.A] = JsValue.Undefined;
            return;
        }

        var baseProto = _heap.GetObject(baseProtoHandle);
        var keyValue = frame.Registers[ins.B];
        if (keyValue.Tag == JsValueTag.Symbol)
        {
            frame.Registers[ins.A] = GetReceiverSymbolProperty(JsValue.FromObject(baseProtoHandle), keyValue.AsSymbolId());
            return;
        }

        var name = ToPropertyKey(keyValue);
        frame.Registers[ins.A] = TryGetPropertyValue(baseProto, JsValue.FromObject(baseProtoHandle), name, out var v)
            ? v
            : JsValue.Undefined;
    }


    internal void HandleLoadSuperConstructor(InterpreterFrame frame, Instruction ins)
    {
        // ECMA-262 13.3.7.4 GetSuperConstructor: read the active function's
        // HomeObject (which the class compiler sets to the class itself for the
        // constructor), then return HomeObject.[[Prototype]] - the base class.
        if (frame.CalleeFunctionObject is not { } callee || callee.HomeObject is not { } home)
        {
            ThrowOrHandle(frame, CreateReferenceError("super constructor call requires a class constructor context."));
            return;
        }

        var homeObj = _heap.GetObject(home);
        if (homeObj.PrototypeHandle is not { } baseHandle)
        {
            ThrowOrHandle(frame, CreateTypeError("super constructor is not callable (no base class)."));
            return;
        }

        frame.Registers[ins.A] = JsValue.FromObject(baseHandle);
        frame.SuperConstructorHandle = baseHandle;
    }

}


