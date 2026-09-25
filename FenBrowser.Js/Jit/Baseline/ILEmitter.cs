using System.Reflection;
using System.Reflection.Emit;

#if !PUBLISH_AOT
namespace FenBrowser.Js.Jit.Baseline;

/// <summary>
/// Writes IL into a <see cref="DynamicMethod"/>. Nothing here knows what
/// JavaScript is; it exists so the emitter reads as the instruction stream it
/// produces rather than as reflection ceremony.
/// </summary>
internal readonly struct ILEmitter(ILGenerator il)
{
    private readonly ILGenerator _il = il;

    internal Label DefineLabel() => _il.DefineLabel();

    internal void Mark(Label label) => _il.MarkLabel(label);

    internal LocalBuilder Local(Type type) => _il.DeclareLocal(type);

    internal Label BeginTry() => _il.BeginExceptionBlock();

    internal void BeginCatch(Type exceptionType) => _il.BeginCatchBlock(exceptionType);

    /// <summary>Starts an exception filter; the exception object is on the stack.</summary>
    internal void BeginFilter() => _il.BeginExceptFilterBlock();

    /// <summary>Ends the filter (it must leave an int) and starts the handler it guards.</summary>
    internal void BeginFilteredCatch() => _il.BeginCatchBlock(null);

    internal void EndTry() => _il.EndExceptionBlock();

    /// <summary>The shortest encoding for a constant int.</summary>
    internal void Int(int value)
    {
        switch (value)
        {
            case -1: _il.Emit(OpCodes.Ldc_I4_M1); return;
            case 0: _il.Emit(OpCodes.Ldc_I4_0); return;
            case 1: _il.Emit(OpCodes.Ldc_I4_1); return;
            case 2: _il.Emit(OpCodes.Ldc_I4_2); return;
            case 3: _il.Emit(OpCodes.Ldc_I4_3); return;
            case 4: _il.Emit(OpCodes.Ldc_I4_4); return;
            case 5: _il.Emit(OpCodes.Ldc_I4_5); return;
            case 6: _il.Emit(OpCodes.Ldc_I4_6); return;
            case 7: _il.Emit(OpCodes.Ldc_I4_7); return;
            case 8: _il.Emit(OpCodes.Ldc_I4_8); return;
        }

        if (value is >= sbyte.MinValue and <= sbyte.MaxValue) _il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
        else _il.Emit(OpCodes.Ldc_I4, value);
    }

    internal void Double(double value) => _il.Emit(OpCodes.Ldc_R8, value);

    internal void Null() => _il.Emit(OpCodes.Ldnull);

    internal void Arg(int index)
    {
        switch (index)
        {
            case 0: _il.Emit(OpCodes.Ldarg_0); return;
            case 1: _il.Emit(OpCodes.Ldarg_1); return;
            case 2: _il.Emit(OpCodes.Ldarg_2); return;
            case 3: _il.Emit(OpCodes.Ldarg_3); return;
            default: _il.Emit(OpCodes.Ldarg_S, (byte)index); return;
        }
    }

    internal void Load(LocalBuilder local) => _il.Emit(OpCodes.Ldloc, local);

    internal void LoadAddress(LocalBuilder local) => _il.Emit(OpCodes.Ldloca, local);

    internal void Store(LocalBuilder local) => _il.Emit(OpCodes.Stloc, local);

    internal void LoadField(FieldInfo field) => _il.Emit(OpCodes.Ldfld, field);

    internal void StoreField(FieldInfo field) => _il.Emit(OpCodes.Stfld, field);

    internal void LoadElement(Type elementType) => _il.Emit(OpCodes.Ldelem, elementType);

    internal void LoadElementRef() => _il.Emit(OpCodes.Ldelem_Ref);

    internal void LoadElementAddress(Type elementType) => _il.Emit(OpCodes.Ldelema, elementType);

    internal void StoreElement(Type elementType) => _il.Emit(OpCodes.Stelem, elementType);

    internal void StoreObject(Type type) => _il.Emit(OpCodes.Stobj, type);

    internal void LoadObject(Type type) => _il.Emit(OpCodes.Ldobj, type);

    internal void Cast(Type type) => _il.Emit(OpCodes.Isinst, type);

    internal void New(ConstructorInfo constructor) => _il.Emit(OpCodes.Newobj, constructor);

    internal void Branch(OpCode branch, Label target) => _il.Emit(branch, target);

    internal void Op(OpCode op) => _il.Emit(op);

    /// <summary>
    /// Calls <paramref name="method"/> non-virtually wherever the target cannot
    /// be overridden, which is every runtime entry point a compiled body uses.
    /// </summary>
    internal void Call(MethodInfo method) =>
        _il.Emit(
            method.IsStatic || !method.IsVirtual || method.IsFinal || method.DeclaringType!.IsSealed
                ? OpCodes.Call
                : OpCodes.Callvirt,
            method);

    internal void Get(PropertyInfo property) => Call(property.GetMethod!);
}
#endif
