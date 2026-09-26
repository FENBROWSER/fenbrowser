using System.Reflection;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;

#if !PUBLISH_AOT
namespace FenBrowser.Js.Jit.Baseline;

/// <summary>
/// Every runtime member a compiled body can reach, resolved once.
/// </summary>
/// <remarks>
/// Both tiers of codegen bind against this table, so the set of entry points a
/// compiled body may call is stated in one place rather than rediscovered by
/// each emitter.
/// </remarks>
internal static class JsRuntimeBindings
{
    internal static readonly MethodInfo MiLoadName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.LoadName), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiStoreName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.StoreName), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiInitializeName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.InitializeName), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiPreResolveBinding = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.PreResolveBinding), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiStoreToResolvedBinding = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.StoreToResolvedBinding), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiIsTruthy = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.IsTruthy), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiLoadThis = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.LoadThisForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiNewObject = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.NewObjectForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiNewArray = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.NewArrayForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiInitThisBinding = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.InitThisBindingForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiSuperCall = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SuperCallForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiSuperCallSpread = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SuperCallSpreadForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiEnterScope = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.EnterScopeForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiLeaveScope = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.LeaveScopeForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiCreatePerIterationEnvironment = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CreatePerIterationEnvironment), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly FieldInfo FiValueTag = typeof(JsValue).GetField(nameof(JsValue.Tag))!;
    internal static readonly MethodInfo MiAsNumber =
        typeof(JsValue).GetMethod(nameof(JsValue.AsNumber), Type.EmptyTypes)!;
    internal static readonly MethodInfo MiAsInt32 =
        typeof(JsValue).GetMethod(nameof(JsValue.AsInt32), Type.EmptyTypes)!;
    internal static readonly MethodInfo MiFromBoolean =
        typeof(JsValue).GetMethod(nameof(JsValue.FromBoolean), new[] { typeof(bool) })!;
    internal static readonly MethodInfo MiFromInt32 =
        typeof(JsValue).GetMethod(nameof(JsValue.FromInt32), new[] { typeof(int) })!;
    internal static readonly MethodInfo MiFromNumber =
        typeof(JsValue).GetMethod(nameof(JsValue.FromNumber), new[] { typeof(double) })!;
    internal static readonly MethodInfo MiAsBoolean =
        typeof(JsValue).GetMethod(nameof(JsValue.AsBoolean), Type.EmptyTypes)!;
    internal static readonly MethodInfo MiFastNumberResult = typeof(BytecodeInterpreter)
        .GetMethod("FastNumberResult", BindingFlags.Static | BindingFlags.NonPublic,
            null, new[] { typeof(double) }, null)!;

    internal static readonly FieldInfo FiThrowRouted =
        typeof(InterpreterFrame).GetField(nameof(InterpreterFrame.ThrowRoutedToHandler))!;

    internal static readonly MethodInfo MiEndFinally = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.EndFinallyForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiTypeOfName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.TypeOfName), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiJsValueFromString = typeof(JsValue)
        .GetMethod(nameof(JsValue.FromString), BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) })!;
    internal static readonly MethodInfo MiCreateFunctionFromNested = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CreateFunctionFromNestedForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiNewRegExp = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.NewRegExpForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiThrow = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.ThrowForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiGetPropByName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.GetPropByNameForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiGetPropByNameDirect = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.GetPropByNameForJit_Direct), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiTryLoadPropertyCached = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.TryLoadPropertyCached), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiLoadPropertyMiss = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.LoadPropertyMiss), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiSetPropByName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SetPropByNameForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiTryStorePropertyCached = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.TryStorePropertyCached), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiStorePropertyMiss = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.StorePropertyMiss), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiSetPropByNameDirect = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SetPropByNameForJit_Direct), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiDeletePropByName = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.DeletePropByNameForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiGetElem = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.GetElemForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiSetElem = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SetElemForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiSetElemByIndex = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SetElemByIndexForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiGetElemConst = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.GetElemConstForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiDeleteElem = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.DeleteElemForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiCall0 = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.Call0ForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiCall1 = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.Call1ForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiCallN = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CallNForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiCallMethod0 = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CallMethod0ForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiCallMethod1 = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CallMethod1ForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiCallMethodN = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CallMethodNForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiConstruct0 = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.Construct0ForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiConstruct1 = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.Construct1ForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiConstructN = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.ConstructNForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly PropertyInfo PiCatchHandlers =
        typeof(InterpreterFrame).GetProperty("CatchHandlers")!;

    internal static readonly PropertyInfo PiFinallyHandlers =
        typeof(InterpreterFrame).GetProperty("FinallyHandlers")!;

    internal static readonly PropertyInfo PiHandlerEnvironments =
        typeof(InterpreterFrame).GetProperty("HandlerEnvironments")!;

    internal static readonly PropertyInfo PiFrameEnvironment =
        typeof(InterpreterFrame).GetProperty("Environment")!;

    internal static readonly PropertyInfo PiFrameInstructionPointer =
        typeof(InterpreterFrame).GetProperty("InstructionPointer")!;

    internal static readonly MethodInfo MiIntStackPush = typeof(Stack<int>).GetMethod("Push")!;
    internal static readonly MethodInfo MiIntStackPop = typeof(Stack<int>).GetMethod("Pop")!;
    internal static readonly PropertyInfo PiIntStackCount = typeof(Stack<int>).GetProperty("Count")!;

    internal static readonly MethodInfo MiEnvStackPush =
        typeof(Stack<FenBrowser.Js.Environments.EnvironmentRecord>).GetMethod("Push")!;
    internal static readonly MethodInfo MiEnvStackPop =
        typeof(Stack<FenBrowser.Js.Environments.EnvironmentRecord>).GetMethod("Pop")!;
    internal static readonly PropertyInfo PiEnvStackCount =
        typeof(Stack<FenBrowser.Js.Environments.EnvironmentRecord>).GetProperty("Count")!;

    internal static readonly ConstructorInfo CtorJsThrown =
        typeof(JsThrownException).GetConstructor(new[] { typeof(JsValue) })!;

    internal static readonly PropertyInfo PiThrownValue =
        typeof(JsThrownException).GetProperty("Value")!;

    internal static readonly PropertyInfo PiThrownUncatchable =
        typeof(JsThrownException).GetProperty(
            nameof(JsThrownException.IsUncatchableByScript), BindingFlags.Instance | BindingFlags.Public)!;

    internal static readonly MethodInfo MiRouteThrow = typeof(BytecodeInterpreter)
        .GetMethod("TryRouteThrowForJit", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly MethodInfo MiApplyBinop = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.ApplyBinopForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiApplyUnaryOp = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.ApplyUnaryOpForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiInOp = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.InForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiInstanceOfOp = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.InstanceOfForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiDeleteOp = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.DeleteForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiSetPrototypeOp = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SetPrototypeForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiEnumerateKeys = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.EnumerateKeysForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiEnumerateValues = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.EnumerateValuesForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiForOfNext = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.ForOfNextForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiIteratorClose = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.IteratorCloseForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiForInNext = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.ForInNextForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiHasHandler = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.HasHandlerForJit), BindingFlags.Static | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiRequestTailCall = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.RequestTailCallForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiDefineLiteralProperty = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.DefineLiteralPropertyForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiDefinePrivateField = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.DefinePrivateFieldForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiGetPrivateField = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.GetPrivateFieldForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiSetPrivateField = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.SetPrivateFieldForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiCallSpread = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CallSpreadForJit), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiHandleDefineAccessor = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.HandleDefineAccessor), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiHandleDefineAccessorByReg = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.HandleDefineAccessorByReg), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiHandleSetHomeObject = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.HandleSetHomeObject), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiHandleLoadSuperProperty = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.HandleLoadSuperProperty), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiHandleLoadSuperElement = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.HandleLoadSuperElement), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiHandleLoadSuperConstructor = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.HandleLoadSuperConstructor), BindingFlags.Instance | BindingFlags.NonPublic)!;
    internal static readonly MethodInfo MiSlotBindingsFor =
        typeof(FenBrowser.Js.Environments.DeclarativeEnvironmentRecord)
            .GetMethod("SlotBindingsFor", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly Type BindingArrayType =
        typeof(FenBrowser.Js.Environments.DeclarativeEnvironmentRecord)
            .GetMethod("SlotBindingsFor", BindingFlags.Instance | BindingFlags.NonPublic)!.ReturnType;

    internal static readonly PropertyInfo PiBindingValue =
        BindingArrayType.GetElementType()!.GetProperty("Value")!;

    internal static readonly PropertyInfo PiBindingInitialized =
        BindingArrayType.GetElementType()!.GetProperty("IsInitialized")!;
    internal static readonly ConstructorInfo CiBinding =
        BindingArrayType.GetElementType()!.GetConstructors()[0]!;
    internal static readonly PropertyInfo PiBindingMutable =
        BindingArrayType.GetElementType()!.GetProperty("IsMutable")!;
    internal static readonly PropertyInfo PiBindingStrict =
        BindingArrayType.GetElementType()!.GetProperty("IsStrict")!;
    internal static readonly PropertyInfo PiBindingDeletable =
        BindingArrayType.GetElementType()!.GetProperty("IsDeletable")!;

    internal static readonly MethodInfo MiLoadSlotFast = typeof(BytecodeInterpreter)
        .GetMethod("LoadSlotFast", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly MethodInfo MiStoreSlotFast = typeof(BytecodeInterpreter)
        .GetMethod("StoreSlotFast", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly MethodInfo MiCheckExecutionBudgetCharged = typeof(BytecodeInterpreter)
        .GetMethod("CheckExecutionBudgetForJit", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(int) }, null)!;

    internal static readonly MethodInfo MiCheckExecutionBudget = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CheckExecutionBudgetForJit),
            BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!;
    internal static readonly PropertyInfo PiRegisters = typeof(InterpreterFrame).GetProperty(nameof(InterpreterFrame.Registers))!;
    internal static readonly PropertyInfo PiFunction = typeof(InterpreterFrame).GetProperty(nameof(InterpreterFrame.Function))!;
    internal static readonly PropertyInfo PiConstants = typeof(BytecodeFunction).GetProperty(nameof(BytecodeFunction.Constants))!;
    internal static readonly PropertyInfo PiThisValue = typeof(InterpreterFrame).GetProperty(nameof(InterpreterFrame.ThisValue))!;
    internal static readonly PropertyInfo PiNewTarget = typeof(InterpreterFrame).GetProperty(nameof(InterpreterFrame.NewTarget))!;

    internal static readonly PropertyInfo PiJsValueUndefined =
        typeof(JsValue).GetProperty(nameof(JsValue.Undefined))!;

    internal static readonly ConstructorInfo CtorInstruction =
        typeof(Instruction).GetConstructor(
            [typeof(OpCode), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int)])!;

    internal static readonly FieldInfo FiBaselinePool = typeof(BytecodeFunction)
        .GetField("BaselinePool", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly FieldInfo FiPoolConstants = typeof(BaselinePool)
        .GetField(nameof(BaselinePool.Constants), BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly FieldInfo FiPoolNames = typeof(BaselinePool)
        .GetField(nameof(BaselinePool.Names), BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly FieldInfo FiPoolSites = typeof(BaselinePool)
        .GetField(nameof(BaselinePool.Sites), BindingFlags.Instance | BindingFlags.NonPublic)!;

    // The pieces a compiled body needs to run a cache program's guards itself.
    // Each is small enough for RyuJIT to inline, which is the whole point of
    // emitting the guard rather than calling something that performs it.
    internal static readonly MethodInfo MiCacheableLoadReceiver = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CacheableLoadReceiver), BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly MethodInfo MiCacheableStoreReceiver = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CacheableStoreReceiver), BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly MethodInfo MiCachedStoreBarrier = typeof(BytecodeInterpreter)
        .GetMethod(nameof(BytecodeInterpreter.CachedStoreBarrier), BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly MethodInfo MiMarkPrototypeAssignment = typeof(BytecodeInterpreter)
        .GetMethod("MarkFunctionInstancePrototypeAssignment", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly PropertyInfo PiSiteFirst = typeof(FenBrowser.Js.Jit.CacheIR.CacheIRSite)
        .GetProperty("First", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly PropertyInfo PiInlineLoadShape = typeof(FenBrowser.Js.Jit.CacheIR.CacheIRProgram)
        .GetProperty("InlineLoadShape", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly PropertyInfo PiInlineStoreShape = typeof(FenBrowser.Js.Jit.CacheIR.CacheIRProgram)
        .GetProperty("InlineStoreShape", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly PropertyInfo PiResultSlot = typeof(FenBrowser.Js.Jit.CacheIR.CacheIRProgram)
        .GetProperty("ResultSlot", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly PropertyInfo PiObjectShape = typeof(FenBrowser.Js.Objects.JsObject)
        .GetProperty("CurrentShape", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly MethodInfo MiTryReadDataSlot = typeof(FenBrowser.Js.Objects.JsObject)
        .GetMethod("TryReadDataSlot", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly MethodInfo MiIsWritableDataSlot = typeof(FenBrowser.Js.Objects.JsObject)
        .GetMethod("IsWritableDataSlot", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static readonly MethodInfo MiWriteDataSlot = typeof(FenBrowser.Js.Objects.JsObject)
        .GetMethod("WriteDataSlot", BindingFlags.Instance | BindingFlags.NonPublic)!;
}
#endif
